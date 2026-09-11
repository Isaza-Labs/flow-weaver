using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Scheduler;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowTriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.Services.WorkflowTrigger;

public class WorkflowTriggerService : IWorkflowTrigger
{
    private const string DefaultTimezone = "UTC";

    private static readonly Func<WorkflowTriggerModel, WorkflowTriggerResponse> ToResponse = t => new()
    {
        WorkflowTriggerId = t.WorkflowTriggerId,
        WorkflowId = t.WorkflowId,
        Name = t.Name,
        Type = t.Type,
        Description = t.Description,
        Route = t.Route,
        WebhookPath = IsWebhookType(t.Type)
            ? $"/api/webhooks/workflow/{t.WorkflowTriggerId}"
            : null,
        HasWebhookSecret = t.EncryptedSecret is { Length: > 0 },
        AllowUnsigned = t.AllowUnsigned,
        AllowTargetOverride = t.AllowTargetOverride,
        CronExpression = t.CronExpression,
        Timezone = t.Timezone,
        InputSchema = t.InputSchema,
        InputDefaults = t.InputDefaults,
        Enabled = t.Enabled,
        NextRunAt = t.NextRunAt,
        LastRunAt = t.LastRunAt,
        LastRunStatus = t.LastRunStatus,
        NotificationWebhookURL = t.NotificationWebhookURL,
        NotifyOn = t.NotifyOn,
        TargetDevices = t.TargetDevices,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };

    private readonly IWorkflowTriggerRepository _triggers;
    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ICurrentUser _caller;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IAuditLogger _audit;
    private readonly ILogger<WorkflowTriggerService> _logger;

    public WorkflowTriggerService(
        IWorkflowTriggerRepository triggers,
        IRepository<WorkflowModel> workflows,
        ICurrentUser caller,
        ICredentialEncryptionService crypto,
        IAuditLogger audit,
        ILogger<WorkflowTriggerService> logger)
    {
        _triggers = triggers;
        _workflows = workflows;
        _caller = caller;
        _crypto = crypto;
        _audit = audit;
        _logger = logger;
    }

    // Shared audit projection. A trigger is the thing that fires a workflow
    // with nobody watching, so cron/route/enabled/allow_unsigned are the
    // fields worth diffing. EncryptedSecret is never included.
    private static object TriggerAudit(WorkflowTriggerModel t) => new
    {
        t.Name,
        t.Type,
        t.Route,
        cron_expression = t.CronExpression,
        t.Timezone,
        t.Enabled,
        allow_unsigned = t.AllowUnsigned,
        allow_target_override = t.AllowTargetOverride,
        workflow_id = t.WorkflowId,
        target_device_count = t.TargetDevices?.Count ?? 0,
    };

    private static bool IsWebhookType(string? type) =>
        string.Equals(type, WorkflowTriggerModel.TypeWebhook, StringComparison.OrdinalIgnoreCase);

    // Random signing secret: 32 bytes of CSPRNG output as lowercase hex (64
    // chars, no special characters to escape in a webhook sender's config).
    private static string GenerateSecret() =>
        System.Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _triggers.CountAsync();
        var triggers = await _triggers.ListAsync(limit, offset);

        _logger.LogDebug(
            "workflow_trigger.list.ok total={Total} returned={Returned}",
            total, triggers.Count);

        return new OkObjectResult(new ListResponse<WorkflowTriggerResponse>
        {
            Data = triggers.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetByWorkflowAsync(
        Guid workflowId, int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var workflowExists = await _workflows.ExistsAsync(workflowId);
        if (!workflowExists)
        {
            _logger.LogWarning(
                "workflow_trigger.list.not_found workflow_id={WorkflowId} reason=parent_workflow_missing",
                workflowId);
            return new NotFoundObjectResult(new { error = "workflow not found" });
        }

        var total = await _triggers.CountByWorkflowAsync(workflowId);
        var triggers = await _triggers.ListByWorkflowAsync(workflowId, limit, offset);

        _logger.LogDebug(
            "workflow_trigger.list.ok workflow_id={WorkflowId} total={Total} returned={Returned}",
            workflowId, total, triggers.Count);

        return new OkObjectResult(new ListResponse<WorkflowTriggerResponse>
        {
            Data = triggers.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<WorkflowTriggerResponse>> GetByIdAsync(Guid id)
    {
        var trigger = await _triggers.GetByIdAsync(id);
        if (trigger is null)
        {
            _logger.LogWarning("workflow_trigger.get.not_found workflow_trigger_id={WorkflowTriggerId}", id);
            return new NotFoundObjectResult(new { error = "workflow_trigger not found" });
        }

        return ToResponse(trigger);
    }

    public Task<ActionResult<WorkflowTriggerResponse>> PostAsync(CreateWorkflowTrigger dto)
    {
        _logger.LogWarning("workflow_trigger.create.validation_failed reason=use_nested_route");
        return Task.FromResult<ActionResult<WorkflowTriggerResponse>>(
            new BadRequestObjectResult(new
            {
                error = "use POST /api/workflow/{workflowId}/triggers to create a trigger"
            }));
    }

    public async Task<ActionResult<WorkflowTriggerResponse>> PostForWorkflowAsync(
        Guid workflowId, CreateWorkflowTrigger dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("workflow_trigger.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Type))
        {
            _logger.LogWarning("workflow_trigger.create.validation_failed reason=type_required");
            return new BadRequestObjectResult(new { error = "type is required" });
        }

        if (IsScheduleType(dto.Type))
        {
            if (string.IsNullOrWhiteSpace(dto.CronExpression))
            {
                _logger.LogWarning(
                    "workflow_trigger.create.validation_failed reason=cron_expression_required type={TriggerType}",
                    dto.Type);
                return new BadRequestObjectResult(new { error = "cron_expression is required for schedule/cron triggers" });
            }
            if (CronSchedule.TryParse(dto.CronExpression, out var cronError) is null)
            {
                _logger.LogWarning(
                    "workflow_trigger.create.validation_failed reason=cron_expression_invalid type={TriggerType}",
                    dto.Type);
                return new BadRequestObjectResult(new { error = cronError });
            }
        }

        // Parent workflow must exist.
        var workflowExists = await _workflows.ExistsAsync(workflowId);
        if (!workflowExists)
        {
            _logger.LogWarning(
                "workflow_trigger.create.not_found workflow_id={WorkflowId} reason=parent_workflow_missing",
                workflowId);
            return new NotFoundObjectResult(new { error = "workflow not found" });
        }

        var now = DateTime.UtcNow;
        var trigger = new WorkflowTriggerModel
        {
            WorkflowTriggerId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Name = dto.Name,
            Type = dto.Type,
            Description = dto.Description,
            Route = dto.Route,
            CronExpression = dto.CronExpression,
            Timezone = string.IsNullOrWhiteSpace(dto.Timezone) ? DefaultTimezone : dto.Timezone,
            InputSchema = dto.InputSchema ?? default,
            InputDefaults = dto.InputDefaults ?? default,
            Enabled = dto.Enabled ?? true,
            AllowUnsigned = dto.AllowUnsigned ?? false,
            AllowTargetOverride = dto.AllowTargetOverride ?? false,
            NotificationWebhookURL = dto.NotificationWebhookURL,
            NotifyOn = dto.NotifyOn ?? new(),
            TargetDevices = dto.TargetDevices ?? new(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Stamp the first fire time so the scheduler's due-query picks it up.
        trigger.NextRunAt = ComputeNextRun(trigger);

        // Webhook triggers auto-generate a signing secret at create time, so the
        // public ingest is authenticated by default. The plaintext is surfaced
        // ONCE in this response and never again (only the encrypted form is
        // stored) — same one-time-reveal contract as an API key.
        string? plaintextSecret = null;
        if (IsWebhookType(trigger.Type) && !(dto.AllowUnsigned ?? false))
        {
            plaintextSecret = GenerateSecret();
            trigger.EncryptedSecret = _crypto.Encrypt(plaintextSecret);
        }

        _triggers.Add(trigger);
        try
        {
            await _triggers.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow_trigger.create.failed workflow_trigger_name={WorkflowTriggerName}", trigger.Name);
            throw;
        }

        // A trigger runs a workflow unattended on a schedule or an inbound
        // HTTP call. Creating one, widening its cron, or flipping
        // allow_unsigned are all changes to what can happen with no human in
        // the loop, and none of them were recorded.
        await _audit.LogAsync("workflow_trigger", trigger.WorkflowTriggerId, "create",
            after: TriggerAudit(trigger));

        _logger.LogInformation(
            "workflow_trigger.create.ok workflow_trigger_id={WorkflowTriggerId} workflow_trigger_name={WorkflowTriggerName} workflow_id={WorkflowId} type={Type}",
            trigger.WorkflowTriggerId, trigger.Name, workflowId, trigger.Type);

        var response = ToResponse(trigger);
        response.WebhookSecret = plaintextSecret;
        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "WorkflowTrigger",
            routeValues: new { id = trigger.WorkflowTriggerId },
            value: response);
    }

    public async Task<ActionResult<WorkflowTriggerResponse>> RotateWebhookSecretAsync(Guid id)
    {
        var trigger = await _triggers.GetByIdAsync(id);
        if (trigger is null)
        {
            _logger.LogWarning("workflow_trigger.rotate_secret.not_found workflow_trigger_id={WorkflowTriggerId}", id);
            return new NotFoundObjectResult(new { error = "workflow_trigger not found" });
        }
        if (!IsWebhookType(trigger.Type))
        {
            _logger.LogWarning(
                "workflow_trigger.rotate_secret.validation_failed workflow_trigger_id={WorkflowTriggerId} reason=not_a_webhook",
                id);
            return new BadRequestObjectResult(new { error = "only webhook triggers have a signing secret" });
        }

        var plaintextSecret = GenerateSecret();
        trigger.EncryptedSecret = _crypto.Encrypt(plaintextSecret);
        trigger.AllowUnsigned = false;   // rotating in a secret implies "now require signatures"
        trigger.UpdatedAt = DateTime.UtcNow;
        await _triggers.SaveChangesAsync();

        // Rotating invalidates whatever the previous sender was using, so
        // this is the row that explains a sudden burst of 401s on the ingest
        // endpoint. The secret itself is never recorded.
        await _audit.LogAsync("workflow_trigger", trigger.WorkflowTriggerId, "rotate_secret",
            after: TriggerAudit(trigger));

        _logger.LogInformation("workflow_trigger.rotate_secret.ok workflow_trigger_id={WorkflowTriggerId}", id);

        var response = ToResponse(trigger);
        response.WebhookSecret = plaintextSecret;
        return response;
    }

    public async Task<ActionResult<WorkflowTriggerResponse>> UpdateAsync(Guid id, UpdateWorkflowTrigger dto)
    {
        var trigger = await _triggers.GetByIdAsync(id);
        if (trigger is null)
        {
            _logger.LogWarning("workflow_trigger.update.not_found workflow_trigger_id={WorkflowTriggerId}", id);
            return new NotFoundObjectResult(new { error = "workflow_trigger not found" });
        }

        // Validate the POST-MERGE cron before touching the entity. `trigger`
        // is tracked, so assigning first and rejecting afterwards would leave
        // the invalid expression in the change tracker, ready to be written by
        // the next SaveChanges in the same unit of work. Same shape as
        // VendorCommandService.UpdateAsync.
        var mergedType = dto.Type ?? trigger.Type;
        var mergedCron = dto.CronExpression ?? trigger.CronExpression;
        if (IsScheduleType(mergedType) && !string.IsNullOrWhiteSpace(mergedCron)
            && CronSchedule.TryParse(mergedCron, out var cronError) is null)
        {
            _logger.LogWarning(
                "workflow_trigger.update.validation_failed workflow_trigger_id={WorkflowTriggerId} reason=cron_expression_invalid",
                trigger.WorkflowTriggerId);
            return new BadRequestObjectResult(new { error = cronError });
        }

        // Snapshot before the dto lands (`trigger` is tracked).
        var auditBefore = TriggerAudit(trigger);

        if (dto.Name is not null) trigger.Name = dto.Name;
        if (dto.Type is not null) trigger.Type = dto.Type;
        if (dto.Description is not null) trigger.Description = dto.Description;
        if (dto.Route is not null) trigger.Route = dto.Route;
        if (dto.CronExpression is not null) trigger.CronExpression = dto.CronExpression;
        if (dto.Timezone is not null) trigger.Timezone = dto.Timezone;
        if (dto.InputSchema is not null) trigger.InputSchema = dto.InputSchema.Value;
        if (dto.InputDefaults is not null) trigger.InputDefaults = dto.InputDefaults.Value;
        if (dto.Enabled is not null) trigger.Enabled = dto.Enabled.Value;
        if (dto.AllowUnsigned is not null) trigger.AllowUnsigned = dto.AllowUnsigned.Value;
        if (dto.AllowTargetOverride is not null) trigger.AllowTargetOverride = dto.AllowTargetOverride.Value;
        if (dto.NotificationWebhookURL is not null) trigger.NotificationWebhookURL = dto.NotificationWebhookURL;
        if (dto.NotifyOn is not null) trigger.NotifyOn = dto.NotifyOn;
        if (dto.TargetDevices is not null) trigger.TargetDevices = dto.TargetDevices;

        // Re-anchor the next fire time. Recomputing on every update also
        // re-activates a schedule that was toggled back on and clears
        // NextRunAt when it's disabled or made a non-schedule type — keeping
        // the scheduler's due-query honest.
        trigger.NextRunAt = ComputeNextRun(trigger);

        trigger.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _triggers.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow_trigger.update.failed workflow_trigger_id={WorkflowTriggerId}", trigger.WorkflowTriggerId);
            throw;
        }

        await _audit.LogAsync("workflow_trigger", trigger.WorkflowTriggerId, "update",
            before: auditBefore, after: TriggerAudit(trigger));

        _logger.LogInformation("workflow_trigger.update.ok workflow_trigger_id={WorkflowTriggerId}", trigger.WorkflowTriggerId);
        return ToResponse(trigger);
    }

    public async Task<ActionResult<WorkflowTriggerResponse>> DeleteAsync(Guid id)
    {
        var trigger = await _triggers.GetByIdAsync(id);
        if (trigger is null)
        {
            _logger.LogWarning("workflow_trigger.delete.not_found workflow_trigger_id={WorkflowTriggerId}", id);
            return new NotFoundObjectResult(new { error = "workflow_trigger not found" });
        }

        trigger.IsActive = false;
        trigger.UpdatedAt = DateTime.UtcNow;
        await _triggers.SaveChangesAsync();

        await _audit.LogAsync("workflow_trigger", trigger.WorkflowTriggerId, "delete",
            before: TriggerAudit(trigger));

        _logger.LogInformation("workflow_trigger.delete.ok workflow_trigger_id={WorkflowTriggerId}", trigger.WorkflowTriggerId);
        return ToResponse(trigger);
    }

    private static bool IsScheduleType(string? type) =>
        type is not null
        && (type.Equals("cron", StringComparison.OrdinalIgnoreCase)
            || type.Equals("schedule", StringComparison.OrdinalIgnoreCase));

    // Next UTC fire time for a schedule trigger, or null when it isn't an
    // enabled schedule (webhook/route triggers and disabled ones carry no
    // NextRunAt, so the scheduler's due-query skips them). A cron that fails
    // to parse also yields null here — creation/update already rejected those.
    private static DateTime? ComputeNextRun(WorkflowTriggerModel t) =>
        IsScheduleType(t.Type) && t.Enabled && !string.IsNullOrWhiteSpace(t.CronExpression)
            ? CronSchedule.ComputeNextUtc(t.CronExpression, t.Timezone, DateTime.UtcNow, out _)
            : null;
}
