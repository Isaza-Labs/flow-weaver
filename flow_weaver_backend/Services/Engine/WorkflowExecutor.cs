using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;
using StepRunModel = flow_weaver_backend.Models.StepRun;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Services.Engine;

// Singleton orchestrator. Two entry points:
//
//   EnqueueRunAsync — called from the HTTP controller (POST /workflow/{id}/run).
//     Creates a WorkflowRun row, validates the DAG eagerly, enqueues a
//     `type=workflow_run` job, and returns immediately with the run id.
//
//   ExecuteRunAsync — called by the worker (Sprint 2.5) when it claims
//     that job. Holds a scoped DbContext for the run's duration while
//     polling step_runs; bounded by a SemaphoreSlim so the process
//     never orchestrates more than MaxConcurrentWorkflows simultaneously.
//
// Architecture: the executor does NOT execute steps in-process. It
// creates step_run rows and enqueues `type=step` jobs. Workers claim
// those jobs, run handlers (Sprint 2.6), and write results back to
// step_runs. The orchestrator sees the update on its next poll cycle.
//
// MVP limitations (documented for later sprints):
//   - One step_run per DAG node (no per-device fan-out yet; targets are
//     passed in InputPayload for handlers to loop over).
//   - Join semantics: a node activates on the FIRST predecessor that
//     completes with a matching edge. Full all-predecessor gating arrives
//     with the condition evaluator in Sprint 2.7.
//   - Retry policy: wired and delay-calculated, but the re-enqueue loop
//     lives in the worker handler dispatch (Sprint 2.5); the executor
//     records the retry count on step_runs for visibility.
public sealed class WorkflowExecutor : IWorkflowExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DagParser _parser;
    private readonly IVariableResolver _resolver;
    private readonly IConditionEvaluator _conditions;
    private readonly RetryPolicyExecutor _retryCalc;
    private readonly WorkflowExecutorOptions _options;
    private readonly ILogger<WorkflowExecutor> _logger;
    private readonly SemaphoreSlim _gate;

    private static readonly string[] Sentinels = { "__start__", "__end__", "subflow" };

    public WorkflowExecutor(
        IServiceScopeFactory scopeFactory,
        DagParser parser,
        IVariableResolver resolver,
        IConditionEvaluator conditions,
        RetryPolicyExecutor retryCalc,
        IOptions<WorkflowExecutorOptions> options,
        ILogger<WorkflowExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _parser = parser;
        _resolver = resolver;
        _conditions = conditions;
        _retryCalc = retryCalc;
        _options = options.Value;
        _logger = logger;
        _gate = new SemaphoreSlim(_options.MaxConcurrentWorkflows, _options.MaxConcurrentWorkflows);
    }

    // ───────────────────────────────────────────────────────────────────
    //  EnqueueRunAsync — HTTP-facing
    // ───────────────────────────────────────────────────────────────────

    public async Task<Guid> EnqueueRunAsync(
        Guid userId, Guid workflowId,
        RunWorkflowRequest request, CancellationToken ct,
        string trigger = "manual")
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IQueueRepository>();

        var workflow = await db.Workflows
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowId == workflowId
                                      && w.IsActive, ct)
            ?? throw new WorkflowExecutorException($"workflow {workflowId} not found");

        // Sprint 3.4: environment guard. Draft workflows can only run on
        // dev-sandbox workers. QA on qa-lab. Production on production.
        // This is enforced at enqueue time so the user gets immediate
        // feedback rather than a deferred failure in the worker.
        var allowedEnv = workflow.Environment switch
        {
            "production" => "production",
            "qa" => "qa-lab",
            _ => "dev-sandbox",
        };
        var workerEnv = _options.WorkerEnvironment ?? "dev-sandbox";
        if (workflow.Environment != "draft" && workerEnv != allowedEnv)
            throw new WorkflowExecutorException(
                $"workflow environment '{workflow.Environment}' requires worker environment '{allowedEnv}', but current is '{workerEnv}'");

        // FR-021: resolve the requested targets against the workflow's
        // environment before the run row exists. The same selection that
        // works in draft resolves to nothing in qa when the devices aren't
        // flagged as QA lab, and the operator deserves that as a 409 on the
        // run request rather than a run that starts and does nothing.
        if (request.TargetDevices.Count > 0 || request.TargetPools.Count > 0)
        {
            var preflight = await ResolveTargetsAsync(
                db, request.TargetDevices, request.TargetPools,
                workflow.Environment, ct);
            EnsureTargetsResolved(preflight, workflow.Environment);
        }

        // Fail fast on structural errors before writing any row.
        _parser.Parse(workflow.Nodes, workflow.Edges);

        // FU-2: pre-flight integration health. Refuse to enqueue when
        // the workflow references an integration that is still waiting
        // for credentials (Status = "needs_config", typical right after
        // the import wizard auto-creates the row). Without this gate the
        // run would start, dispatch the first step, and fail in the
        // handler — the user pays setup cost (audit row, trace) for a
        // run that could never succeed.
        await ValidateIntegrationsReadyAsync(db, workflow.Nodes, ct);

        // Detect transitive subflow cycles (Sprint 3.3).
        await DagParser.DetectSubflowCyclesAsync(
            workflowId, workflow.Nodes,
            async childId =>
            {
                var child = await db.Workflows
                    .AsNoTracking()
                    .FirstOrDefaultAsync(w => w.WorkflowId == childId
                                              && w.IsActive, ct);
                return child?.Nodes;
            });

        var now = DateTime.UtcNow;
        var inputPayload = request.Input.ValueKind != JsonValueKind.Undefined
            ? request.Input
            : JsonDocument.Parse("{}").RootElement;

        var run = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = RunStatus.Pending,
            InputPayload = inputPayload,
            TargetDevices = request.TargetDevices,
            TargetPools = request.TargetPools,
            Trigger = trigger,
            CreatedBy = userId.ToString(),
            // Freeze the DAG at enqueue time so concurrent edits to the
            // workflow definition cannot alter this run.
            NodesSnapshot = CloneJson(workflow.Nodes),
            EdgesSnapshot = CloneJson(workflow.Edges),
            // …and record WHICH graph that was. The snapshot answers it by value; this
            // answers it by name, which is what an audit event can carry and what a
            // promotion gate compares against.
            SchemaHash = Services.Workflow.WorkflowCanonicalizer.ComputeSchemaHash(
                workflow.Nodes, workflow.Edges),
            // Recorded, not just obeyed — see the column's note. Defaults to stopping.
            StopOnFailure = request.StopOnFailure ?? true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkflowRuns.Add(run);

        // Persist the run + the orchestrator job in a single transaction so
        // we never end up with a run row that has no matching job, or a job
        // that references a run id that was rolled back.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);

        var jobPayload = JsonSerializer.SerializeToElement(
            new { workflow_run_id = run.WorkflowRunId });
        await queue.EnqueueAsync("workflow_run", jobPayload,
            tag: "orchestrator", priority: 10, ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Run {RunId} enqueued for workflow {WorkflowId}",
            run.WorkflowRunId, workflowId);

        return run.WorkflowRunId;
    }

    // ───────────────────────────────────────────────────────────────────
    //  ExecuteRunAsync — worker-facing (long-running)
    // ───────────────────────────────────────────────────────────────────

    public async Task ExecuteRunAsync(
        Guid workflowRunId,
        CancellationToken ct,
        Guid? jobId = null,
        string? workerId = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await OrchestrateAsync(workflowRunId, ct, jobId, workerId);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  Orchestration body
    // ───────────────────────────────────────────────────────────────────

    private async Task OrchestrateAsync(
        Guid workflowRunId,
        CancellationToken ct,
        Guid? jobId = null,
        string? workerId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IQueueRepository>();

        // Heartbeat every (lease - 60s) so we always renew well before the
        // 5-minute default lease expires. The interval is recomputed inside
        // the loop in case the lease constant changes.
        const int heartbeatLeaseSeconds = 300;
        var heartbeatInterval = TimeSpan.FromSeconds(heartbeatLeaseSeconds - 60);
        var nextHeartbeatAt = DateTime.UtcNow + heartbeatInterval;

        var run = await db.WorkflowRuns
            .FirstOrDefaultAsync(r => r.WorkflowRunId == workflowRunId, ct);
        if (run is null)
        {
            _logger.LogWarning("Run {RunId} not found — aborting orchestration", workflowRunId);
            return;
        }

        // Transition to running.
        run.Status = RunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        run.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            // Prefer the snapshot captured at enqueue time so the run always
            // executes the DAG its author asked for. Fall back to the live
            // workflow only for legacy rows written before the snapshot
            // columns existed.
            JsonElement nodes;
            JsonElement edges;
            if (run.NodesSnapshot.ValueKind == JsonValueKind.Array
                && run.EdgesSnapshot.ValueKind == JsonValueKind.Array)
            {
                nodes = run.NodesSnapshot;
                edges = run.EdgesSnapshot;
            }
            else
            {
                var workflow = await db.Workflows
                    .AsNoTracking()
                    .FirstOrDefaultAsync(w => w.WorkflowId == run.WorkflowId, ct)
                    ?? throw new WorkflowExecutorException("workflow disappeared mid-run");
                nodes = workflow.Nodes;
                edges = workflow.Edges;
            }

            var dag = _parser.Parse(nodes, edges);

            // FR-021: qa runs only fan out to qa-lab devices/pools. We
            // look up the workflow's environment once (snapshots don't
            // carry it) and pass it to target resolution so production
            // inventory can't get pinged by a qa promotion smoke test.
            var runMeta = await db.Workflows
                .AsNoTracking()
                .Where(w => w.WorkflowId == run.WorkflowId)
                .Select(w => new { w.Environment, w.Name })
                .FirstOrDefaultAsync(ct);
            var runEnvironment = runMeta?.Environment ?? "draft";
            var runWorkflowName = runMeta?.Name ?? string.Empty;

            var resolution = await ResolveTargetsAsync(
                db, run.TargetDevices, run.TargetPools, runEnvironment, ct);

            // Re-checked here and not only at enqueue: subflow children are
            // built directly (they inherit the parent's targets) and a QA
            // flag can be cleared between enqueue and execution.
            if (run.TargetDevices.Count > 0 || run.TargetPools.Count > 0)
            {
                if (resolution.HadRejections)
                    _logger.LogWarning(
                        "Run {RunId} target resolution dropped entries in env={Environment}: "
                        + "blocked_devices={BlockedDevices} blocked_pools={BlockedPools} missing={Missing}",
                        workflowRunId, runEnvironment,
                        string.Join(",", resolution.BlockedDevices),
                        string.Join(",", resolution.BlockedPools),
                        string.Join(",", resolution.MissingIds));

                EnsureTargetsResolved(resolution, runEnvironment);
            }

            var targets = resolution.DeviceIds;

            // Whoever started the run, for `{{ run.owner_email }}` — the
            // default recipient of the canonical notify-failure node. Runs
            // store the starter's user id in CreatedBy (subflows inherit the
            // parent's), so one lookup per orchestration resolves it.
            var ownerEmail = await ResolveOwnerEmailAsync(db, run, ct);
            if (string.IsNullOrEmpty(ownerEmail))
                _logger.LogWarning(
                    "Run {RunId} has no resolvable owner email (created_by={CreatedBy}) — "
                    + "{{{{ run.owner_email }}}} resolves to an empty string and any "
                    + "notify-failure node using it will fail at send time",
                    workflowRunId, run.CreatedBy ?? "(null)");

            // Run-context payload made available to templates as
            // `{{ run.id }}`, `{{ run.workflow_name }}`, etc. Built once
            // per orchestration so the polling loop's BuildResolvedInput
            // can capture it via closure.
            //
            // `failed_step_id` / `failed_step_error` are placeholders here:
            // EnqueueStepAsync overlays the real values when a node fires
            // from a `failure` edge (see WithFailedStep). They must still
            // exist as empty strings so a notify-failure node reached by an
            // `always` edge resolves them instead of dying on the residual
            // unresolved-template scan.
            var runContext = BuildRunContext(
                run.WorkflowRunId, run.WorkflowId, runWorkflowName, runEnvironment,
                run.Trigger ?? "manual", run.StartedAt?.ToString("o"),
                ownerEmail, BuildRunUrl(run.WorkflowRunId));

            // Track completed nodes across the polling loop so the resolver
            // can evaluate {{ steps.X.output.Y }} for each new step.
            var completedOutputs = new Dictionary<string, StepResult>();

            // Whether a failure has ended the walk. Once set, nothing further is dispatched.
            //
            // Deliberately a state of the LOOP and not a status on a row: it does not cancel
            // work already claimed by a worker, does not touch rows it does not own, and does
            // not race anyone. Nodes that never ran are recorded `skipped` at finalization by
            // the pass that already handles every unreached node.
            var stopped = false;
            var stopOnFailure = run.StopOnFailure ?? true;
            var enqueuedNodeIds = new HashSet<string>();

            // Idempotent replay: if this orchestration is a re-claim of a
            // previously-running orchestrator (lease expired, worker
            // crashed, etc.), the existing step_runs are the source of
            // truth. Rebuilding state from the DB keeps EnqueueStepAsync
            // from creating a second copy of __start__ / fetch / gather
            // and downstream steps that previously caused the
            // hostname_collision warnings + ssh_failed false positives.
            await HydrateOrchestratorStateAsync(
                db, run, completedOutputs, enqueuedNodeIds, ct);

            // Enqueue any start nodes that aren't already represented.
            // First boot of this run hydrates an empty set; replay finds
            // the start nodes already there and skips re-creation.
            foreach (var node in dag.StartNodes())
            {
                if (enqueuedNodeIds.Contains(node.Id)) continue;
                await EnqueueStepAsync(db, queue, run, dag, node, targets, completedOutputs, enqueuedNodeIds, ct, runContext);
            }
            await db.SaveChangesAsync(ct);

            // Poll until all steps are terminal or we time out.
            var deadline = DateTime.UtcNow.AddSeconds(_options.OrchestrationTimeoutSeconds);
            var pollInterval = TimeSpan.FromMilliseconds(_options.PollIntervalMs);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                // Operator-initiated cancel: another request flipped the
                // run row to 'cancelled' between iterations. Reload and
                // exit the loop so we don't enqueue any further DAG nodes.
                // The post-loop finalize block honors the cancelled status
                // instead of overwriting it with completed/failed.
                await db.Entry(run).ReloadAsync(ct);
                if (run.Status == RunStatus.Cancelled)
                {
                    _logger.LogInformation(
                        "Run {RunId} cancelled by operator — stopping orchestration",
                        workflowRunId);
                    break;
                }

                // Lease heartbeat: push the orchestrator job's lease forward
                // so JobReclaim doesn't resurrect this run under another
                // worker while we're still healthy. Without this, any
                // orchestration > 5 min got reclaimed mid-flight and the
                // second worker re-enqueued __start__/gather-* duplicates.
                if (jobId.HasValue && workerId is not null && DateTime.UtcNow >= nextHeartbeatAt)
                {
                    var alive = await queue.RenewLeaseAsync(
                        jobId.Value, workerId, heartbeatLeaseSeconds, ct);
                    if (!alive)
                    {
                        // Our claim was already reclaimed and someone else
                        // owns the orchestration now. Bail out so we don't
                        // race the new owner on the same run's state.
                        _logger.LogWarning(
                            "Run {RunId} lost claim (job {JobId}) — yielding to new owner",
                            workflowRunId, jobId);
                        return;
                    }
                    nextHeartbeatAt = DateTime.UtcNow + heartbeatInterval;
                }

                var steps = await db.StepRuns
                    .AsNoTracking()
                    .Where(s => s.WorkflowRunId == workflowRunId && s.IsActive)
                    .ToListAsync(ct);

                // Group by NodeId and detect nodes whose step_runs are ALL
                // terminal but haven't been processed yet. With per-device
                // fan-out a single node can have multiple step_runs; we
                // must wait for every one before declaring the node done.
                var nodeGroups = steps
                    .GroupBy(s => s.NodeId)
                    .Where(g => !completedOutputs.ContainsKey(g.Key)
                                && g.All(s => IsTerminal(s.Status)))
                    .ToList();

                foreach (var group in nodeGroups)
                {
                    var nodeSteps = group.ToList();

                    // Aggregate status: any failed step fails the node, any
                    // skipped-only set skips the node, else completed.
                    var groupFailed = nodeSteps.Any(s => s.Status == StepStatus.Failed);
                    var allSkipped = nodeSteps.All(s => s.Status == StepStatus.Skipped);

                    // Aggregate output: single step_run returns its own
                    // payload (keeps {{ steps.X.output.Y }} templates
                    // working for the common case). Multi-step fan-out
                    // wraps into the per-device envelope so downstream
                    // templates can index per-device if needed.
                    JsonElement aggregatedOutput;
                    if (nodeSteps.Count == 1)
                    {
                        aggregatedOutput = nodeSteps[0].OutputPayload;
                    }
                    else
                    {
                        var deviceIds = nodeSteps
                            .Where(s => s.DeviceId.HasValue)
                            .Select(s => s.DeviceId!.Value)
                            .Distinct()
                            .ToList();
                        var names = deviceIds.Count > 0
                            ? await db.Devices
                                .AsNoTracking()
                                .Where(d => deviceIds.Contains(d.DeviceId))
                                .Select(d => new { d.DeviceId, d.DeviceName })
                                .ToDictionaryAsync(x => x.DeviceId, x => x.DeviceName, ct)
                            : new Dictionary<Guid, string>();
                        aggregatedOutput = AggregatePerDeviceOutput(nodeSteps, names, _logger, group.Key);
                    }

                    if (SecretMarkers.ContainsAny(aggregatedOutput))
                        _logger.LogInformation(
                            "engine.step.secret_marker_neutralized workflow_run_id={WorkflowRunId} node_id={NodeId} source=step_output",
                            run.WorkflowRunId, group.Key);
                    completedOutputs[group.Key] = new StepResult(SecretMarkers.Neutralize(aggregatedOutput));

                    // Non-conditional edges: fire on matching result
                    // (success/failure/always). Use the aggregate status.
                    var result = groupFailed ? "failure" : (allSkipped ? "skipped" : "success");

                    // execution/SPEC.md §1: with stop_on_failure the walk stops at the first
                    // failed node *whose failure is not consumed by a `failure` edge*.
                    //
                    // ORDER IS THE WHOLE THING HERE. The obvious implementation tests
                    // `failed && stopOnFailure` and returns, before asking the graph anything
                    // — and Nashira shipped exactly that, which short-circuited the very rule
                    // that makes a `failure` edge mean anything: no notify-on-failure or
                    // compensation node ever ran, and `{{ run.failed_step_* }}` was
                    // unreachable by design. So: the node failed, ASK THE GRAPH, and only
                    // then stop.
                    //
                    // The stop is set before the edges fire, so an `always` edge out of this
                    // node does not fire either. An `always` edge says "either way", which is
                    // a statement about the edge; only a `failure` edge is the author saying
                    // what to do about the failure.
                    if (groupFailed && stopOnFailure && !ConsumesFailure(dag, group.Key))
                    {
                        stopped = true;
                        _logger.LogInformation(
                            "Run {RunId} stopping at node {NodeId}: it failed and no failure edge leaves it",
                            workflowRunId, group.Key);
                    }

                    // What downstream notify-failure nodes report via
                    // `{{ run.failed_step_id }}` / `{{ run.failed_step_error }}`.
                    // Null when the node succeeded, so `always` edges from a
                    // healthy node don't claim a failure that never happened.
                    var failedNodeId = groupFailed ? group.Key : null;
                    var failedNodeError = groupFailed ? FirstStepError(nodeSteps) : null;

                    if (stopped) continue;

                    foreach (var next in dag.NextNodes(group.Key, result))
                    {
                        if (enqueuedNodeIds.Contains(next.Id)) continue;
                        await EnqueueStepAsync(db, queue, run, dag, next, targets, completedOutputs, enqueuedNodeIds, ct, runContext,
                            failedNodeId, failedNodeError);
                    }

                    // Conditional edges: evaluate the expression against completed outputs.
                    //
                    // Only out of a node that SUCCEEDED. A conditional edge is a `success`
                    // edge with an extra question attached, so a failed source answers it
                    // before the expression is ever read (execution/SPEC.md §1) — and a node
                    // that was itself skipped has no outputs for the expression to read.
                    //
                    // This guard used to be missing, which made conditional edges FAIL OPEN:
                    // `1 == 1` on an edge out of a crashed step fired it, and the workflow
                    // walked on as if the step had succeeded. The failure edge beside it fired
                    // too, so a run could take both the failure branch and the happy branch
                    // out of the same failed node. Found by `executor.conditional
                    // .source_failure_blocks_a_true_condition`.
                    if (result == "success" && dag.Adjacency.TryGetValue(group.Key, out var outEdges))
                    {
                        foreach (var edge in outEdges.Where(e => e.EdgeType == "conditional"))
                        {
                            if (enqueuedNodeIds.Contains(edge.Target)) continue;
                            // The same two objects EnqueueStepAsync feeds to
                            // the resolver just below — run input and run
                            // context as steps see them, secret markers
                            // neutralized — so a condition and the payload it
                            // gates can never disagree about the same run
                            // (SPEC §9). deviceContext stays null:
                            // an edge fires once at DAG level after its
                            // source completes, so there is no single
                            // current device to bind `{{ device.X }}` to.
                            if (!string.IsNullOrWhiteSpace(edge.Condition)
                                && _conditions.Evaluate(
                                    edge.Condition, completedOutputs,
                                    deviceContext: null,
                                    runInput: RunInputForSteps(run),
                                    runContext: runContext is { } rc
                                        ? SecretMarkers.Neutralize(rc)
                                        : runContext))
                            {
                                await EnqueueStepAsync(db, queue, run, dag, dag.Nodes[edge.Target],
                                    targets, completedOutputs, enqueuedNodeIds, ct, runContext,
                                    failedNodeId, failedNodeError);
                            }
                        }
                    }
                }

                if (nodeGroups.Count > 0)
                    await db.SaveChangesAsync(ct);

                // Check for step-level timeouts.
                var stepTimeout = TimeSpan.FromSeconds(_options.StepTimeoutSeconds);
                var timedOutAny = false;
                foreach (var step in steps.Where(s => !IsTerminal(s.Status)))
                {
                    if (step.CreatedAt.Add(stepTimeout) < DateTime.UtcNow)
                    {
                        _logger.LogWarning("Step {StepRunId} timed out", step.StepRunId);
                        var tracked = await db.StepRuns.FindAsync(new object[] { step.StepRunId }, ct);
                        if (tracked is not null && !IsTerminal(tracked.Status))
                        {
                            tracked.Status = StepStatus.Failed;
                            tracked.Error = "step timed out";
                            tracked.CompletedAt = DateTime.UtcNow;
                            tracked.UpdatedAt = DateTime.UtcNow;
                            timedOutAny = true;
                        }
                    }
                }
                // Flush the force-fail immediately. The next poll iteration
                // and the run-outcome query below both read through
                // AsNoTracking, so an unsaved timeout would be invisible to
                // them — the run would be reported `completed` while its
                // step row said `failed`, and the DAG's failure edges would
                // never fire.
                if (timedOutAny) await db.SaveChangesAsync(ct);

                // If no in-flight steps remain, we're done.
                var allTerminal = steps.All(s => IsTerminal(s.Status) || completedOutputs.ContainsKey(s.NodeId));
                if (allTerminal && steps.Count > 0 && nodeGroups.Count == 0)
                    break;

                await Task.Delay(pollInterval, ct);
            }

            // Determine run outcome.
            var final = await db.StepRuns
                .AsNoTracking()
                .Where(s => s.WorkflowRunId == workflowRunId && s.IsActive)
                .ToListAsync(ct);

            // If the operator cancelled mid-flight, leave the cancelled
            // status (and CompletedAt) the cancel handler already wrote.
            if (run.Status != RunStatus.Cancelled)
            {
                var anyFailed = final.Any(s => s.Status == StepStatus.Failed);
                run.Status = anyFailed ? RunStatus.Failed : RunStatus.Completed;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                // Every node the walk never reached is part of this run's story too.
                var skipped = RecordSkippedNodes(db, run, final);
                // `Status` says the run finished; this says what it left behind.
                await RecordOutcomeAsync(scope, db, run, [.. final, .. skipped], ct);
                await db.SaveChangesAsync(ct);
            }

            await PropagateChildCompletionAsync(db, run, final, ct);

            _logger.LogInformation(
                "Run {RunId} finished with status={Status}, steps={Total} (failed={Failed})",
                workflowRunId, run.Status, final.Count,
                final.Count(s => s.Status == StepStatus.Failed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Run {RunId} orchestration failed", workflowRunId);
            run.Status = RunStatus.Failed;
            // Record WHY on the run itself. This failure happens outside any
            // step, so without this the UI can only report that the run failed
            // and that no step said anything — which is where this class of bug
            // used to go to die.
            //
            // WorkflowExecutorException messages are written for the author
            // (they name the node and what to do); anything else is an internal
            // fault, so it is summarised by type rather than leaked verbatim.
            run.Error = ex is WorkflowExecutorException
                ? ex.Message
                : $"orchestration failed ({ex.GetType().Name}). See the worker log for run {workflowRunId}.";
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;

            // A run refused before it dispatched anything left nothing behind, so there is
            // nothing to compensate. Saying so matters when this run is somebody's CHILD: a
            // parent reads this field to score its own rollback plan, and a null here reads
            // as "unknown", which the parent then treats conservatively — keeping itself out
            // of a plan on account of a subflow that never started.
            //
            // Only for the empty case. A run that failed PART WAY through leaves the field
            // null on purpose: some of its steps ran, scoring them needs the resolution this
            // path no longer has, and a guess in the unsafe direction is the one thing this
            // whole model exists to remove.
            try
            {
                var ranAnything = await db.StepRuns
                    .AsNoTracking()
                    .AnyAsync(s => s.WorkflowRunId == workflowRunId && s.IsActive, CancellationToken.None);
                if (!ranAnything)
                    run.StrictestTier = RunOutcomeCalculator.ToWire(IdempotencyKind.Idempotent);
            }
            catch { /* best-effort, same as the save below */ }

            try { await db.SaveChangesAsync(CancellationToken.None); } catch { /* best-effort */ }
            try { await PropagateChildCompletionAsync(db, run, finalSteps: null, CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  Run outcome (workflow-v1/run-outcome)
    // ───────────────────────────────────────────────────────────────────

    // Writes a `skipped` step row for every node in the run's graph that never ran.
    //
    // This orchestrator is edge-driven: a node is enqueued when an incoming edge fires, so a
    // node whose edges never fired simply never appeared. Nothing was wrong with that — but
    // nothing RECORDED it either, and a run that shows three rows for a ten-node graph cannot
    // tell "the other seven were skipped" from "the other seven are missing".
    //
    // The evidence was already being read and never written: `IsTerminal`, the node-group
    // aggregation's `allSkipped`, and the CSV export's skipped counter all handle
    // StepStatus.Skipped, and no code path in this product ever produced one. So this adds
    // no vocabulary — it makes the vocabulary that already existed reachable.
    //
    // Written at the END rather than during the walk on purpose: only when the run is over is
    // "this node never ran" a fact rather than a guess about a node that had not run YET.
    private static List<StepRunModel> RecordSkippedNodes(
        AppDbContext db, WorkflowRunModel run, IReadOnlyList<StepRunModel> ran)
    {
        var reached = ran.Select(s => s.NodeId).ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var rows = new List<StepRunModel>();

        if (run.NodesSnapshot.ValueKind != JsonValueKind.Array) return rows;

        foreach (var node in run.NodesSnapshot.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var nodeId = idEl.GetString();
            if (nodeId is null || !reached.Add(nodeId)) continue;

            rows.Add(new StepRunModel
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = run.WorkflowRunId,
                NodeId = nodeId,
                Status = StepStatus.Skipped,
                // It did not run, so it changed nothing. Measured, not defaulted: "skipped"
                // is the one status where the answer is knowable without a handler.
                ChangedState = false,
                // No StartedAt: it never started. CompletedAt is when the run decided so.
                CompletedAt = now,
                InputPayload = default,
                OutputPayload = default,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.StepRuns.AddRange(rows);
        return rows;
    }

    // Scores the finished run: how many steps changed something, whether what they changed
    // could be undone, and — for a failed run — which nodes a reversal would have to visit.
    //
    // Read off `StepRun.ChangedState`, which the WORKER wrote from what each handler
    // measured. That is the whole point of the change signal: before it, this product had
    // no way to tell a `GET` that returned 200 from a `DELETE` that returned 200, so it
    // could not have answered any of these three questions honestly.
    //
    // Best-effort by construction. A run that finished is a fact, and failing to score it
    // must not turn that into a failed run — so the outcome is left null and logged. Null
    // already means "not recorded" for every row written before this model existed.
    private async Task RecordOutcomeAsync(
        IServiceScope scope, AppDbContext db, WorkflowRunModel run,
        IReadOnlyList<StepRunModel> steps, CancellationToken ct)
    {
        try
        {
            var tiers = await ResolveTiersAsync(scope, db, run, steps, ct);

            // Execution order. `StartedAt` is what the rest of this class orders steps by;
            // a step that never started sorts last, where a skipped node belongs.
            var ordered = steps
                .OrderBy(s => s.StartedAt ?? DateTime.MaxValue)
                .Select(s => new StepOutcome(
                    s.NodeId,
                    s.Status == StepStatus.Failed,
                    s.ChangedState == true,
                    tiers.TryGetValue(s.NodeId, out var t) ? t : IdempotencyKind.RequiresCompensation))
                .ToList();

            var outcome = RunOutcomeCalculator.Compute(ordered);
            run.FinalState = outcome.FinalState;
            run.ChangedCount = outcome.ChangedCount;
            run.RollbackPlanJson = JsonSerializer.Serialize(outcome.RollbackPlan);

            // The strictest tier among the nodes this run EXECUTED, for one reader: the
            // `subflow` step in a parent run, which cannot work it out for itself.
            //
            // Skipped nodes are excluded — nothing ran, so nothing about them constrains a
            // parent — and a run in which nothing ran at all is `idempotent` by the same
            // argument: it left no side effect to compensate.
            var executed = ordered.Where(o => !Skipped(steps, o.NodeId)).ToList();
            run.StrictestTier = RunOutcomeCalculator.ToWire(
                executed.Count == 0 ? IdempotencyKind.Idempotent : executed.Max(o => o.Tier));

            // One audit.v1 event per node that CHANGED something — the executed mutations,
            // which is a different set from the steps that ran. New behaviour in this
            // product, and the first thing here implemented because the shared contract
            // asked for it rather than because a feature did.
            var audit = scope.ServiceProvider.GetService<IAuditLogger>();
            if (audit is not null)
            {
                var ops = NodeSnippetIds(run.NodesSnapshot);
                var now = DateTime.UtcNow;
                foreach (var step in ordered.Where(o => o.Changed).DistinctBy(o => o.NodeId))
                {
                    var e = new AuditV1Event(
                        "audit.v1", Guid.NewGuid(), run.WorkflowId, run.SchemaHash ?? string.Empty,
                        step.NodeId,
                        ops.TryGetValue(step.NodeId, out var op) ? op : string.Empty,
                        RunOutcomeCalculator.ToWire(step.Tier),
                        run.CreatedBy ?? string.Empty,
                        Guid.Empty, now, "changed");

                    // The event travels whole as the audit row's `after`, rather than being
                    // flattened into columns. The eleven members ARE the contract; a row that
                    // kept only the ones this product's audit schema happens to have would be
                    // a different record that merely looks like one.
                    await audit.LogAsync("workflow.node", run.WorkflowId, e.Op, after: e, ct: ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Run {RunId} finished but its outcome could not be scored; final_state left unrecorded",
                run.WorkflowRunId);
        }
    }

    private static bool Skipped(IReadOnlyList<StepRunModel> steps, string nodeId) =>
        steps.Where(s => s.NodeId == nodeId).All(s => s.Status == StepStatus.Skipped);

    // node id -> the tier that applies to it, decided by the one function that decides it
    // (`WorkflowRollbackAnalyzer.EffectiveKind`): the handler's floor, the snippet's own
    // tier below that ceiling, and the node's stricter-only override on top.
    private static async Task<Dictionary<string, IdempotencyKind>> ResolveTiersAsync(
        IServiceScope scope, AppDbContext db, WorkflowRunModel run,
        IReadOnlyList<StepRunModel> steps, CancellationToken ct)
    {
        var snippetIds = steps.Where(s => s.SnippetId.HasValue)
            .Select(s => s.SnippetId!.Value).Distinct().ToList();
        // activeOnly is not applied: a run may reference a snippet that has since been
        // soft-deleted, and its tier is still the tier the step ran under.
        var snippets = snippetIds.Count == 0
            ? new List<SnippetModel>()
            : await db.Snippets.AsNoTracking()
                .Where(s => snippetIds.Contains(s.SnippetId)).ToListAsync(ct);
        var byId = snippets.ToDictionary(s => s.SnippetId);

        var handlerKindByType = scope.ServiceProvider.GetServices<ISnippetHandler>()
            .GroupBy(h => h.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DefaultIdempotency, StringComparer.OrdinalIgnoreCase);

        var nodeOverrides = NodeIdempotencyOverrides(run.NodesSnapshot);

        var tiers = new Dictionary<string, IdempotencyKind>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (tiers.ContainsKey(step.NodeId)) continue;

            // A step that recorded its own tier is believed, and nothing recomputes it. This
            // is how a `subflow` node's tier — which belongs to the child run and is not
            // derivable from this graph — survives to the rollback plan.
            if (WorkflowRollbackAnalyzer.ParseTier(step.Tier) is { } recorded)
            {
                tiers[step.NodeId] = recorded;
                continue;
            }

            if (!step.SnippetId.HasValue || !byId.TryGetValue(step.SnippetId.Value, out var snippet)) continue;

            // An unregistered type is scored RequiresCompensation, the conservative
            // reading: it may have changed something and nobody can say it was safe.
            var handlerKind = handlerKindByType.TryGetValue(snippet.Type, out var hk)
                ? hk
                : IdempotencyKind.RequiresCompensation;

            nodeOverrides.TryGetValue(step.NodeId, out var declared);
            tiers[step.NodeId] = WorkflowRollbackAnalyzer.EffectiveKind(
                handlerKind, snippet.Idempotency, declared);
        }
        return tiers;
    }

    // node id -> the node's `snippet_id`, verbatim as the graph spells it.
    //
    // `op` in an audit.v1 event is the snippet id as a STRING, not a parsed Guid: the
    // conformance graphs use `__start__` and `__end__` as snippet ids, and a reader
    // that insisted on a Guid would drop exactly the nodes a control-flow test is about.
    private static Dictionary<string, string> NodeSnippetIds(JsonElement nodes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (nodes.ValueKind != JsonValueKind.Array) return map;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;

            // The editor writes `snippet_id` at the top level; some exporters nest it
            // under `data`. Same two shapes WorkflowRollbackAnalyzer.ParseGraph accepts.
            var snippetId = node.TryGetProperty("snippet_id", out var sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString()
                : node.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("snippet_id", out var dsn) && dsn.ValueKind == JsonValueKind.String
                    ? dsn.GetString()
                    : null;

            if (snippetId is { Length: > 0 }) map[idEl.GetString()!] = snippetId;
        }
        return map;
    }

    private static Dictionary<string, string> NodeIdempotencyOverrides(JsonElement nodes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (nodes.ValueKind != JsonValueKind.Array) return map;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            if (node.TryGetProperty("config_overrides", out var ov)
                && ov.ValueKind == JsonValueKind.Object
                && ov.TryGetProperty("idempotency", out var tier)
                && tier.ValueKind == JsonValueKind.String
                && tier.GetString() is { Length: > 0 } raw)
                map[idEl.GetString()!] = raw;
        }
        return map;
    }

    // Whether the author wired a `failure` edge out of this node — i.e. whether the graph
    // says what to do when it fails. That, and only that, is what lets the walk continue past
    // a failure under stop_on_failure.
    //
    // A property of the GRAPH, not of the run: it asks what the author wrote, not what
    // happened. Copied from Nashira's function of the same name.
    private static bool ConsumesFailure(Dag dag, string nodeId) =>
        dag.Edges.Any(e => e.EdgeType == "failure" && string.Equals(e.Source, nodeId, StringComparison.Ordinal));

    // When this run is a subflow's child (ParentRunId set), update the
    // parent's step_run that invoked us so the parent's polling loop can
    // mark the subflow node terminal and advance the DAG. The link is
    // `StepRun.ChildRunId = childRun.WorkflowRunId` — set when the
    // parent enqueues the subflow. No-op for top-level runs.
    // The subflow step's output (execution/SPEC.md §5): the child run's identity and verdict,
    // plus each of its steps' output keyed by node id, so a downstream template reads
    // `{{ steps.<subflow-node>.output.steps.<child-node>.… }}`.
    //
    // BREAKING relative to what this product used to emit, which was the steps map ALONE at
    // the top level — `{{ steps.<subflow-node>.output.<child-node>.… }}`, one level shallower
    // and with no way to ask whether the child actually succeeded. A template reading the old
    // shape has to gain `.steps`. No dual-shape shim: a node answering to two shapes makes the
    // contract's shape optional in practice, and the vectors would pass while nothing had
    // committed to it.
    private static JsonElement SubflowOutput(
        WorkflowRunModel childRun, IReadOnlyList<StepRunModel>? steps)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("run_id", childRun.WorkflowRunId);
            w.WriteString("status", childRun.Status);
            // Null rather than absent when the child never got far enough to be scored: a
            // template reading it gets a null, not an unresolved reference that fails a step.
            if (childRun.FinalState is { Length: > 0 } fs) w.WriteString("final_state", fs);
            else w.WriteNull("final_state");
            if (childRun.Error is { Length: > 0 } err) w.WriteString("error", err);

            w.WritePropertyName("steps");
            w.WriteStartObject();
            foreach (var grp in (steps ?? []).GroupBy(x => x.NodeId, StringComparer.Ordinal))
            {
                w.WritePropertyName(grp.Key);
                var output = grp.First().OutputPayload;
                if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    w.WriteNullValue();
                else
                    output.WriteTo(w);
            }
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private async Task PropagateChildCompletionAsync(
        AppDbContext db,
        WorkflowRunModel childRun,
        IReadOnlyList<StepRunModel>? finalSteps,
        CancellationToken ct)
    {
        if (childRun.ParentRunId is not Guid parentRunId) return;

        var parentStep = await db.StepRuns
            .Where(s => s.WorkflowRunId == parentRunId
                     && s.ChildRunId == childRun.WorkflowRunId
                     && s.IsActive)
            .FirstOrDefaultAsync(ct);
        if (parentStep is null)
        {
            _logger.LogWarning(
                "engine.subflow.parent_step_missing child_run_id={ChildRunId} parent_run_id={ParentRunId}",
                childRun.WorkflowRunId, parentRunId);
            return;
        }

        // Idempotent: if the parent step was already terminalised by an
        // earlier orchestrator (rare reclaim path), skip.
        if (IsTerminal(parentStep.Status)) return;

        parentStep.Status = childRun.Status switch
        {
            RunStatus.Completed => StepStatus.Completed,
            RunStatus.Failed    => StepStatus.Failed,
            RunStatus.Cancelled => StepStatus.Failed,
            _                   => StepStatus.Failed,
        };
        parentStep.CompletedAt = childRun.CompletedAt ?? DateTime.UtcNow;
        parentStep.UpdatedAt = DateTime.UtcNow;

        // The subflow node changed something if the child did. It is the one node whose own
        // handler cannot answer, because it has no handler — the child run is the action.
        parentStep.ChangedState = childRun.ChangedCount > 0;

        // The child's strictest tier becomes the parent step's, and this is the load-bearing
        // line of the whole subflow story: a child that sent an email is not reversible, and a
        // parent that listed this node in its rollback plan would report `rolled_back` for a
        // run whose effects are still out there.
        //
        // Stricter-only, matching the ceiling rule everywhere else: if the parent's node was
        // already stricter than the child, the parent's stands. A parent may raise, never lower.
        var childTier = WorkflowRollbackAnalyzer.ParseTier(childRun.StrictestTier)
            ?? IdempotencyKind.RequiresCompensation;
        var ownTier = WorkflowRollbackAnalyzer.ParseTier(parentStep.Tier);
        parentStep.Tier = RunOutcomeCalculator.ToWire(
            ownTier is { } own && own > childTier ? own : childTier);

        // Surface the child's failed-step error on the parent step so
        // /admin/audit and the runs UI show why the subflow blew up.
        if (parentStep.Status == StepStatus.Failed)
        {
            var firstFailed = finalSteps?.FirstOrDefault(s => s.Status == StepStatus.Failed);
            // The child ran and failed. `subflow_missing` is the OTHER thing — a child that
            // never existed — and the two are the same sentence to a reader but completely
            // different next steps to an operator: one says go read the child run, the other
            // says there is no child run to read.
            parentStep.ErrorCode = "subflow_failed";
            parentStep.Error = firstFailed?.Error is { Length: > 0 } err
                ? $"subflow run {childRun.WorkflowRunId} failed at step {firstFailed!.NodeId}: {err}"
                : $"subflow run {childRun.WorkflowRunId} ended with status={childRun.Status}";
        }

        parentStep.OutputPayload = SubflowOutput(childRun, finalSteps);

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "engine.subflow.propagated child_run_id={ChildRunId} parent_run_id={ParentRunId} parent_step_id={ParentStepId} status={Status}",
            childRun.WorkflowRunId, parentRunId, parentStep.StepRunId, parentStep.Status);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Replay hydration
    // ───────────────────────────────────────────────────────────────────

    // Reads existing step_runs for the run and seeds the orchestrator's
    // in-memory state (enqueuedNodeIds, completedOutputs) from them. Called
    // once at orchestration start so a re-claimed run resumes from where
    // its first orchestrator left off instead of re-creating start nodes.
    //
    // Two pieces of state matter:
    //   - enqueuedNodeIds: every node that already has at least one
    //     step_run row. Prevents EnqueueStepAsync from creating a second
    //     copy of nodes that were already scheduled.
    //   - completedOutputs: nodes whose step_runs are ALL terminal. The
    //     polling loop normally fills this as it processes node groups;
    //     pre-filling here means the loop's first iteration won't re-fire
    //     the success/failure edges for already-completed nodes.
    private async Task HydrateOrchestratorStateAsync(
        AppDbContext db,
        WorkflowRunModel run,
        Dictionary<string, StepResult> completedOutputs,
        HashSet<string> enqueuedNodeIds,
        CancellationToken ct)
    {
        var existing = await db.StepRuns
            .AsNoTracking()
            .Where(s => s.WorkflowRunId == run.WorkflowRunId && s.IsActive)
            .ToListAsync(ct);
        if (existing.Count == 0) return;

        foreach (var nodeId in existing.Select(s => s.NodeId).Distinct())
            enqueuedNodeIds.Add(nodeId);

        var groups = existing.GroupBy(s => s.NodeId).ToList();
        foreach (var group in groups)
        {
            var nodeSteps = group.ToList();
            if (!nodeSteps.All(s => IsTerminal(s.Status))) continue;

            JsonElement aggregatedOutput;
            if (nodeSteps.Count == 1)
            {
                aggregatedOutput = nodeSteps[0].OutputPayload;
            }
            else
            {
                var deviceIds = nodeSteps
                    .Where(s => s.DeviceId.HasValue)
                    .Select(s => s.DeviceId!.Value)
                    .Distinct()
                    .ToList();
                var names = deviceIds.Count > 0
                    ? await db.Devices
                        .AsNoTracking()
                        .Where(d => deviceIds.Contains(d.DeviceId))
                        .Select(d => new { d.DeviceId, d.DeviceName })
                        .ToDictionaryAsync(x => x.DeviceId, x => x.DeviceName, ct)
                    : new Dictionary<Guid, string>();
                aggregatedOutput = AggregatePerDeviceOutput(nodeSteps, names, _logger, group.Key);
            }

            completedOutputs[group.Key] = new StepResult(SecretMarkers.Neutralize(aggregatedOutput));
        }

        _logger.LogInformation(
            "engine.orchestrator.replay_hydrated workflow_run_id={WorkflowRunId} known_nodes={Nodes} completed_nodes={Completed}",
            run.WorkflowRunId, enqueuedNodeIds.Count, completedOutputs.Count);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Step creation
    // ───────────────────────────────────────────────────────────────────

    private async Task EnqueueStepAsync(
        AppDbContext db,
        IQueueRepository queue,
        WorkflowRunModel run,
        Dag dag,
        WorkflowNode node,
        List<Guid> resolvedTargetIds,
        IReadOnlyDictionary<string, StepResult> completedOutputs,
        HashSet<string> enqueuedNodeIds,
        CancellationToken ct,
        JsonElement? runContext = null,
        string? failedStepId = null,
        string? failedStepError = null)
    {
        enqueuedNodeIds.Add(node.Id);

        // This node fired from a failing predecessor: let its templates name
        // the culprit. Overlaid per enqueue rather than baked into the shared
        // run context, because two independent risky nodes can both route to
        // the same notify-failure sink and each alert must name its own.
        if (runContext.HasValue && failedStepId is not null)
            runContext = WithFailedStep(runContext.Value, failedStepId, failedStepError);
        // failed_step_error quotes whatever the failing handler reported.
        if (runContext.HasValue)
            runContext = SecretMarkers.Neutralize(runContext.Value);

        // Sentinel nodes (__start__, __end__) get an immediately-completed
        // step_run with empty output. The orchestrator processes them in the
        // same poll cycle so downstream nodes fire without delay.
        if (node.SnippetId is "__start__" or "__end__")
        {
            var sentinel = MakeStepRun(run, node, snippetId: null, resolvedInput: default);
            sentinel.Status = StepStatus.Completed;
            sentinel.CompletedAt = DateTime.UtcNow;
            db.StepRuns.Add(sentinel);
            return;
        }

        // Subflow nodes spawn a child WorkflowRun for the referenced
        // workflow. The child run's output (once complete) becomes this
        // step's output. The orchestrator polls the child run via the
        // step_run's status, which the child executor sets on completion.
        if (node.SnippetId == "subflow")
        {
            await EnqueueSubflowAsync(db, queue, run, node, completedOutputs, ct);
            return;
        }

        if (!Guid.TryParse(node.SnippetId, out var svcId))
        {
            // Compatibility shim for workflows saved with an EDITOR MARKER in
            // snippet_id instead of a real id. The canvas represents an
            // integration_action node as the literal string "integration_action"
            // while you are editing, and that marker used to be persisted
            // verbatim — the reference validator skipped every non-GUID id as
            // "a sentinel, handled elsewhere", and nothing handled this one, so
            // the workflow saved cleanly and died here at run time with an
            // opaque `invalid snippet_id`.
            //
            // The marker happens to equal the seeded snippet's Type, so it can
            // be resolved rather than rejected. New saves write the real id (the
            // canvas maps it now); this keeps already-saved workflows running
            // without anyone having to re-open and re-save them.
            var byType = await db.Snippets
                .AsNoTracking()
                .Where(s => s.IsActive && s.Type == node.SnippetId)
                .Select(s => (Guid?)s.SnippetId)
                .FirstOrDefaultAsync(ct);

            if (byType is not Guid resolved)
                throw new WorkflowExecutorException(
                    $"node `{node.Id}` has snippet_id '{node.SnippetId}', which is neither a snippet id "
                    + "nor a known node type. Open the workflow, re-select the node's service, and save.");

            _logger.LogWarning(
                "engine.node.legacy_snippet_marker run_id={RunId} node_id={NodeId} marker={Marker} resolved_snippet_id={SnippetId} "
                + "— saved before the editor wrote real ids; re-saving the workflow removes this lookup.",
                run.WorkflowRunId, node.Id, node.SnippetId, resolved);

            svcId = resolved;
        }

        // Look up target_mode so we can decide between one step_run per
        // node (target_mode=once) or one step_run per device
        // (target_mode=per_device). Fallback to "once" when the snippet
        // doesn't declare a mode — preserves the old behavior.
        // NOTE: we do this BEFORE resolving templates because per_device
        // fan-out needs to resolve once per device (each with its own
        // scoped view of upstream per-device outputs, see ScopeOutputsToDevice).
        var snippetMeta = await db.Snippets
            .AsNoTracking()
            .Where(s => s.SnippetId == svcId)
            .Select(s => new { s.TargetMode, s.Type })
            .FirstOrDefaultAsync(ct);
        var targetMode = snippetMeta?.TargetMode;
        var snippetType = snippetMeta?.Type;
        var perDevice = string.Equals(targetMode, "per_device", StringComparison.OrdinalIgnoreCase)
                        && resolvedTargetIds.Count > 0;
        // python_snippet authors expect `inp['steps']['<id>']['output']`
        // (ergonomic structured access). Other handlers receive their step
        // outputs via `{{ … }}` templates inside `config_overrides` and
        // don't need the bag injected — saves bytes on InputPayload.
        var injectSteps = string.Equals(snippetType, "python_snippet", StringComparison.OrdinalIgnoreCase);

        // python_snippet steps run inside the bwrap sandbox, which only works
        // in a container whose seccomp/AppArmor is relaxed (the compose
        // `worker` service). Route them to the dedicated `sandbox` queue tag
        // so a hardened container (the API service keeps Docker's default
        // seccomp) never claims one and fails with the misleading
        // "No permissions to create new namespace". Every other step type
        // stays on `default` and can run anywhere.
        var stepTag = injectSteps ? "sandbox" : "default";

        // Prefetch Device rows we'll need for `{{ device.X }}` template
        // resolution. For per_device fan-out we need every target; for
        // `once` with a single target the lookup is still cheap and lets
        // that step reference its target's device context unambiguously.
        // `once` + multiple targets has no single current device — we
        // skip the prefetch and `{{ device.X }}` stays literal.
        var deviceContextsById = new Dictionary<Guid, JsonElement>();
        var shouldLoadDeviceContext = resolvedTargetIds.Count > 0
                                      && (perDevice || resolvedTargetIds.Count == 1);
        if (shouldLoadDeviceContext)
        {
            var deviceIdsToLoad = perDevice
                ? resolvedTargetIds
                : new List<Guid> { resolvedTargetIds[0] };
            var deviceRows = await db.Devices.AsNoTracking()
                .Where(d => deviceIdsToLoad.Contains(d.DeviceId))
                .ToListAsync(ct);
            foreach (var d in deviceRows)
                deviceContextsById[d.DeviceId] = SecretMarkers.Neutralize(BuildDeviceContext(d));
        }

        // Build the step's input: merge run.InputPayload + node.ConfigOverrides,
        // then resolve {{ steps.X.output.Y }} and {{ device.X }} templates
        // using already-completed steps and the current device context.
        // This means the step handler will see a fully resolved payload.
        var stepRunInput = RunInputForSteps(run);
        // Silent rewriting is hard to debug: say so once per step when it happened.
        if (SecretMarkers.ContainsAny(run.InputPayload) && !IsSubflowChild(run))
            _logger.LogInformation(
                "engine.step.secret_marker_neutralized workflow_run_id={WorkflowRunId} node_id={NodeId} source=run_input",
                run.WorkflowRunId, node.Id);
        var merged = MergePayloads(stepRunInput, node.ConfigOverrides);

        JsonElement? DeviceContextFor(Guid? deviceId) =>
            deviceId.HasValue && deviceContextsById.TryGetValue(deviceId.Value, out var ctx)
                ? ctx
                : (JsonElement?)null;

        // Resolve templates with two independent device-aware knobs:
        //   - scopeUpstream: unwraps upstream per_device aggregate outputs
        //     (shape `{ devices: [ {device_id, status, output}, ... ] }`)
        //     to the matching device's own `output`. Only makes sense for
        //     per_device CURRENT steps — otherwise the caller might want
        //     the aggregate envelope (once-mode handlers often loop over
        //     `_targets` and expect upstream outputs in their raw shape).
        //   - deviceContextId: the device whose metadata feeds
        //     `{{ device.X }}` templates. Unambiguous any time there is
        //     exactly one current device, regardless of target_mode.
        // Together they make
        //   {{ steps.ssh-lldp.output.stdout }}
        //   {{ device.name }}
        // both resolve naturally in the common per_device-after-per_device
        // pipeline shown by the LLDP sync workflow.
        // Returns the resolved payload AND a list of any residual templates
        // that didn't resolve (step not found, path miss, wrong grammar).
        // Callers decide how to surface the failure — this helper stays
        // pure so the polling loop can test it too.
        (JsonElement Payload, IReadOnlyList<VariableResolver.UnresolvedTemplate> Unresolved)
            BuildResolvedInput(Guid? scopeUpstreamDeviceId, Guid? deviceContextId)
        {
            var scoped = scopeUpstreamDeviceId.HasValue
                ? ScopeOutputsToDevice(completedOutputs, scopeUpstreamDeviceId.Value)
                : completedOutputs;
            var resolved = _resolver.Resolve(merged, scoped, DeviceContextFor(deviceContextId), stepRunInput, runContext);
            // Inject the full target list into the input regardless of mode so
            // handlers that want to do their own fan-out (legacy behavior) keep
            // working. For per_device fan-out we also set StepRun.DeviceId on
            // each row so per-device handlers (ping, ssh, ansible) resolve
            // Device.IpAddress without touching InputPayload._targets.
            if (resolvedTargetIds.Count > 0)
                resolved = InjectTargets(resolved, resolvedTargetIds);
            // python_snippet: stamp `steps` so authors can read upstream
            // outputs structurally without manually templating each one
            // through config_overrides (which is the source of "empty
            // aggregator" bugs — a snippet doing `inp['steps']['x']['output']`
            // sees `None` if the author forgot to plumb the reference).
            if (injectSteps)
                resolved = InjectSteps(resolved, scoped);
            // python_snippet: also stamp the current device so a per_device
            // script can read inp['device']['name'] / ['platform'] / etc.
            // directly — the shape authors intuitively reach for — instead of
            // threading {{ device.name }} through config_overrides and hoping
            // the key matches (the unknown-device.json bug). Same object that
            // feeds {{ device.X }}.
            if (injectSteps && DeviceContextFor(deviceContextId) is { } deviceCtx)
                resolved = InjectDevice(resolved, deviceCtx);
            var unresolved = VariableResolver.FindUnresolvedTemplates(resolved);
            return (resolved, unresolved);
        }

        // Central "a template reference is broken" path. Instead of
        // enqueueing a job whose handler would then emit garbage (the
        // dns-reconcile incident: literal `{{ steps.X.output.rows[0] }}`
        // showing up as CSV cells), we persist a pre-failed StepRun with
        // the exact list of offending fields. The DAG's failure edges —
        // if any — fire as normal.
        void PersistFailedStepForUnresolvedTemplates(
            Guid? deviceId,
            JsonElement attemptedInput,
            IReadOnlyList<VariableResolver.UnresolvedTemplate> unresolved)
        {
            var errorLines = unresolved
                .Take(10) // cap so the error string doesn't explode on pathological inputs
                .Select(u => $"  - {u.Location}: {u.TemplateText}")
                .ToList();
            if (unresolved.Count > errorLines.Count)
                errorLines.Add($"  … and {unresolved.Count - errorLines.Count} more");

            var errorMessage =
                "unresolved template references — this step references step outputs or device "
                + "fields that don't exist or didn't resolve. Fix the workflow config so every "
                + $"`{{{{ ... }}}}` points at a real upstream step/path:\n{string.Join("\n", errorLines)}";

            var failedStep = MakeStepRun(run, node, snippetId: svcId, resolvedInput: attemptedInput, deviceId: deviceId);
            failedStep.Status = StepStatus.Failed;
            failedStep.ErrorCode = "unresolved_template";
            failedStep.Error = errorMessage;
            // It never reached its handler, so nothing happened.
            failedStep.ChangedState = false;
            failedStep.StartedAt = DateTime.UtcNow;
            failedStep.CompletedAt = DateTime.UtcNow;
            db.StepRuns.Add(failedStep);

            _logger.LogError(
                "engine.step.unresolved_templates workflow_run_id={WorkflowRunId} node_id={NodeId} device_id={DeviceId} unresolved_count={Count}",
                run.WorkflowRunId, node.Id, deviceId, unresolved.Count);
        }

        // A python step receives the credentials of every integration its payload
        // names under `*_integration_id`. Only the workflow's author may choose
        // those (see AuthoredIntegrationKeys); one chosen by run input or by a
        // template fails the step before any credential is handed out.
        IReadOnlyList<string> ForeignIntegrationKeys(JsonElement resolvedInput)
            => injectSteps
                ? AuthoredIntegrationKeys.Unauthored(
                    resolvedInput, node.ConfigOverrides,
                    IsSubflowChild(run) ? run.InputPayload : null)
                : Array.Empty<string>();

        void PersistFailedStepForForeignIntegrations(
            Guid? deviceId, JsonElement attemptedInput, IReadOnlyList<string> keys)
        {
            var failedStep = MakeStepRun(run, node, snippetId: svcId, resolvedInput: attemptedInput, deviceId: deviceId);
            failedStep.Status = StepStatus.Failed;
            failedStep.ErrorCode = "integration_not_authored";
            failedStep.Error =
                $"{string.Join(", ", keys)}: a python step only receives integration credentials for "
                + "integrations named as a literal id in its own node config. This value came from the "
                + "run input or a template. Set the integration id directly in the node's config_overrides.";
            failedStep.ChangedState = false;
            failedStep.StartedAt = DateTime.UtcNow;
            failedStep.CompletedAt = DateTime.UtcNow;
            db.StepRuns.Add(failedStep);

            _logger.LogWarning(
                "engine.step.integration_not_authored workflow_run_id={WorkflowRunId} node_id={NodeId} device_id={DeviceId} keys={Keys}",
                run.WorkflowRunId, node.Id, deviceId, string.Join(",", keys));
        }

        if (perDevice && resolvedTargetIds.Count > 1)
        {
            // Fan-out: one step_run per device, each with its own DeviceId
            // AND its own device-scoped template resolution. Downstream
            // gating waits for ALL per-device step_runs of the node to
            // reach a terminal state (see polling loop).
            foreach (var deviceId in resolvedTargetIds)
            {
                var (fanInput, fanUnresolved) = BuildResolvedInput(deviceId, deviceId);
                if (fanUnresolved.Count > 0)
                {
                    PersistFailedStepForUnresolvedTemplates(deviceId, fanInput, fanUnresolved);
                    continue;
                }
                if (ForeignIntegrationKeys(fanInput) is { Count: > 0 } fanForeign)
                {
                    PersistFailedStepForForeignIntegrations(deviceId, fanInput, fanForeign);
                    continue;
                }
                var fanStep = MakeStepRun(run, node, snippetId: svcId, resolvedInput: fanInput, deviceId: deviceId);
                db.StepRuns.Add(fanStep);

                // Commit the step_run row before enqueueing its job. The
                // queue insert is raw SQL (auto-committed), so without this
                // flush a worker could claim the job in the window before
                // EF persists the row, find no step_run, and fail the job —
                // leaving the step_run pending forever once SaveChanges
                // eventually commits.
                await db.SaveChangesAsync(ct);

                var fanPayload = JsonSerializer.SerializeToElement(new { step_run_id = fanStep.StepRunId });
                await queue.EnqueueAsync("step", fanPayload,
                    tag: stepTag, priority: 5, ct);
            }
            return;
        }

        // Single step_run: either `once` mode, zero targets, or per_device
        // with a single target (where fan-out would produce exactly one row
        // anyway). Stamp the primary device so handlers can look it up.
        //
        // Device-context rules:
        //   - perDevice + single target:   scope upstream AND bind device ctx.
        //   - once + single target:        NO upstream scope (once handlers
        //                                  may loop over _targets and need
        //                                  the aggregate envelope), but bind
        //                                  device ctx so `{{ device.X }}`
        //                                  still works for the lone target.
        //   - once + multiple targets:     neither (no unambiguous device).
        //   - zero targets:                neither.
        var primaryDeviceId = resolvedTargetIds.Count > 0 ? resolvedTargetIds[0] : (Guid?)null;
        var singleScopeId = perDevice ? primaryDeviceId : null;
        var singleCtxId = shouldLoadDeviceContext ? primaryDeviceId : null;
        var (singleInput, singleUnresolved) = BuildResolvedInput(singleScopeId, singleCtxId);

        if (singleUnresolved.Count > 0)
        {
            PersistFailedStepForUnresolvedTemplates(primaryDeviceId, singleInput, singleUnresolved);
            return;
        }
        if (ForeignIntegrationKeys(singleInput) is { Count: > 0 } singleForeign)
        {
            PersistFailedStepForForeignIntegrations(primaryDeviceId, singleInput, singleForeign);
            return;
        }

        var stepRun = MakeStepRun(run, node, snippetId: svcId, resolvedInput: singleInput, deviceId: primaryDeviceId);
        db.StepRuns.Add(stepRun);

        // See the fan-out branch above for why this flush is required
        // before queue.EnqueueAsync — same race between Dapper's auto-
        // committed INSERT and EF's deferred SaveChanges.
        await db.SaveChangesAsync(ct);

        var jobPayload = JsonSerializer.SerializeToElement(
            new { step_run_id = stepRun.StepRunId });
        await queue.EnqueueAsync("step", jobPayload,
            tag: stepTag, priority: 5, ct);
    }

    // Returns a copy of `outputs` where any step whose aggregated output has
    // the per-device fan-out shape `{ devices: [ {device_id, output}, ... ] }`
    // is replaced by the matching device's own `output`. Steps that don't
    // match the shape (once-mode steps, sentinels, single-step per_device)
    // pass through untouched.
    //
    // Fail closed (workflow.v1 SPEC §6): a producer that IS a fan-out
    // envelope but carries no entry for this device is OMITTED from the
    // scoped map, so `{{ steps.X.output.total }}` resolves as unresolved and
    // the step fails with unresolved_template naming the reference. Passing
    // the aggregate through instead would let this device silently read data
    // belonging to the OTHER devices in the envelope.
    //
    // This is the key piece that keeps `{{ steps.X.output.field }}`
    // templates working when X is a per_device node and the current step is
    // also per_device — without it, the downstream handler would receive a
    // `{ devices: [...] }` envelope that no template in the original
    // workflow references, and every `.field` path resolution would fail
    // silently.
    /// <summary>
    /// The `{{ run.* }}` namespace, as a template sees it.
    /// </summary>
    /// <remarks>
    /// Extracted from the orchestration path so the conformance adapter can ask for the REAL
    /// projection instead of building a second one beside it. A vector that asserts the field
    /// set — "null, never absent" — has to be answered by the thing production uses, or it is
    /// a copy agreeing with itself.
    /// </remarks>
    internal static JsonElement BuildRunContext(
        Guid runId, Guid workflowId, string workflowName, string environment,
        string trigger, string? startedAt, string? ownerEmail, string? url) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = runId,
            workflow_id = workflowId,
            workflow_name = workflowName,
            environment,
            trigger,
            started_at = startedAt,
            owner_email = ownerEmail,
            url,
            // EMPTY STRINGS, not nulls, and this was re-decided in 2026-08 rather than left
            // by default. A conformance vector expected null and this was briefly changed to
            // match it — until `Orchestrate_FailedStepFieldsAreEmptyWhenThePredecessorSucceeded`
            // failed and made the reason explicit: these fields are read by a shared notify
            // sink on the SUCCESS path, where `{{ run.failed_step_error }}` is the whole value
            // of a message body. As null it substitutes as a JSON null, and a handler that
            // requires a body then refuses a step that used to work. As "" it sends an empty
            // body, which is what the author asked for. The vector was corrected instead.
            failed_step_id = string.Empty,
            failed_step_error = string.Empty,
        })).RootElement;

    internal static IReadOnlyDictionary<string, StepResult> ScopeOutputsToDevice(
        IReadOnlyDictionary<string, StepResult> outputs,
        Guid deviceId)
    {
        var scoped = new Dictionary<string, StepResult>(outputs.Count);
        var deviceStr = deviceId.ToString();
        foreach (var kv in outputs)
        {
            // Not a fan-out envelope at all — pass it through verbatim.
            if (!IsAggregate(kv.Value.Output, out var devices))
            {
                scoped[kv.Key] = kv.Value;
                continue;
            }
            // A fan-out envelope: this device gets its own slice, or nothing.
            if (TryExtractDeviceOutput(devices, deviceStr, out var deviceOutput))
                scoped[kv.Key] = new StepResult(deviceOutput);
        }
        return scoped;
    }

    // The fan-out envelope is an object with a `devices` array at least one of
    // whose entries carries a string `device_id`. Requiring the id is what
    // separates a real fan-out from an ordinary output that merely happens to
    // have a `devices` list (an inventory query, say) — the latter must still
    // reach the consumer untouched.
    private static bool IsAggregate(JsonElement output, out JsonElement devices)
    {
        devices = default;
        if (output.ValueKind != JsonValueKind.Object) return false;
        if (!output.TryGetProperty("devices", out devices)
            || devices.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var entry in devices.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("device_id", out var did)
                && did.ValueKind == JsonValueKind.String)
                return true;
        }
        return false;
    }

    // Builds the public `{{ device.X }}` view of a Device row. Keeps the
    // shape stable (snake_case keys, string guid for id) so workflows can
    // rely on it without peeking at EF column names. `properties` is the
    // operator's custom JSON blob — we pass it through verbatim so
    // `{{ device.properties.foo }}` works without schema changes here.
    //
    // We intentionally do NOT expose credential_id, last_sync_at or the
    // expected SSH host key fingerprint: those are either secret or
    // implementation detail that steps have no business templating.
    internal static JsonElement BuildDeviceContext(flow_weaver_backend.Models.Device device)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("id", device.DeviceId.ToString());
            w.WriteString("name", device.DeviceName);
            w.WriteString("ip", device.IpAddress);
            w.WriteString("platform", device.Platform);
            w.WriteString("vendor", device.Vendor);
            w.WriteString("os_version", device.OsVersion);
            w.WriteString("site", device.Site);
            w.WriteString("role", device.Role);
            w.WriteString("external_id", device.ExternalId ?? string.Empty);
            w.WriteString("status", device.Status);
            w.WritePropertyName("properties");
            if (device.Properties.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                w.WriteNullValue();
            else
                device.Properties.WriteTo(w);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // `devices` is the array from an already-validated fan-out envelope
    // (see IsAggregate). Returns false when this device has no entry in it.
    private static bool TryExtractDeviceOutput(JsonElement devices, string deviceId, out JsonElement output)
    {
        output = default;
        foreach (var entry in devices.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("device_id", out var did)
                || did.ValueKind != JsonValueKind.String
                || !string.Equals(did.GetString(), deviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.TryGetProperty("output", out var devOutput)) continue;
            output = devOutput;
            return true;
        }
        return false;
    }

    private static StepRunModel MakeStepRun(
        WorkflowRunModel run,
        WorkflowNode node,
        Guid? snippetId,
        JsonElement resolvedInput,
        Guid? deviceId = null)
    {
        var now = DateTime.UtcNow;
        return new StepRunModel
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = run.WorkflowRunId,
            NodeId = node.Id,
            SnippetId = snippetId,
            DeviceId = deviceId,
            Status = StepStatus.Pending,
            InputPayload = resolvedInput,
            OutputPayload = default,
            Logs = string.Empty,
            Error = string.Empty,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ───────────────────────────────────────────────────────────────────
    //  Subflow handling (Sprint 3.3)
    // ───────────────────────────────────────────────────────────────────

    // A subflow node that refused before it started anything. Recorded as a failed step so
    // the graph can react — a `failure` edge out of it still fires, which is the entire point
    // of failing the step rather than the run.
    private async Task FailSubflowStepAsync(
        AppDbContext db, WorkflowRunModel parentRun, WorkflowNode node,
        string code, string message, CancellationToken ct)
    {
        var step = MakeStepRun(parentRun, node, snippetId: null, resolvedInput: default);
        step.Status = StepStatus.Failed;
        step.ErrorCode = code;
        step.Error = message;
        // Nothing ran, so nothing changed. This is one of the few places the answer is
        // knowable without a handler.
        step.ChangedState = false;
        step.StartedAt = DateTime.UtcNow;
        step.CompletedAt = DateTime.UtcNow;
        db.StepRuns.Add(step);
        await db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "engine.subflow.refused parent_run_id={ParentRunId} node_id={NodeId} code={Code}",
            parentRun.WorkflowRunId, node.Id, code);
    }

    // The workflow ids already running above this run, this run's own included.
    //
    // Walks `ParentRunId` upward. Bounded by MaxSubflowChain so a corrupted chain — a run
    // whose ancestry loops, which no code path should produce — costs a bounded number of
    // queries instead of hanging the orchestrator.
    private const int MaxSubflowChain = 32;

    private static async Task<HashSet<Guid>?> SubflowChainAsync(
        AppDbContext db, WorkflowRunModel run, CancellationToken ct)
    {
        var chain = new HashSet<Guid> { run.WorkflowId };
        var seen = new HashSet<Guid> { run.WorkflowRunId };
        var parentId = run.ParentRunId;

        for (var hop = 0; hop < MaxSubflowChain && parentId is Guid id; hop++)
        {
            if (!seen.Add(id)) break;
            var ancestor = await db.WorkflowRuns
                .AsNoTracking()
                .Where(r => r.WorkflowRunId == id)
                .Select(r => new { r.WorkflowId, r.ParentRunId })
                .FirstOrDefaultAsync(ct);
            if (ancestor is null) break;
            chain.Add(ancestor.WorkflowId);
            parentId = ancestor.ParentRunId;
        }

        return chain;
    }

    private async Task EnqueueSubflowAsync(
        AppDbContext db,
        IQueueRepository queue,
        WorkflowRunModel parentRun,
        WorkflowNode node,
        IReadOnlyDictionary<string, StepResult> completedOutputs,
        CancellationToken ct)
    {
        // Read subflow_workflow_id from config_overrides.
        //
        // A node without it is not "a subflow with no input" — there is no workflow to run —
        // so the STEP fails and the rest of the graph reacts to that. It used to throw, which
        // failed the whole run before any step row existed: the run reported failed with
        // nothing saying which node was at fault, and no `failure` edge could compensate for
        // a step that was never recorded.
        if (node.ConfigOverrides.ValueKind != JsonValueKind.Object
            || !node.ConfigOverrides.TryGetProperty("subflow_workflow_id", out var sfIdEl)
            || sfIdEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(sfIdEl.GetString(), out var subflowWorkflowId)
            || subflowWorkflowId == Guid.Empty)
        {
            await FailSubflowStepAsync(db, parentRun, node, "subflow_missing",
                $"subflow node `{node.Id}` needs config_overrides.subflow_workflow_id to name a "
                + "workflow by id. Nothing ran for this node.", ct);
            return;
        }

        // Both remaining guards run BEFORE the child run row is created and before its input
        // is resolved: a subflow that can never finish should cost one query, not a nested
        // run's worth of side effects on the way to finding out.

        // A child id that names nothing is a BROKEN REFERENCE, not a child that failed. The
        // two read alike and mean opposite things: `subflow_failed` sends an operator to read
        // the child run, and there is no child run to read.
        var childExists = await db.Workflows
            .AsNoTracking()
            .AnyAsync(w => w.WorkflowId == subflowWorkflowId && w.IsActive, ct);
        if (!childExists)
        {
            await FailSubflowStepAsync(db, parentRun, node, "subflow_missing",
                $"subflow node `{node.Id}` names workflow {subflowWorkflowId}, which does not "
                + "exist or is not active. Nothing ran for this node.", ct);
            return;
        }

        // Would starting this child re-enter a workflow already on the chain? The chain is the
        // ancestry of run rows above this one, which is the only record of how we got here.
        //
        // `EnqueueRunAsync` already refuses a cycle it can see in the STORED graphs, and that
        // check stays — it is earlier and friendlier. This one catches what that one cannot:
        // a graph edited after the run started, and any run that did not enter through the
        // HTTP surface at all.
        if (await SubflowChainAsync(db, parentRun, ct) is { } chain && chain.Contains(subflowWorkflowId))
        {
            await FailSubflowStepAsync(db, parentRun, node, "subflow_cycle",
                $"subflow node `{node.Id}` would start workflow {subflowWorkflowId}, which is "
                + "already running above it in this chain. Nothing ran for this node.", ct);
            return;
        }

        // Build a child run. The child inherits the parent's targets and
        // merges the parent's input with the subflow node's config_overrides.
        var merged = MergePayloads(RunInputForSteps(parentRun), node.ConfigOverrides);
        var resolved = _resolver.Resolve(merged, completedOutputs);

        // The child would trust its input's integration ids
        // (AuthoredIntegrationKeys), so an id this subflow node's author did not
        // write fails the step here — the same answer a python step gives, rather
        // than a silent strip that resurfaces as "integration not available"
        // inside the child.
        var foreignIntegrationKeys = AuthoredIntegrationKeys.Unauthored(
            resolved, node.ConfigOverrides,
            IsSubflowChild(parentRun) ? parentRun.InputPayload : null);
        if (foreignIntegrationKeys.Count > 0)
        {
            var stepFail = MakeStepRun(parentRun, node, snippetId: null, resolvedInput: resolved);
            stepFail.Status = StepStatus.Failed;
            stepFail.ErrorCode = "integration_not_authored";
            stepFail.Error =
                $"{string.Join(", ", foreignIntegrationKeys)}: a subflow only passes on integration "
                + "credentials for integrations named as a literal id in this subflow node's own config. "
                + "This value came from the run input or a template.";
            stepFail.ChangedState = false;
            stepFail.StartedAt = DateTime.UtcNow;
            stepFail.CompletedAt = DateTime.UtcNow;
            db.StepRuns.Add(stepFail);
            _logger.LogWarning(
                "engine.subflow.integration_not_authored parent_run_id={ParentRunId} node_id={NodeId} keys={Keys}",
                parentRun.WorkflowRunId, node.Id, string.Join(",", foreignIntegrationKeys));
            return;
        }

        // Same "unresolved templates fail the step" guard as regular
        // step enqueue — a subflow's child run gets populated with the
        // resolved payload, so any residual `{{ X }}` would leak into
        // every nested step's InputPayload. Fail the subflow step loudly
        // instead of silently passing fiction downstream.
        var subflowUnresolved = VariableResolver.FindUnresolvedTemplates(resolved);
        if (subflowUnresolved.Count > 0)
        {
            var errorLines = subflowUnresolved
                .Take(10)
                .Select(u => $"  - {u.Location}: {u.TemplateText}")
                .ToList();
            if (subflowUnresolved.Count > errorLines.Count)
                errorLines.Add($"  … and {subflowUnresolved.Count - errorLines.Count} more");

            var stepFail = MakeStepRun(parentRun, node, snippetId: null, resolvedInput: resolved);
            stepFail.Status = StepStatus.Failed;
            stepFail.ErrorCode = "unresolved_template";
            stepFail.Error =
                "subflow unresolved template references — fix the workflow config so every "
              + $"`{{{{ ... }}}}` points at a real upstream step/path:\n{string.Join("\n", errorLines)}";
            stepFail.StartedAt = DateTime.UtcNow;
            stepFail.CompletedAt = DateTime.UtcNow;
            stepFail.ChangedState = false;
            db.StepRuns.Add(stepFail);
            _logger.LogError(
                "engine.subflow.unresolved_templates parent_run_id={ParentRunId} node_id={NodeId} unresolved_count={Count}",
                parentRun.WorkflowRunId, node.Id, subflowUnresolved.Count);
            return;
        }

        var now = DateTime.UtcNow;
        var childRun = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = subflowWorkflowId,
            Status = RunStatus.Pending,
            InputPayload = resolved,
            TargetDevices = parentRun.TargetDevices,
            TargetPools = parentRun.TargetPools,
            Trigger = "subflow",
            CreatedBy = parentRun.CreatedBy,
            ParentRunId = parentRun.WorkflowRunId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkflowRuns.Add(childRun);

        // Also create a step_run that tracks the subflow in the parent's
        // poll loop. Status transitions to completed/failed when the child
        // run finishes — the child's orchestrator writes the output. The
        // ChildRunId backlink lets the child's terminal block find this
        // exact row in O(1) without scanning by NodeId (which wouldn't
        // disambiguate two subflow nodes invoking the same child).
        var stepRun = MakeStepRun(parentRun, node, snippetId: null, resolvedInput: resolved);
        stepRun.ChildRunId = childRun.WorkflowRunId;
        db.StepRuns.Add(stepRun);

        // Persist child run + parent step_run before the queue insert. The
        // worker that claims the orchestrator job opens a fresh DbContext
        // and looks up workflow_run_id; if the row is still in EF's tracker
        // it sees nothing and fails the orchestrator job, orphaning the
        // step_run in pending.
        await db.SaveChangesAsync(ct);

        // Enqueue the child run for the worker to orchestrate.
        var jobPayload = JsonSerializer.SerializeToElement(
            new { workflow_run_id = childRun.WorkflowRunId });
        await queue.EnqueueAsync("workflow_run", jobPayload,
            tag: "orchestrator", priority: 8, ct);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Target resolution
    // ───────────────────────────────────────────────────────────────────

    // Outcome of target resolution. Rejections are kept apart from the
    // usable ids so callers can tell the operator *why* a target vanished
    // instead of silently shrinking the fan-out — a run whose devices all
    // disallow its environment used to resolve to zero targets and then
    // execute as if no device had ever been selected.
    private sealed record TargetResolution(
        List<Guid> DeviceIds,
        List<Guid> BlockedDevices,
        List<Guid> BlockedPools,
        List<Guid> MissingIds)
    {
        public bool HadRejections =>
            BlockedDevices.Count > 0 || BlockedPools.Count > 0 || MissingIds.Count > 0;
    }

    // Does this device/pool accept runs of workflows in `environment`?
    // An unrecognised environment is treated as draft — it is what the
    // enqueue guard already falls back to, and it keeps a typo from
    // accidentally unlocking production inventory.
    // Shared with RunDeviceAuthorizer so the permission check and the dispatch
    // filter can never disagree about which devices a run reaches.
    internal static bool EnvironmentAllows(
        string environment, bool allowDraft, bool allowQa, bool allowProduction)
        => AllowsEnvironment(environment, allowDraft, allowQa, allowProduction);

    private static bool AllowsEnvironment(
        string environment, bool allowDraft, bool allowQa, bool allowProduction)
        => environment.ToLowerInvariant() switch
        {
            "qa" => allowQa,
            "production" => allowProduction,
            _ => allowDraft,
        };

    private static async Task<TargetResolution> ResolveTargetsAsync(
        AppDbContext db, List<Guid> targetDevices, List<Guid> targetPools,
        string environment, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        var blockedDevices = new HashSet<Guid>();
        var blockedPools = new HashSet<Guid>();
        var missing = new HashSet<Guid>();

        if (targetDevices.Count > 0)
        {
            // Query without the environment filter and partition in memory:
            // the rows the filter would have dropped are exactly what we need
            // to report back, and one round trip answers both questions.
            var rows = await db.Devices
                .AsNoTracking()
                .Where(d => targetDevices.Contains(d.DeviceId)
                            && d.IsActive)
                .Select(d => new { d.DeviceId, d.AllowDraft, d.AllowQa, d.AllowProduction })
                .ToListAsync(ct);

            var found = new HashSet<Guid>();
            foreach (var row in rows)
            {
                found.Add(row.DeviceId);
                if (AllowsEnvironment(environment, row.AllowDraft, row.AllowQa, row.AllowProduction))
                    ids.Add(row.DeviceId);
                else
                    blockedDevices.Add(row.DeviceId);
            }
            foreach (var id in targetDevices)
                if (!found.Contains(id)) missing.Add(id);
        }

        if (targetPools.Count > 0)
        {
            var pools = await db.DevicePools
                .AsNoTracking()
                .Where(p => targetPools.Contains(p.DevicePoolId)
                            && p.IsActive)
                .ToListAsync(ct);

            var usablePools = new List<Models.DevicePool>();
            foreach (var pool in pools)
            {
                if (AllowsEnvironment(environment, pool.AllowDraft, pool.AllowQa, pool.AllowProduction))
                    usablePools.Add(pool);
                else
                    blockedPools.Add(pool.DevicePoolId);
            }

            foreach (var id in targetPools)
                if (!pools.Any(p => p.DevicePoolId == id)) missing.Add(id);

            var memberIds = usablePools
                .SelectMany(p => p.StaticMembers)
                .Distinct()
                .Where(id => !ids.Contains(id))
                .ToList();

            if (memberIds.Count > 0)
            {
                var members = await db.Devices
                    .AsNoTracking()
                    .Where(d => memberIds.Contains(d.DeviceId)
                                && d.IsActive)
                    .Select(d => new { d.DeviceId, d.AllowDraft, d.AllowQa, d.AllowProduction })
                    .ToListAsync(ct);

                foreach (var member in members)
                {
                    // A stale pool member is not the operator's mistake the
                    // way a hand-picked device is, so it never lands in
                    // MissingIds — only the environment block is reported.
                    if (AllowsEnvironment(environment, member.AllowDraft, member.AllowQa, member.AllowProduction))
                        ids.Add(member.DeviceId);
                    else
                        blockedDevices.Add(member.DeviceId);
                }
            }
        }

        return new TargetResolution(
            ids.ToList(),
            blockedDevices.ToList(),
            blockedPools.ToList(),
            missing.ToList());
    }

    // Fail-fast. Selecting targets and getting none of them is always an
    // operator error, and letting the run proceed is the worst outcome:
    // per_device nodes collapse to a single device-less step, `_targets` is
    // never injected and `{{ device.* }}` resolves empty, so the run either
    // fails deep inside a handler ("cannot resolve SSH host") or — worse —
    // succeeds against nothing. Partial rejections stay a warning: a mixed
    // pool exposing only the members that allow this environment is by design.
    private static void EnsureTargetsResolved(TargetResolution resolution, string environment)
    {
        if (resolution.DeviceIds.Count > 0) return;

        var reasons = new List<string>();
        if (resolution.BlockedDevices.Count > 0)
            reasons.Add($"{resolution.BlockedDevices.Count} device(s) do not allow this environment");
        if (resolution.BlockedPools.Count > 0)
            reasons.Add($"{resolution.BlockedPools.Count} device pool(s) do not allow this environment");
        if (resolution.MissingIds.Count > 0)
            reasons.Add($"{resolution.MissingIds.Count} target(s) no longer exist or are inactive");

        var detail = reasons.Count > 0 ? string.Join(", ", reasons) : "no matching inventory";
        var hint = resolution.BlockedDevices.Count > 0 || resolution.BlockedPools.Count > 0
            ? $" — tick '{environment}' in the Environments column on the Devices / Device pools page, or run this workflow from an environment they already allow"
            : string.Empty;

        throw new WorkflowExecutorException(
            $"no runnable targets in environment '{environment}': {detail}{hint}");
    }

    // ───────────────────────────────────────────────────────────────────
    //  Run-context helpers  (`{{ run.<field> }}`)
    // ───────────────────────────────────────────────────────────────────

    // Email of whoever started the run, for `{{ run.owner_email }}`.
    // EnqueueRunAsync stores the starter's user id in CreatedBy, so the
    // Guid branch is the live path; the username fallback covers rows
    // written by older/other code paths. Empty string when nothing
    // matches — never null, so the template still resolves.
    private static async Task<string> ResolveOwnerEmailAsync(
        AppDbContext db, WorkflowRunModel run, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(run.CreatedBy))
            return string.Empty;

        var users = db.Users.AsNoTracking();

        var email = Guid.TryParse(run.CreatedBy, out var ownerId)
            ? await users.Where(u => u.UserId == ownerId).Select(u => u.Email).FirstOrDefaultAsync(ct)
            : await users.Where(u => u.Username == run.CreatedBy).Select(u => u.Email).FirstOrDefaultAsync(ct);

        return email ?? string.Empty;
    }

    // `{{ run.url }}` — the operator-facing run page. Falls back to the
    // relative path when no PublicBaseUrl is configured: a bad link beats
    // an unresolved template that fails the whole notification step.
    private string BuildRunUrl(Guid runId)
    {
        var path = $"/runs/{runId}";
        var b = _options.PublicBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(b) ? path : b + path;
    }

    // The error a notify-failure node reports. Steps of a per_device
    // fan-out fail independently; we surface the first non-empty message
    // and say how many others failed rather than pasting all of them.
    private static string FirstStepError(IReadOnlyList<StepRunModel> nodeSteps)
    {
        var failed = nodeSteps.Where(s => s.Status == StepStatus.Failed).ToList();
        var first = failed
            .Select(s => s.Error)
            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
            ?? "step failed without an error message";
        var message = failed.Count > 1
            ? $"{first} (+{failed.Count - 1} more failed targets)"
            : first;
        return NeutralizeTemplateBraces(message);
    }

    // An unresolved-template failure quotes the offending `{{ ... }}` in its
    // error message. Injected verbatim into run.failed_step_error, that text
    // lands in the notify node's payload and the residual scan flags it —
    // the alert step fails for a template it never wrote, and the operator
    // never hears about the original failure. Collapsing the doubled braces
    // reads the same in an email and no longer matches
    // VariableResolver.ResidualTemplateRegex, which requires `{{ … }}`.
    private static string NeutralizeTemplateBraces(string message)
        => message.Replace("{{", "{").Replace("}}", "}");

    // Copy of `runContext` with failed_step_id / failed_step_error replaced.
    // Any other field is passed through untouched, so adding run fields
    // above doesn't require touching this.
    private static JsonElement WithFailedStep(JsonElement runContext, string failedStepId, string? failedStepError)
    {
        if (runContext.ValueKind != JsonValueKind.Object)
            return runContext;

        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in runContext.EnumerateObject())
            {
                if (prop.NameEquals("failed_step_id") || prop.NameEquals("failed_step_error"))
                    continue;
                w.WritePropertyName(prop.Name);
                prop.Value.WriteTo(w);
            }
            w.WriteString("failed_step_id", failedStepId);
            w.WriteString("failed_step_error", failedStepError ?? string.Empty);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Payload helpers
    // ───────────────────────────────────────────────────────────────────

    // The run input as steps see it. Whoever starts a run (a manual caller, a
    // webhook sender, a trigger's input_defaults) must not be able to name a
    // `${secret:...}` for a handler to decrypt, so markers in it are
    // neutralized (SecretMarkers). A subflow child's input is the parent step's
    // payload, already built from a neutralized input plus the parent's own
    // authored config, so its markers are the author's and stay live.
    internal static JsonElement RunInputForSteps(WorkflowRunModel run)
        => IsSubflowChild(run)
            ? run.InputPayload
            : SecretMarkers.Neutralize(run.InputPayload);

    private static bool IsSubflowChild(WorkflowRunModel run)
        => string.Equals(run.Trigger, "subflow", StringComparison.OrdinalIgnoreCase);

    // Shallow merge: ConfigOverrides keys win over run InputPayload keys.
    private static JsonElement MergePayloads(JsonElement runInput, JsonElement configOverrides)
    {
        if (configOverrides.ValueKind != JsonValueKind.Object)
            return runInput.ValueKind == JsonValueKind.Object ? runInput : JsonDocument.Parse("{}").RootElement;
        if (runInput.ValueKind != JsonValueKind.Object)
            return configOverrides;

        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in runInput.EnumerateObject())
            {
                w.WritePropertyName(prop.Name);
                prop.Value.WriteTo(w);
            }
            foreach (var prop in configOverrides.EnumerateObject())
            {
                w.WritePropertyName(prop.Name);
                prop.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // Collapses a set of per-device step_runs into a single JSON object that
    // preserves each device's outcome. Shape:
    //   {
    //     "devices": [
    //       {
    //         "device_id", "device" (name), "device_name", "hostname",
    //         "status", "error", "output" (raw handler output),
    //         ...spread of output's top-level keys (non-colliding)
    //       }, ...
    //     ],
    //     "devices_by_name": { "<hostname>": <same shape as devices[i]> },
    //     "first_success":   <devices[i] | null>,
    //     "success_count", "failure_count", "skipped_count", "total",
    //     "successful_devices": [ "name", ... ],
    //     "failed_devices":     [ "name", ... ]
    //   }
    // The spread + root counters let templates like
    //   {{ steps.X.output.success_count }}
    //   {{ steps.X.output.devices[0].device }}
    //   {{ steps.X.output.devices_by_name['router-1'].output.results[0].output }}
    //   {{ steps.X.output.first_success.output.stdout }}
    // resolve against the envelope without authors having to reach through
    // `devices[i].output.<key>` every time. Raw `output` is preserved for
    // scripts that need the full payload verbatim.
    //
    // Reserved keys (`device_id`, `device`, `device_name`, `hostname`,
    // `status`, `error`, `output`) are never overwritten by spread — if a
    // handler's output contains a key with one of those names it stays
    // under `.output.<name>` and a warning is logged so the snippet author
    // notices the collision.
    private static JsonElement AggregatePerDeviceOutput(
        List<StepRunModel> nodeSteps,
        IReadOnlyDictionary<Guid, string> deviceNames,
        ILogger? logger = null,
        string? nodeId = null)
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "device_id", "device", "device_name", "hostname",
            "status", "error", "output",
        };

        // Defense in depth: if upstream produced multiple step_runs for
        // the same DeviceId (orchestrator double-claim, retried fan-out,
        // anything we haven't anticipated), keep only the latest by
        // CreatedAt. The previous behavior — suffix the second hostname
        // with `#2` and emit it under devices_by_name — broke downstream
        // snippets that index by bare hostname (which is the natural
        // shape callers expect). This collapses to one entry per device
        // and logs the drop so operators can investigate the upstream
        // duplication.
        var byDeviceId = new Dictionary<Guid, StepRunModel>();
        var deduped = new List<StepRunModel>(nodeSteps.Count);
        foreach (var s in nodeSteps.OrderBy(s => s.CreatedAt))
        {
            if (!s.DeviceId.HasValue)
            {
                deduped.Add(s);
                continue;
            }
            if (byDeviceId.TryGetValue(s.DeviceId.Value, out var prev))
            {
                logger?.LogWarning(
                    "workflow.aggregate.duplicate_step_run node_id={NodeId} device_id={DeviceId} kept_step_run_id={Kept} dropped_step_run_id={Dropped}",
                    nodeId, s.DeviceId.Value, s.StepRunId, prev.StepRunId);
            }
            byDeviceId[s.DeviceId.Value] = s;
        }
        deduped.AddRange(byDeviceId.Values);
        nodeSteps = deduped;

        int success = 0, failure = 0, skipped = 0;
        var successful = new List<string>();
        var failed = new List<string>();
        var entries = new List<(string Name, string Status, byte[] Json)>(nodeSteps.Count);
        int firstSuccessIdx = -1;

        foreach (var s in nodeSteps)
        {
            var deviceName = s.DeviceId.HasValue
                && deviceNames.TryGetValue(s.DeviceId.Value, out var n)
                && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : s.DeviceId?.ToString() ?? string.Empty;

            if (s.Status == StepStatus.Completed)
            {
                success++;
                if (!string.IsNullOrEmpty(deviceName)) successful.Add(deviceName);
                if (firstSuccessIdx < 0) firstSuccessIdx = entries.Count;
            }
            else if (s.Status == StepStatus.Failed)
            {
                failure++;
                if (!string.IsNullOrEmpty(deviceName)) failed.Add(deviceName);
            }
            else if (s.Status == StepStatus.Skipped)
            {
                skipped++;
            }

            using var entryMs = new System.IO.MemoryStream();
            using (var ew = new System.Text.Json.Utf8JsonWriter(entryMs))
            {
                ew.WriteStartObject();
                ew.WriteString("device_id", s.DeviceId?.ToString() ?? string.Empty);
                ew.WriteString("device", deviceName);
                ew.WriteString("device_name", deviceName);
                ew.WriteString("hostname", deviceName);
                ew.WriteString("status", s.Status);
                ew.WriteString("error", s.Error ?? string.Empty);
                ew.WritePropertyName("output");
                if (s.OutputPayload.ValueKind == JsonValueKind.Undefined)
                    ew.WriteNullValue();
                else
                    s.OutputPayload.WriteTo(ew);

                if (s.OutputPayload.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in s.OutputPayload.EnumerateObject())
                    {
                        if (reserved.Contains(prop.Name))
                        {
                            logger?.LogWarning(
                                "workflow.aggregate.reserved_key_collision node_id={NodeId} device={Device} key={Key} — kept under .output, alias not overwritten",
                                nodeId, deviceName, prop.Name);
                            continue;
                        }
                        ew.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(ew);
                    }
                }
                ew.WriteEndObject();
            }
            entries.Add((deviceName, s.Status, entryMs.ToArray()));
        }

        // Build devices_by_name with collision suffixing (#2, #3, …).
        var byNameKeys = new List<string>(entries.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, _, _) in entries)
        {
            var key = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            if (seen.TryGetValue(key, out var prev))
            {
                seen[key] = prev + 1;
                var suffixed = $"{key}#{prev + 1}";
                logger?.LogWarning(
                    "workflow.aggregate.hostname_collision node_id={NodeId} hostname={Hostname} suffix={Suffix}",
                    nodeId, key, suffixed);
                byNameKeys.Add(suffixed);
            }
            else
            {
                seen[key] = 1;
                byNameKeys.Add(key);
            }
        }

        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();

            w.WritePropertyName("devices");
            w.WriteStartArray();
            foreach (var (_, _, json) in entries)
            {
                using var doc = JsonDocument.Parse(json);
                doc.RootElement.WriteTo(w);
            }
            w.WriteEndArray();

            w.WritePropertyName("devices_by_name");
            w.WriteStartObject();
            for (int i = 0; i < entries.Count; i++)
            {
                w.WritePropertyName(byNameKeys[i]);
                using var doc = JsonDocument.Parse(entries[i].Json);
                doc.RootElement.WriteTo(w);
            }
            w.WriteEndObject();

            w.WritePropertyName("first_success");
            if (firstSuccessIdx >= 0)
            {
                using var doc = JsonDocument.Parse(entries[firstSuccessIdx].Json);
                doc.RootElement.WriteTo(w);
            }
            else
            {
                w.WriteNullValue();
            }

            w.WriteNumber("success_count", success);
            w.WriteNumber("failure_count", failure);
            w.WriteNumber("skipped_count", skipped);
            w.WriteNumber("total", nodeSteps.Count);

            w.WritePropertyName("successful_devices");
            w.WriteStartArray();
            foreach (var name in successful) w.WriteStringValue(name);
            w.WriteEndArray();

            w.WritePropertyName("failed_devices");
            w.WriteStartArray();
            foreach (var name in failed) w.WriteStringValue(name);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // Add `_targets: [guid, ...]` to the payload root for handlers.
    private static JsonElement InjectTargets(JsonElement payload, List<Guid> targetIds)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in payload.EnumerateObject())
                {
                    w.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(w);
                }
            }
            w.WritePropertyName("_targets");
            w.WriteStartArray();
            foreach (var id in targetIds) w.WriteStringValue(id.ToString());
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // Stamp `steps: { <node-id>: { output: <output> }, ... }` onto the
    // payload root so python_snippet authors can read upstream outputs
    // structurally — `inp['steps']['ssh-show-version']['output']['devices']`
    // — without manually templating every reference through config_overrides.
    // The map respects ScopeOutputsToDevice's unwrapping: per_device
    // consumers see each upstream's per-device output; once consumers see
    // the aggregate envelope.
    //
    // python_snippet only: stamp the current device's context as `device`
    // so a per_device script can read inp['device']['name'] directly. Same
    // shape as the `{{ device.X }}` context (BuildDeviceContext). An
    // author-supplied `device` (from config_overrides) wins — left as-is.
    private static JsonElement InjectDevice(JsonElement payload, JsonElement deviceContext)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("device", out _))
            return payload;

        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (payload.ValueKind == JsonValueKind.Object)
                foreach (var prop in payload.EnumerateObject())
                {
                    w.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(w);
                }
            w.WritePropertyName("device");
            deviceContext.WriteTo(w);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // If the payload already declares a `steps` key (rare; an author
    // explicitly built one via templates) the existing value wins — we
    // don't clobber author intent.
    private static JsonElement InjectSteps(
        JsonElement payload,
        IReadOnlyDictionary<string, StepResult> steps)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("steps", out _))
        {
            return payload;
        }

        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in payload.EnumerateObject())
                {
                    w.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(w);
                }
            }
            w.WritePropertyName("steps");
            w.WriteStartObject();
            foreach (var kv in steps)
            {
                w.WritePropertyName(kv.Key);
                w.WriteStartObject();
                w.WritePropertyName("output");
                if (kv.Value.Output.ValueKind == JsonValueKind.Undefined)
                    w.WriteNullValue();
                else
                    kv.Value.Output.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    private static bool IsTerminal(string status) =>
        status is StepStatus.Completed or StepStatus.Failed or StepStatus.Skipped or StepStatus.Cancelled;

    // FU-2: walks the workflow nodes, collects every integration_id
    // referenced in `integration_action` config_overrides, and rejects
    // the enqueue if any of those integrations is in NeedsConfig.
    // Bulk-queries the rows in one SELECT to avoid N+1.
    // internal (InternalsVisibleTo) so the FR-026 readiness gate can be
    // asserted directly without standing up the full enqueue scope.
    internal static async Task ValidateIntegrationsReadyAsync(
        AppDbContext db, JsonElement nodes, CancellationToken ct)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return;

        var integrationIds = new HashSet<Guid>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("config_overrides", out var co)
                || co.ValueKind != JsonValueKind.Object) continue;
            if (!co.TryGetProperty("integration_id", out var iid)
                || iid.ValueKind != JsonValueKind.String) continue;
            if (Guid.TryParse(iid.GetString(), out var guid))
                integrationIds.Add(guid);
        }
        if (integrationIds.Count == 0) return;

        var notReady = await db.Integrations
            .AsNoTracking()
            .Where(i => integrationIds.Contains(i.IntegrationId)
                && i.Status == IntegrationStatus.NeedsConfig)
            .Select(i => i.Name)
            .ToListAsync(ct);

        if (notReady.Count > 0)
        {
            throw new WorkflowExecutorException(
                $"Cannot enqueue run: integration(s) still in needs_config status: " +
                $"{string.Join(", ", notReady)}. Add credentials in /integrations before running.");
        }
    }

    // Detaches the JsonElement from its source JsonDocument by round-tripping
    // through text. The live workflow's document is disposed once the
    // EnqueueRunAsync scope ends, which would invalidate NodesSnapshot /
    // EdgesSnapshot before EF writes them.
    private static JsonElement CloneJson(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Undefined)
            return JsonDocument.Parse("null").RootElement;
        return JsonDocument.Parse(source.GetRawText()).RootElement;
    }
}
