using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Identity;
using WorkflowTriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.Services.WorkflowTrigger;

// Handles a verified inbound webhook delivery for a `webhook`-type
// WorkflowTrigger: verifies the signature, applies backpressure, and enqueues
// exactly one workflow run with the raw payload as `input`. Mirrors
// GitWebhookReceiver.
//
// SSRF note: this receiver makes NO outbound HTTP calls — it only reads the
// trigger row and enqueues a run. A URL inside the payload is inert here; it
// only becomes a request if the fired workflow's rest_call / integration nodes
// use it, and those are already guarded by IUrlGuard at execution time
// (UrlGuardSsrfTests). So the ingest itself is not an SSRF vector.
//
// Concurrency: invoked from the public [AllowAnonymous] controller, so there is
// no ICurrentUser from claims — this rebinds the caller via MutableCurrentUser
// in a fresh scope, exactly like the scheduler and the Git receiver.
public sealed class WorkflowWebhookReceiver
{
    public sealed record Outcome(int StatusCode, string Message, Guid? WorkflowRunId);

    // Same threshold as the Git receiver: once the job queue is this deep the
    // system is already behind, so shed webhook-driven load.
    private const int MaxQueueDepth = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowWebhookReceiver> _logger;

    public WorkflowWebhookReceiver(
        IServiceScopeFactory scopeFactory,
        ILogger<WorkflowWebhookReceiver> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<Outcome> ReceiveAsync(
        Guid triggerId, string? signatureHeader, string? tokenHeader,
        byte[] body, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var triggers = sp.GetRequiredService<IWorkflowTriggerRepository>();
        var jobs = sp.GetRequiredService<IJobRepository>();
        var crypto = sp.GetRequiredService<ICredentialEncryptionService>();
        var userOverride = sp.GetRequiredService<MutableCurrentUser>();

        var trigger = await triggers.FindActiveByIdAsync(triggerId, ct);
        if (trigger is null)
            return new Outcome(404, "webhook trigger not found", null);

        // Only webhook-type triggers are ingestable. A cron/schedule trigger id
        // must never be firable from the public endpoint.
        if (!string.Equals(trigger.Type, WorkflowTriggerModel.TypeWebhook, StringComparison.OrdinalIgnoreCase))
            return new Outcome(404, "webhook trigger not found", null);

        if (!trigger.Enabled)
        {
            await StampAsync(triggers, triggerId, "webhook_rejected", ct);
            return new Outcome(403, "trigger disabled", null);
        }

        // Signature verification (same policy as GitWebhookReceiver):
        //   • secret on file → verify HMAC (X-FlowWeaver-Signature) OR the
        //     shared-secret token (X-FlowWeaver-Token); reject on mismatch.
        //   • no secret AND AllowUnsigned=false (default) → reject.
        //   • no secret AND AllowUnsigned=true → accept (admin opt-in).
        var hasSecret = trigger.EncryptedSecret is { Length: > 0 };
        if (hasSecret)
        {
            var secret = crypto.Decrypt(trigger.EncryptedSecret) ?? string.Empty;
            var ok = GitWebhookSignature.VerifyGeneric(body, signatureHeader, secret)
                     || GitWebhookSignature.VerifyGitlab(tokenHeader, secret);
            if (!ok)
            {
                await StampAsync(triggers, triggerId, "webhook_rejected", ct);
                _logger.LogWarning(
                    "workflow_webhook.signature_invalid trigger_id={TriggerId}", triggerId);
                return new Outcome(401, "signature mismatch", null);
            }
        }
        else if (!trigger.AllowUnsigned)
        {
            await StampAsync(triggers, triggerId, "webhook_rejected", ct);
            _logger.LogWarning(
                "workflow_webhook.unsigned_rejected trigger_id={TriggerId}", triggerId);
            return new Outcome(401, "trigger has no secret configured; set one or enable allow_unsigned", null);
        }

        // Rebind an identity for the anonymous scope so IWorkflowExecutor has
        // a caller to attribute the run to.
        userOverride.Bind(userId: null, username: "workflow-webhook");

        // Backpressure — refuse to pile onto an already-deep queue.
        var queueDepth = await jobs.CountByStatusesAsync(new[] { JobStatus.Pending, JobStatus.Claimed }, ct);
        if (queueDepth >= MaxQueueDepth)
        {
            await StampAsync(triggers, triggerId, "webhook_backpressure", ct);
            _logger.LogWarning(
                "workflow_webhook.backpressure trigger_id={TriggerId} queue_depth={QueueDepth}",
                triggerId, queueDepth);
            return new Outcome(503, "queue full, retry later", null);
        }

        // Parse the body once. The JSON payload becomes the run input (under
        // `webhook`). Top-level `target_devices` / `target_pools` (arrays of
        // GUIDs) may also TARGET the run, but only under the rules in
        // ResolveTargets — see the note on WorkflowTrigger.AllowTargetOverride
        // for why they are not simply honoured. A non-JSON / non-object body
        // fires with just the defaults + provenance.
        var parsed = ParseBody(trigger, body);
        var targets = ResolveTargets(trigger, parsed, _logger);

        try
        {
            var executor = sp.GetRequiredService<IWorkflowExecutor>();
            var runId = await executor.EnqueueRunAsync(Guid.Empty,
                trigger.WorkflowId,
                new RunWorkflowRequest
                {
                    Input = parsed.Input,
                    TargetDevices = targets.Devices,
                    TargetPools = targets.Pools,
                },
                ct,
                trigger: "webhook");

            await StampAsync(triggers, triggerId, "webhook_dispatched", ct);
            _logger.LogInformation(
                "workflow_webhook.dispatched trigger_id={TriggerId} workflow_id={WorkflowId} run_id={RunId}",
                triggerId, trigger.WorkflowId, runId);
            return new Outcome(202, "workflow run enqueued", runId);
        }
        catch (Exception ex)
        {
            // EnqueueRunAsync throws WorkflowExecutorException for pre-flight
            // failures (env mismatch, missing integration creds, structural
            // errors). Record + surface a 500 so the sender can retry.
            await StampAsync(triggers, triggerId, "webhook_failed", ct);
            _logger.LogError(ex,
                "workflow_webhook.enqueue_failed trigger_id={TriggerId} workflow_id={WorkflowId}",
                triggerId, trigger.WorkflowId);
            return new Outcome(500, ex.Message, null);
        }
    }

    private sealed record ParsedBody(JsonElement Input, List<Guid> TargetDevices, List<Guid> TargetPools);

    private sealed record ResolvedTargets(List<Guid> Devices, List<Guid> Pools);

    // Decides what the run actually fires against.
    //
    // The caller here holds the trigger's shared secret, not a user session,
    // and the run is enqueued straight through IWorkflowExecutor — so it never
    // passes the environment/resource-scoped RBAC check a manual run goes
    // through (see the granular-RBAC block in WorkflowController.Run). The body
    // therefore cannot be trusted to widen anything:
    //
    //   AllowTargetOverride = false (default)
    //       Body targets ignored entirely. The run uses the trigger's
    //       configured TargetDevices. This is the safe shape: a leaked webhook
    //       secret lets an attacker re-fire the workflow at the devices an
    //       operator already chose, and nothing more.
    //
    //   AllowTargetOverride = true, trigger scoped (TargetDevices non-empty)
    //       Body devices are INTERSECTED with the trigger's list — the caller
    //       may narrow, never extend. Pools are dropped, because a pool is an
    //       indirection whose membership changes outside this request and so
    //       cannot be checked against the scope here.
    //
    //   AllowTargetOverride = true, trigger unscoped (TargetDevices empty)
    //       Body devices and pools are honoured as-is. There is no scope to
    //       narrow, which is the operator explicitly saying "this webhook
    //       picks its own targets".
    private static ResolvedTargets ResolveTargets(
        WorkflowTriggerModel trigger, ParsedBody parsed, ILogger logger)
    {
        var configured = trigger.TargetDevices ?? new List<Guid>();
        var requestedAny = parsed.TargetDevices.Count > 0 || parsed.TargetPools.Count > 0;

        if (!trigger.AllowTargetOverride)
        {
            if (requestedAny)
                logger.LogWarning(
                    "workflow_webhook.targets_ignored trigger_id={TriggerId} devices={Devices} pools={Pools} "
                    + "— body-supplied targets are refused unless the trigger sets allow_target_override.",
                    trigger.WorkflowTriggerId, parsed.TargetDevices.Count, parsed.TargetPools.Count);
            return new ResolvedTargets(configured, new List<Guid>());
        }

        if (configured.Count == 0)
            return new ResolvedTargets(parsed.TargetDevices, parsed.TargetPools);

        if (!requestedAny)
            return new ResolvedTargets(configured, new List<Guid>());

        var allowed = configured.ToHashSet();
        var narrowed = parsed.TargetDevices.Where(allowed.Contains).ToList();
        var rejected = parsed.TargetDevices.Count - narrowed.Count;

        if (rejected > 0 || parsed.TargetPools.Count > 0)
            logger.LogWarning(
                "workflow_webhook.targets_narrowed trigger_id={TriggerId} requested={Requested} "
                + "accepted={Accepted} rejected={Rejected} pools_dropped={Pools} "
                + "— a scoped trigger only lets the body narrow its device list.",
                trigger.WorkflowTriggerId, parsed.TargetDevices.Count, narrowed.Count,
                rejected, parsed.TargetPools.Count);

        // Every requested device was outside the scope. Falling back to the
        // configured list would run against devices the caller did not ask for,
        // so fire against nothing and let the executor report an empty target
        // set rather than doing something surprising.
        return new ResolvedTargets(narrowed, new List<Guid>());
    }

    private static ParsedBody ParseBody(WorkflowTriggerModel trigger, byte[] body)
    {
        JsonElement? payload = null;
        var devices = new List<Guid>();
        var pools = new List<Guid>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                payload = doc.RootElement.Clone();
                ExtractGuids(doc.RootElement, "target_devices", devices);
                ExtractGuids(doc.RootElement, "target_pools", pools);
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — fire with defaults + provenance only.
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();

            // Configured defaults first, so payload/provenance can't be shadowed
            // by them but the workflow still sees them.
            if (trigger.InputDefaults.ValueKind == JsonValueKind.Object)
                foreach (var prop in trigger.InputDefaults.EnumerateObject())
                    prop.WriteTo(w);

            w.WriteString("trigger_id", trigger.WorkflowTriggerId.ToString());
            w.WritePropertyName("webhook");
            if (payload is { } p) p.WriteTo(w); else w.WriteNullValue();

            w.WriteEndObject();
        }
        var input = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
        return new ParsedBody(input, devices, pools);
    }

    // Pull a top-level array of GUID strings (e.g. "target_devices") out of the
    // webhook body. Non-array / non-GUID entries are ignored.
    private static void ExtractGuids(JsonElement obj, string property, List<Guid> into)
    {
        if (!obj.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                into.Add(g);
    }

    private static async Task StampAsync(
        IWorkflowTriggerRepository triggers, Guid triggerId, string status, CancellationToken ct)
    {
        var tracked = await triggers.FindTrackedByIdAsync(triggerId, ct);
        if (tracked is null) return;
        var now = DateTime.UtcNow;
        tracked.LastRunAt = now;
        tracked.LastRunStatus = status;
        tracked.UpdatedAt = now;
        await triggers.SaveChangesAsync(ct);
    }
}
