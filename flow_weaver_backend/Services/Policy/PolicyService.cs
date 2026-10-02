using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using PolicyModel = flow_weaver_backend.Models.Policy;

namespace flow_weaver_backend.Services.Policy;

// Guardrail CRUD. Thin layer — the interesting
// logic lives in IPolicyEvaluator, which this service only writes rows
// for. Enable/disable is a first-class toggle so admins can pause a
// rule without losing its text.
public class PolicyService : IPolicy
{
    private readonly IRepository<PolicyModel> _policies;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<PolicyService> _logger;

    public PolicyService(
        IRepository<PolicyModel> policies, ICurrentUser caller,
        IAuditLogger audit, ILogger<PolicyService> logger)
    {
        _policies = policies;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<PolicyResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _policies.CountAsync();
        var rows = await _policies.ListAsync(limit, offset);

        _logger.LogDebug(
            "policy.list.ok total={Total} returned={Returned}",
            total, rows.Count);

        return new OkObjectResult(new ListResponse<PolicyResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<PolicyResponse>> GetByIdAsync(Guid id)
    {
        var row = await _policies.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("policy.get.not_found policy_id={PolicyId}", id);
            return new NotFoundObjectResult(new { error = "policy not found" });
        }
        return ToResponse(row);
    }

    public async Task<ActionResult<PolicyResponse>> PostAsync(CreatePolicy dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("policy.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (dto.Rule.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("policy.create.validation_failed reason=rule_not_object");
            return new BadRequestObjectResult(new { error = "rule must be a JSON object" });
        }

        var now = DateTime.UtcNow;
        var row = new PolicyModel
        {
            PolicyId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Description = dto.Description,
            Rule = dto.Rule,
            Enabled = dto.Enabled,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _policies.Add(row);
        try
        {
            await _policies.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "policy.create.failed policy_name={PolicyName}", row.Name);
            throw;
        }

        // Policies ARE the guardrail. /policies/audit shows what they blocked
        // but nothing recorded changes to the rules themselves, so "who
        // disabled the rule that would have stopped this" was unanswerable.
        // The rule body is included: for a guardrail it is the whole point.
        await _audit.LogAsync("policy", row.PolicyId, "create",
            after: new { row.Name, row.Description, row.Enabled, rule = row.Rule });

        _logger.LogInformation(
            "policy.create.ok policy_id={PolicyId} policy_name={PolicyName}",
            row.PolicyId, row.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Policy",
            routeValues: new { id = row.PolicyId },
            value: ToResponse(row));
    }

    public async Task<ActionResult<PolicyResponse>> UpdateAsync(Guid id, UpdatePolicy dto)
    {
        var row = await _policies.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("policy.update.not_found policy_id={PolicyId}", id);
            return new NotFoundObjectResult(new { error = "policy not found" });
        }

        // Snapshot before the dto lands (`row` is tracked).
        var auditBefore = new { row.Name, row.Description, row.Enabled, rule = row.Rule };

        if (dto.Name is not null) row.Name = dto.Name.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Rule is not null) row.Rule = dto.Rule.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _policies.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "policy.update.failed policy_id={PolicyId}", row.PolicyId);
            throw;
        }

        // Disabling a guardrail is the interesting case, so it gets its own
        // action rather than hiding inside a generic update payload.
        var disabled = auditBefore.Enabled && !row.Enabled;
        await _audit.LogAsync("policy", row.PolicyId,
            disabled ? "policy.disabled" : "update",
            before: auditBefore,
            after: new { row.Name, row.Description, row.Enabled, rule = row.Rule });

        _logger.LogInformation("policy.update.ok policy_id={PolicyId}", row.PolicyId);
        return ToResponse(row);
    }

    public async Task<ActionResult<PolicyResponse>> DeleteAsync(Guid id)
    {
        var row = await _policies.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("policy.delete.not_found policy_id={PolicyId}", id);
            return new NotFoundObjectResult(new { error = "policy not found" });
        }
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _policies.SaveChangesAsync();

        await _audit.LogAsync("policy", row.PolicyId, "delete",
            before: new { row.Name, row.Description, row.Enabled, rule = row.Rule });

        _logger.LogInformation("policy.delete.ok policy_id={PolicyId}", row.PolicyId);
        return ToResponse(row);
    }

    internal static PolicyResponse ToResponse(PolicyModel row) => new()
    {
        PolicyId = row.PolicyId,
        Name = row.Name,
        Description = row.Description,
        Rule = row.Rule,
        Enabled = row.Enabled,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
