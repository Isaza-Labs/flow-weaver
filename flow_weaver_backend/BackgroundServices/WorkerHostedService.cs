using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using JobModel = flow_weaver_backend.Models.Job;
using StepRunModel = flow_weaver_backend.Models.StepRun;

namespace flow_weaver_backend.BackgroundServices;

// Long-running hosted service that polls the job queue, claims work, and
// dispatches it to either the workflow executor (for orchestration jobs)
// or a registered ISnippetHandler (for step jobs).
//
// Concurrency model: a SemaphoreSlim with `MaxConcurrency` slots gates
// how many jobs run in parallel. Each claimed job spawns a fire-and-forget
// Task that releases its slot on completion. When all slots are taken the
// main loop blocks on WaitAsync until one frees up — back-pressure is
// automatic.
//
// The claim itself is transactional (SKIP LOCKED) so two worker processes
// will never double-process the same job.
//
// Worker ID: <MachineName>-<PID>-<8-char random> so log lines are
// traceable back to a specific container + process even in a multi-replica
// docker-compose setup.
public sealed class WorkerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowExecutor _executor;
    private readonly RetryPolicyExecutor _retry;
    private readonly WorkerOptions _options;
    private readonly ILogger<WorkerHostedService> _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly string _workerId;

    public WorkerHostedService(
        IServiceScopeFactory scopeFactory,
        IWorkflowExecutor executor,
        RetryPolicyExecutor retry,
        IOptions<WorkerOptions> options,
        ILogger<WorkerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _retry = retry;
        _options = options.Value;
        _logger = logger;
        _semaphore = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        var raw = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _workerId = raw.Length > 48 ? raw[..48] : raw;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker {WorkerId} started — tags=[{Tags}], maxConcurrency={Max}",
            _workerId, string.Join(", ", _options.EffectiveTags), _options.MaxConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Acquire a concurrency slot before claiming — no point in
            // pulling a job we can't run yet.
            await _semaphore.WaitAsync(stoppingToken);

            JobModel? job;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IQueueRepository>();
                job = await queue.ClaimAsync(_options.EffectiveTags, _workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _semaphore.Release();
                break;
            }
            catch (Exception ex)
            {
                _semaphore.Release();
                _logger.LogError(ex, "Worker {WorkerId}: claim failed — retrying in {Ms}ms",
                    _workerId, _options.PollDelayMs);
                try { await Task.Delay(_options.PollDelayMs, stoppingToken); } catch { break; }
                continue;
            }

            if (job is null)
            {
                _semaphore.Release();
                try { await Task.Delay(_options.PollDelayMs, stoppingToken); } catch { break; }
                continue;
            }

            // Fire-and-forget: DispatchAsync releases the semaphore when done.
            _ = DispatchAsync(job, stoppingToken);
        }

        _logger.LogInformation("Worker {WorkerId} shutting down", _workerId);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Dispatch
    // ───────────────────────────────────────────────────────────────────

    private async Task DispatchAsync(JobModel job, CancellationToken ct)
    {
        // The OUTER try/finally guarantees the concurrency slot is
        // released even if something between this method's entry and
        // the original inner try (tracing, scope creation, etc.)
        // throws. Without this, a synchronous failure here would
        // permanently leak a semaphore slot per crash.
        try
        {
        // Trace scope covers the entire dispatch lifecycle per job. A separate
        // DI scope is used so the tracer's scoped DbContext doesn't collide
        // with the one the handler builds for its own per-job work.
        Guid? traceId = null;
        try
        {
            using (var traceScope = _scopeFactory.CreateScope())
            {
                // Attribute the trace to the workflow runner identity so the
                // row is not left unattributed.
                BindJobIdentity(traceScope, job);
                var t = traceScope.ServiceProvider.GetRequiredService<ITraceLogger>();
                traceId = await t.StartAsync("worker.dispatch", "worker",
                    metadata: JobTraceMetadata(job), ct);
            }
        }
        catch { /* tracing must never break the worker */ }

        try
        {
            switch (job.Type)
            {
                case "workflow_run":
                    await HandleWorkflowRunJobAsync(job, ct);
                    break;

                case "step":
                    await HandleStepJobAsync(job, ct);
                    break;

                default:
                    _logger.LogWarning("Worker {WorkerId}: unknown job type '{Type}' — failing",
                        _workerId, job.Type);
                    await FailJobAsync(job.JobId, $"unknown job type: {job.Type}", ct);
                    break;
            }

            if (traceId.HasValue)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    BindJobIdentity(scope, job);
                    var t = scope.ServiceProvider.GetRequiredService<ITraceLogger>();
                    await t.CompleteAsync(traceId.Value, ct: ct);
                }
                catch { /* swallow */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — leave the job claimed; it'll be retried
            // by another worker once the claim times out or is reset.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerId}: unhandled error in job {JobId} (type={Type})",
                _workerId, job.JobId, job.Type);
            await FailJobAsync(job.JobId, ex.Message, ct);
            if (traceId.HasValue)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    BindJobIdentity(scope, job);
                    var t = scope.ServiceProvider.GetRequiredService<ITraceLogger>();
                    await t.FailAsync(traceId.Value, ex.Message, ct: ct);
                }
                catch { /* swallow */ }
            }
        }
        finally
        {
            // Inner finally — leaves the original semaphore release in
            // place for the happy path so traces / dbcontext from the
            // catch blocks still complete before we hand the slot back.
            _semaphore.Release();
        }
        }
        finally
        {
            // Outer finally — defense-in-depth: if anything between
            // this method's entry and the inner `try` (or anywhere the
            // inner finally couldn't run) escapes synchronously, we
            // still release the slot. Calling Release twice on a slot
            // is also caught by SemaphoreFullException, but the inner
            // finally always runs first when it gets a chance.
            try { _semaphore.Release(); }
            catch (SemaphoreFullException) { /* inner finally already released */ }
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  Trace attribution
    // ───────────────────────────────────────────────────────────────────

    // Binds the runner identity onto a freshly-created scope so anything that
    // scope resolves — the tracer here — attributes its writes to the runner
    // rather than to nobody. Same pattern the webhook receivers and
    // GitOpsHandler use.
    private static void BindJobIdentity(IServiceScope scope, JobModel job)
    {
        if (scope.ServiceProvider.GetRequiredService<ICurrentUser>() is MutableCurrentUser currentUser)
            currentUser.Bind(userId: null, username: "workflow-runner");
    }

    // What a trace row needs to be useful on its own. `job_id` alone forced a
    // join against the jobs table, which the queue prunes — so an old worker
    // trace became unattributable. The run/step ids are lifted out of the
    // payload so the row points straight at what it executed.
    private object JobTraceMetadata(JobModel job)
    {
        Guid? workflowRunId = ReadGuid(job.Payload, "workflow_run_id");
        Guid? stepRunId = ReadGuid(job.Payload, "step_run_id");

        return new
        {
            job_id = job.JobId,
            job_type = job.Type,
            worker_id = _workerId,
            workflow_run_id = workflowRunId,
            step_run_id = stepRunId,
        };
    }

    private static Guid? ReadGuid(System.Text.Json.JsonElement payload, string property)
    {
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!payload.TryGetProperty(property, out var el)) return null;
        return el.TryGetGuid(out var value) ? value : null;
    }

    // ───────────────────────────────────────────────────────────────────
    //  workflow_run — delegates to the executor
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleWorkflowRunJobAsync(JobModel job, CancellationToken ct)
    {
        if (!job.Payload.TryGetProperty("workflow_run_id", out var idEl))
        {
            await FailJobAsync(job.JobId, "workflow_run job payload missing workflow_run_id", ct);
            return;
        }
        var runId = idEl.GetGuid();

        _logger.LogInformation("Worker {WorkerId}: orchestrating run {RunId}", _workerId, runId);

        try
        {
            // jobId/workerId let the orchestrator heartbeat its own claim;
            // without renewal, runs longer than DefaultLease (5 min) get
            // reclaimed mid-flight and a second worker would race the first.
            await _executor.ExecuteRunAsync(runId, ct, job.JobId, _workerId);
            await CompleteJobAsync(job.JobId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerId}: run {RunId} orchestration failed", _workerId, runId);
            await FailJobAsync(job.JobId, ex.Message, ct);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  step — loads step_run, resolves handler, executes, writes result
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleStepJobAsync(JobModel job, CancellationToken ct)
    {
        if (!job.Payload.TryGetProperty("step_run_id", out var idEl))
        {
            await FailJobAsync(job.JobId, "step job payload missing step_run_id", ct);
            return;
        }
        var stepRunId = idEl.GetGuid();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var step = await db.StepRuns.FindAsync(new object[] { stepRunId }, ct);
        if (step is null)
        {
            _logger.LogWarning("Worker {WorkerId}: step_run {Id} not found", _workerId, stepRunId);
            await FailJobAsync(job.JobId, "step_run not found", ct);
            return;
        }

        // Mark running.
        step.Status = StepStatus.Running;
        step.StartedAt = DateTime.UtcNow;
        step.WorkerId = _workerId;
        step.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Resolve snippet type + stored config from the snippet row.
        // Handlers like python_snippet / ansible_playbook / transform keep
        // their "code" on this row rather than repeating it in every step's
        // InputPayload, so we load it once here and pass it through.
        var snippetType = "unknown";
        var snippetName = "unknown";
        bool? snippetChangesState = null;
        string? snippetCode = null;
        string? scriptLanguage = null;
        var snippetTimeoutSeconds = 0;
        string? targetMode = null;
        var networkEnabled = false;
        var retryPolicy = new RetryPolicy();
        if (step.SnippetId.HasValue)
        {
            var snippet = await db.Snippets
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SnippetId == step.SnippetId.Value, ct);
            if (snippet is not null)
            {
                snippetType = snippet.Type;
                snippetName = snippet.Name;
                // The author's declaration for the types whose code this row carries.
                snippetChangesState = snippet.ChangesState;
                snippetCode = snippet.Code;
                scriptLanguage = snippet.ScriptLanguage;
                snippetTimeoutSeconds = snippet.TimeoutSeconds;
                targetMode = snippet.TargetMode;
                networkEnabled = snippet.NetworkEnabled;
                retryPolicy = RetryPolicyExecutor.Parse(snippet.RetryPolicy);
            }
        }

        // Resolve handler from the scoped container. Handlers are
        // registered as Scoped<ISnippetHandler> so they get a fresh
        // DbContext per job. We find by matching Type string.
        var allHandlers = scope.ServiceProvider.GetServices<ISnippetHandler>();
        var handler = allHandlers.FirstOrDefault(h =>
            string.Equals(h.Type, snippetType, StringComparison.OrdinalIgnoreCase));

        if (handler is null)
        {
            var err = $"no handler registered for snippet type '{snippetType}'";
            MarkStepFailed(step, err);
            await db.SaveChangesAsync(ct);
            await FailJobAsync(job.JobId, err, ct);
            return;
        }

        var request = new SnippetRequest
        {
            StepRunId = step.StepRunId,
            WorkflowRunId = step.WorkflowRunId,
            NodeId = step.NodeId,
            SnippetId = step.SnippetId,
            SnippetType = snippetType,
            InputPayload = step.InputPayload,
            DeviceId = step.DeviceId,
            SnippetCode = snippetCode,
            ScriptLanguage = scriptLanguage,
            SnippetTimeoutSeconds = snippetTimeoutSeconds,
            TargetMode = targetMode,
            NetworkEnabled = networkEnabled,
        };

        // Every attempt the snippet's retry_policy allows runs inside this job
        // (execution/SPEC.md §3). Between attempts the job lease is pushed past the
        // backoff so JobReclaim does not hand the step to a second worker while this one
        // waits, and the step is re-read: if the orchestrator already failed it for
        // timing out, or the run was cancelled, retrying would only race that decision.
        var outcome = await _retry.RunAsync(
            retryPolicy,
            async attemptCt =>
            {
                try
                {
                    return await handler.ExecuteAsync(request, attemptCt);
                }
                catch (Exception ex)
                {
                    return new SnippetResult
                    {
                        // A handler that throws is a bug in the handler, not a workflow failure the
                        // author can act on. Recorded as unchanged for the same reason Nashira's
                        // `SnippetResult.Fail` is: a handler that mutated something and THEN threw
                        // has to catch its own exception and say so, because nobody out here can.
                        // Never retryable: a bug does not fix itself on the next attempt.
                        Change = StepChange.Unchanged,
                        Success = false,
                        Error = ex.Message,
                        Logs = ex.ToString(),
                    };
                }
            },
            (delay, retryCt) => BeforeRetryAsync(job, step, delay, retryCt),
            ct);

        if (outcome.Abandoned)
        {
            // Someone else has already decided this step's fate. Recording our last attempt
            // would overwrite that decision, so only the job is closed.
            _logger.LogWarning(
                "worker.step.retry_abandoned step_run_id={StepRunId} attempts={Attempts}",
                step.StepRunId, outcome.Attempts);
            await FailJobAsync(job.JobId, "retry abandoned: step no longer running", ct);
            return;
        }

        var result = outcome.Result;
        if (outcome.RetryLog.Length > 0)
            step.Logs = string.IsNullOrEmpty(step.Logs)
                ? outcome.RetryLog.TrimEnd()
                : step.Logs + "\n" + outcome.RetryLog.TrimEnd();

        // Did it change anything? The handler answers where it can see the action; where it
        // cannot, the author does — on the node, or on the snippet. Nobody answering is a
        // defect and fails the step rather than resolving to a guess (workflow-v1/run-outcome).
        var changed = StepChangeResolution.Resolve(result.Change, snippetChangesState, step.InputPayload);
        if (changed is null)
        {
            var undeclared = StepChangeResolution.UndeclaredMessage(snippetName, snippetType, step.NodeId);
            _logger.LogWarning(
                "worker.step.change_undeclared step_run_id={StepRunId} snippet_type={SnippetType} node_id={NodeId}",
                step.StepRunId, snippetType, step.NodeId);
            MarkStepFailed(step, undeclared);
            // A code, not just the sentence. This failure reads like a handler blowing up and
            // is not one — nothing about the device or the script is wrong, an author simply
            // has not said whether the step changes anything. The code is what lets a reader
            // (and the runs skill) tell the two apart without parsing prose.
            step.ErrorCode = "change_undeclared";
            step.Logs = string.IsNullOrEmpty(step.Logs) ? result.Logs : step.Logs + "\n" + result.Logs;
            await db.SaveChangesAsync(ct);
            await FailJobAsync(job.JobId, undeclared, ct);
            return;
        }

        // Write result to step_run.
        step.ChangedState = changed;
        step.Status = result.Success ? StepStatus.Completed : StepStatus.Failed;
        step.OutputPayload = result.Output;
        step.Logs = string.IsNullOrEmpty(step.Logs) ? result.Logs : step.Logs + "\n" + result.Logs;
        step.Error = result.Error;
        step.CompletedAt = DateTime.UtcNow;
        step.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Worker {WorkerId}: step {StepRunId} → {Status}",
            _workerId, step.StepRunId, step.Status);

        // Mark job terminal.
        if (result.Success)
            await CompleteJobAsync(job.JobId, ct);
        else
            await FailJobAsync(job.JobId, result.Error, ct);
    }

    // Called before each retry: extend the claim past the wait, wait, then confirm the step
    // is still this worker's to run. False abandons the retry loop.
    private async Task<bool> BeforeRetryAsync(
        JobModel job, StepRunModel step, TimeSpan delay, CancellationToken ct)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IQueueRepository>();
            var leaseSeconds = (int)Math.Ceiling(delay.TotalSeconds) + RetryLeaseMarginSeconds;
            if (!await queue.RenewLeaseAsync(job.JobId, _workerId, leaseSeconds, ct))
                return false;
        }

        await Task.Delay(delay, ct);

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = await db.StepRuns
                .AsNoTracking()
                .Where(s => s.StepRunId == step.StepRunId)
                .Select(s => new { s.Status, s.WorkflowRunId })
                .FirstOrDefaultAsync(ct);
            if (current is null || current.Status != StepStatus.Running)
                return false;

            var runStatus = await db.WorkflowRuns
                .AsNoTracking()
                .Where(r => r.WorkflowRunId == current.WorkflowRunId)
                .Select(r => r.Status)
                .FirstOrDefaultAsync(ct);
            return runStatus != RunStatus.Cancelled;
        }
    }

    // Lease time granted beyond the backoff itself: the next attempt has to run inside the
    // claim too. Matches the queue's default lease.
    private const int RetryLeaseMarginSeconds = 300;

    // ───────────────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────────────

    private static void MarkStepFailed(StepRunModel step, string error)
    {
        step.Status = StepStatus.Failed;
        step.Error = error;
        step.CompletedAt = DateTime.UtcNow;
        step.UpdatedAt = DateTime.UtcNow;
    }

    private async Task CompleteJobAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var q = scope.ServiceProvider.GetRequiredService<IQueueRepository>();
            await q.CompleteAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerId}: failed to complete job {JobId}", _workerId, jobId);
        }
    }

    private async Task FailJobAsync(Guid jobId, string error, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var q = scope.ServiceProvider.GetRequiredService<IQueueRepository>();
            await q.FailAsync(jobId, error, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {WorkerId}: failed to fail job {JobId}", _workerId, jobId);
        }
    }
}
