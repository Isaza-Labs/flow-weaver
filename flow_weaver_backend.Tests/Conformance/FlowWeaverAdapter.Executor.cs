using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using StepRunModel = flow_weaver_backend.Models.StepRun;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests.Conformance;

// Family `executor` (conformance/execution/SPEC.md).
//
//   { workflow: { nodes, edges }, context: { workflow_id, schema_hash, actor,
//                                            nodes: { <id>: { result, tier } } } }
//     -> { status, final_state, steps, results, rollback_plan, errors, outputs,
//          audit_events }
//
// This product's executor is a DURABLE QUEUE ORCHESTRATOR, not an in-process DAG walk. There
// is no seam that takes a graph and a set of node outcomes and returns a run result, so the
// adapter drives the real orchestrator the way this repository's own orchestration tests do:
// an in-memory database, a fake queue, terminal `step_runs` pre-seeded from the vector's
// mocked outcomes, and the polling loop advanced from them. That IS FlowWeaver's executor;
// answering with anything else would be answering about a product that does not exist.
//
// ── What changed, and why this file was rewritten ───────────────────────────
//
// The first version of this adapter reported that twenty-eight of thirty-three vectors
// failed for three reasons, and refused to translate them away:
//
//   - a step here was `completed`/`failed`/`skipped`, with no `changed` / `no_change`
//     distinction anywhere in the model;
//   - a run had a `Status` and no `final_state`, and the executor computed no rollback plan;
//   - no `audit.v1` event was emitted per node.
//
// All three were true, and all three were the PRODUCT's gap, not the adapter's. The product
// now has the model (`adopt-run-outcome-model`), so this file reports it. The vocabulary is
// still FlowWeaver's own — `StepRun.Status` beside `StepRun.ChangedState`, `WorkflowRun
// .FinalState`, the audit events the executor emitted — read out rather than mapped onto.
//
// ── How the vector's mocked outcomes reach the run ──────────────────────────
//
// `context.nodes` says what each node's action WOULD do IF the executor decides to run it.
// It is a script for the handlers, not a transcript of the run — a node the graph skips is
// still in it, with the outcome it would have had.
//
// So the harness cannot pre-seed those outcomes as finished steps: doing that answers the
// question the vector is asking. A `conditional: false` edge, a `success` edge out of a
// failed node, `stop_on_failure` — every one of them is a decision about WHICH nodes run,
// and pre-seeding makes all of them look like they ran. That was this adapter's own bug,
// and it is why ten vectors reported a product gap that was really a harness gap.
//
// Instead a SCRIPTED WORKER runs beside the orchestrator: it watches for the step rows the
// orchestrator creates and drives each one terminal from the script, the way the real
// WorkerHostedService drives them from a handler. The orchestrator therefore makes every
// run/skip decision itself, which is the thing under test.
//
// One inbound mapping remains: a script entry becomes a step's terminal state. It has to,
// because no handler runs here. What comes back out is measured from that state the same way
// a real run measures a real one.
//
// ── Subflows run for real ───────────────────────────────────────────────────
//
// `context.subflows.<node>` scripts a CHILD RUN. The subflow node itself is NOT scripted: the
// orchestrator creates the child run row, the harness orchestrates that row with the same
// scripted worker, and the child's own completion path — `PropagateChildCompletionAsync` —
// writes the outcome back onto the parent's step exactly as it does in production.
//
// That is the whole point. Everything the contract fixes about a subflow (the tier that
// crosses the boundary, the error codes, the output shape) lives in that path, so scripting
// the node's ANSWER would assert nothing about any of it. Nashira's adapter makes the same
// choice for the same reason.
//
// A child declared `unresolved` gets no workflow row seeded, because "the child does not
// exist" is exactly what the product is supposed to notice.
public sealed partial class FlowWeaverAdapter
{
    private static JsonElement? Executor(JsonElement input)
    {
        if (PropOrNull(input, "workflow") is not { } workflow) return null;

        var nodes = PropOrNull(workflow, "nodes")?.GetRawText() ?? "[]";
        var edges = PropOrNull(workflow, "edges")?.GetRawText() ?? "[]";
        var ctx = PropOrNull(input, "context");

        using var harness = new ExecutorHarness();
        var runId = harness.Seed(nodes, edges, ctx);
        harness.Run(runId, PropOrNull(ctx, "nodes"), PropOrNull(ctx, "subflows"));

        return harness.Report(runId);
    }

    // The orchestration fixture, reduced to what a vector needs. Mirrors
    // WorkflowExecutorOrchestrationTests.Fixture rather than reusing it, because that one is
    // private to its test class — a shared harness is the right eventual home and belongs to
    // the file-map change, not to this one.
    private sealed class ExecutorHarness : IDisposable
    {
        private readonly ServiceProvider _sp;
        private readonly CapturingAuditLogger _audit = new();
        private readonly WorkflowExecutorOptions _options = new()
        {
            PollIntervalMs = 1,
            OrchestrationTimeoutSeconds = 5,
            WorkerEnvironment = "dev-sandbox",
        };

        public ExecutorHarness()
        {
            var services = new ServiceCollection();
            var dbName = System.Guid.NewGuid().ToString();
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<IQueueRepository>(new FakeQueue());
            // The executor emits audit.v1 events through IAuditLogger. Capturing the calls is
            // what the vector asks about — the event the executor produced. Persisting them
            // instead would be asking about this repository's audit plumbing, which has its
            // own tests and is not what this family is for.
            services.AddSingleton<IAuditLogger>(_audit);
            services.AddLogging();
            _sp = services.BuildServiceProvider();
        }

        private AppDbContext Db() => _sp.GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public System.Guid Seed(string nodes, string edges, JsonElement? ctx)
        {
            using var db = Db();

            // One snippet per node, carrying that node's declared tier, and the graph
            // rewritten to point at it where it named a marker rather than an id.
            var (graph, snippets) = BuildSnippets(nodes, PropOrNull(ctx, "nodes"));
            db.Snippets.AddRange(snippets);

            // A child workflow per scripted subflow, so the orchestrator has something real
            // to start. Its nodes are the child's scripted steps.
            SeedChildWorkflows(db, graph, PropOrNull(ctx, "subflows"));

            var wf = new WorkflowModel
            {
                // The vector names the workflow when it cares — an audit.v1 event carries it.
                WorkflowId = GuidOf(ctx, "workflow_id") ?? System.Guid.NewGuid(),
                Name = "conformance",
                Nodes = TestJson.Element(graph),
                Edges = TestJson.Element(edges),
                Environment = "draft",
                SchemaVersion = "v1",
                IsActive = true,
            };
            db.Workflows.Add(wf);

            var run = new WorkflowRunModel
            {
                WorkflowRunId = System.Guid.NewGuid(),
                WorkflowId = wf.WorkflowId,
                Status = RunStatus.Pending,
                InputPayload = TestJson.Element("{}"),
                NodesSnapshot = TestJson.Element(graph),
                EdgesSnapshot = TestJson.Element(edges),
                Trigger = "manual",
                // Both are stamped by EnqueueRunAsync in a real run; this harness starts at
                // the orchestration body, so the vector supplies them directly.
                SchemaHash = StrOf(ctx, "schema_hash"),
                CreatedBy = StrOf(ctx, "actor"),
                // Passed through, never assumed. A vector that says `false` must GET false —
                // letting the harness supply the default would make
                // `stop_on_failure.disabled_keeps_walking` pass because the product happened
                // to agree, rather than because it was asked and obeyed.
                StopOnFailure = BoolOf(ctx, "stop_on_failure"),
                IsActive = true,
            };
            db.WorkflowRuns.Add(run);

            db.SaveChanges();
            return run.WorkflowRunId;
        }

        // Gives every node a snippet of its own so the vector's per-node tier survives, and
        // rewrites the graph where a node names a MARKER rather than an id.
        //
        // The vectors write control-flow graphs with `snippet_id: "__start__"`, which the
        // orchestrator resolves through its legacy by-TYPE lookup — and that lookup returns
        // the first snippet of that type, so three nodes named `__end__` would share one
        // snippet and one tier. Rewriting the marker to the id of the node's own snippet
        // avoids that without touching production code.
        //
        // A snippet_id that is already a GUID is LEFT ALONE and its snippet created with
        // exactly that id, because an audit.v1 event carries it as `op` — rewriting it would
        // change the answer the vector is reading. `subflow` is left alone too: the
        // orchestrator branches on that literal before it ever looks a snippet up.
        private static (string Graph, List<SnippetModel> Snippets) BuildSnippets(
            string nodes, JsonElement? script)
        {
            var snippets = new List<SnippetModel>();
            using var doc = JsonDocument.Parse(nodes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (nodes, snippets);

            var rewritten = new List<Dictionary<string, JsonElement>>();
            foreach (var node in doc.RootElement.EnumerateArray())
            {
                var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (node.ValueKind == JsonValueKind.Object)
                    foreach (var p in node.EnumerateObject()) copy[p.Name] = p.Value.Clone();
                rewritten.Add(copy);

                var nodeId = StrOf(node, "id");
                var declared = StrOf(node, "snippet_id");
                if (nodeId is null || declared is null || declared == "subflow") continue;

                var snippetId = System.Guid.TryParse(declared, out var real)
                    ? real
                    : System.Guid.NewGuid();

                if (snippetId != real)
                    copy["snippet_id"] = TestJson.Element(JsonSerializer.Serialize(snippetId.ToString()));

                snippets.Add(new SnippetModel
                {
                    SnippetId = snippetId,
                    Name = $"conformance-{nodeId}",
                    Type = "ssh",
                    TargetMode = "once",
                    // No ISnippetHandler is registered here, so every type resolves to the
                    // conservative handler floor and the snippet's own tier is what applies —
                    // which is exactly the tier the vector declared.
                    Idempotency = StrOf(ScriptFor(script, nodeId), "tier"),
                    IsActive = true,
                });
            }

            return (JsonSerializer.Serialize(rewritten), snippets);
        }

        private static JsonElement? ScriptFor(JsonElement? script, string nodeId) =>
            script is { ValueKind: JsonValueKind.Object } o
            && o.TryGetProperty(nodeId, out var v) ? v : null;

        public void Run(System.Guid runId, JsonElement? script, JsonElement? subflows)
        {
            var executor = new WorkflowExecutor(
                _sp.GetRequiredService<IServiceScopeFactory>(),
                new DagParser(NullLogger<DagParser>.Instance),
                new VariableResolver(NullLogger<VariableResolver>.Instance),
                new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
                Microsoft.Extensions.Options.Options.Create(_options),
                NullLogger<WorkflowExecutor>.Instance);

            // The script the worker answers from: the parent's nodes, plus every scripted
            // child's steps, flattened by node id. A child run's steps are answered by the
            // same loop that answers the parent's, because to the worker they are just steps.
            var flat = FlattenScript(script, subflows);

            using var stop = new CancellationTokenSource();
            var worker = Task.Run(() => ScriptedWorker(flat, executor, stop.Token));

            try
            {
                executor.ExecuteRunAsync(runId, default).GetAwaiter().GetResult();
            }
            catch (WorkflowExecutorException)
            {
                // A refusal before anything ran — a subflow cycle, a missing child workflow.
                // The run row carries the outcome, which is what the vector reads.
            }
            finally
            {
                stop.Cancel();
                worker.Wait(TimeSpan.FromSeconds(5));
            }
        }

        // The worker, reduced to a lookup. It does what WorkerHostedService does — claim a
        // step, run its action, write the terminal state and the change flag — except that
        // the action is a line in the vector instead of a handler.
        //
        // It polls the step table rather than the queue because the queue here is a stub that
        // swallows what it is given; the rows are the real contract between the two halves.
        private void ScriptedWorker(
            Dictionary<string, JsonElement> script, WorkflowExecutor executor, CancellationToken ct)
        {
            var started = new HashSet<System.Guid>();

            while (!ct.IsCancellationRequested)
            {
                using var db = Db();

                // A child run the orchestrator created and nothing is driving. It gets a real
                // orchestration of its own, on its own task so this loop keeps answering the
                // steps BOTH runs are waiting on — the parent is blocked on the child, and a
                // worker that blocked on the child too would deadlock the pair.
                foreach (var child in db.WorkflowRuns
                    .Where(r => r.ParentRunId != null && r.Status == RunStatus.Pending)
                    .Select(r => r.WorkflowRunId)
                    .ToList())
                {
                    if (!started.Add(child)) continue;
                    var id = child;
                    _ = Task.Run(() =>
                    {
                        try { executor.ExecuteRunAsync(id, default).GetAwaiter().GetResult(); }
                        catch (WorkflowExecutorException) { /* the child's row carries it */ }
                    }, CancellationToken.None);
                }

                // Every run's steps, not just the parent's: a child run's steps are steps.
                //
                // Except a SUBFLOW step. It has no handler and is never enqueued as a step job
                // in production either — it goes terminal when the child run it points at
                // finishes, through `PropagateChildCompletionAsync`. A worker that answered it
                // would be answering for the child, which is the thing under test.
                var waiting = db.StepRuns
                    .Where(s => s.ChildRunId == null
                        && (s.Status == StepStatus.Pending || s.Status == StepStatus.Running))
                    .ToList();

                foreach (var step in waiting)
                {
                    // A node with no script entry did nothing and said so. That is a real
                    // answer, not a missing one: `no_change` is what a handler reports when
                    // it ran and found nothing to do.
                    JsonElement? entry = script.TryGetValue(step.NodeId, out var e) ? e : null;
                    var result = StrOf(entry, "result") ?? "no_change";

                    // `changed` and `no_change` are both "the step finished", which is this
                    // product's Status. WHICH of the two is the separate signal beside it,
                    // and that separation is the whole point of the model.
                    step.Status = result switch
                    {
                        "failed" => StepStatus.Failed,
                        "skipped" => StepStatus.Skipped,
                        _ => StepStatus.Completed,
                    };
                    step.ChangedState = result == "changed";
                    step.Error = result == "failed" ? "mocked failure" : string.Empty;
                    // A script entry may carry the step's OUTPUT, and a downstream
                    // `conditional` edge reads it: `{{ steps.a.output.code }} == 200` is a
                    // decision the executor cannot make if the harness throws the output
                    // away. Dropping it made the executor look like it ignored a condition
                    // it had simply never been given anything to evaluate.
                    if (entry is { } scripted && scripted.TryGetProperty("output", out var output))
                        step.OutputPayload = output.Clone();
                    step.StartedAt ??= DateTime.UtcNow;
                    step.CompletedAt = DateTime.UtcNow;
                    step.UpdatedAt = DateTime.UtcNow;
                }

                if (waiting.Count > 0) db.SaveChanges();
                Thread.Sleep(1);
            }
        }

        public JsonElement Report(System.Guid runId)
        {
            using var db = Db();
            var run = db.WorkflowRuns.AsNoTracking().Single(r => r.WorkflowRunId == runId);
            // Graph order, not clock order. A skipped node has no start time — it never
            // started — so ordering by the clock would sort every skipped node to the end and
            // report a sequence the run never had. The contract fixes topological order, and
            // the graph's own order is the closest thing this report has to it; two steps of
            // the same node (a `per_device` fan-out) stay adjacent and fall back to the clock.
            var order = NodeOrder(run.NodesSnapshot);
            var steps = db.StepRuns.AsNoTracking()
                .Where(s => s.WorkflowRunId == runId)
                .ToList()
                .OrderBy(s => order.TryGetValue(s.NodeId, out var i) ? i : int.MaxValue)
                .ThenBy(s => s.StartedAt ?? DateTime.MaxValue)
                .ToList();

            return JsonSerializer.SerializeToElement(new
            {
                status = run.Status,
                final_state = run.FinalState,
                steps = steps.Select(s => new { node_id = s.NodeId, result = Result(s) }),
                // The same outcomes keyed by node, for the cases where the contract fixes
                // WHAT ran and not the total order. Topological order is contract; the
                // relative order of two independent nodes is not.
                results = ByNode(steps).ToDictionary(s => s.NodeId, Result),
                rollback_plan = Plan(run.RollbackPlanJson),
                changed_count = run.ChangedCount,
                // The CODE where the step recorded one. A code is what a caller branches on
                // and what the contract compares; the message beside it is written for a
                // person and changes freely.
                errors = ByNode(steps).Where(s => !string.IsNullOrEmpty(s.ErrorCode) || !string.IsNullOrEmpty(s.Error))
                    .ToDictionary(s => s.NodeId, s => s.ErrorCode is { Length: > 0 } c ? c : s.Error),
                // Only steps that RECORDED a tier. Every other step's tier is derived at
                // scoring time and never stored, and inventing one here would report a
                // computation as if it were a record.
                tiers = ByNode(steps).Where(s => !string.IsNullOrEmpty(s.Tier))
                    .ToDictionary(s => s.NodeId, s => s.Tier),
                outputs = ByNode(steps).ToDictionary(s => s.NodeId, s => s.OutputPayload),
                audit_events = _audit.Events,
            });
        }

        // `changed` / `no_change` / `failed` / `skipped`, read off the two fields that
        // together say it: Status for whether the step ran, ChangedState for whether it did
        // anything. A row recorded before the signal existed reads `no_change`, which is what
        // null means there — nobody measured, so nothing is claimed.
        private static string Result(StepRunModel s) => s.Status switch
        {
            StepStatus.Failed => "failed",
            StepStatus.Skipped => "skipped",
            _ => s.ChangedState == true ? "changed" : "no_change",
        };

        // node id -> script entry, for the parent's nodes and every scripted child's steps.
        //
        // A child's steps arrive as an ARRAY of `{ node_id, result, tier, output }` where the
        // parent's arrive as an object keyed by node id; both become the same lookup, because
        // the worker answering them does not care which run they belong to.
        private static Dictionary<string, JsonElement> FlattenScript(
            JsonElement? nodes, JsonElement? subflows)
        {
            var flat = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            if (nodes is { ValueKind: JsonValueKind.Object } n)
                foreach (var p in n.EnumerateObject()) flat[p.Name] = p.Value.Clone();

            if (subflows is { ValueKind: JsonValueKind.Object } sf)
                foreach (var child in sf.EnumerateObject())
                    foreach (var step in ChildSteps(child.Value))
                        if (StrOf(step, "node_id") is { } id) flat.TryAdd(id, step.Clone());

            return flat;
        }

        private static IEnumerable<JsonElement> ChildSteps(JsonElement child) =>
            child.ValueKind == JsonValueKind.Object
            && child.TryGetProperty("steps", out var steps)
            && steps.ValueKind == JsonValueKind.Array
                ? steps.EnumerateArray()
                : [];

        // A workflow row per scripted child, so the orchestrator has something real to start.
        //
        // A child declared `unresolved` is deliberately NOT seeded: "this workflow does not
        // exist" is the condition the product is being asked to notice, and seeding it would
        // be the harness answering the question.
        private static void SeedChildWorkflows(AppDbContext db, string graph, JsonElement? subflows)
        {
            if (subflows is not { ValueKind: JsonValueKind.Object } sf) return;

            var childIds = SubflowNodeTargets(graph);
            foreach (var child in sf.EnumerateObject())
            {
                if (!childIds.TryGetValue(child.Name, out var childWorkflowId)) continue;
                if (child.Value.ValueKind == JsonValueKind.Object
                    && child.Value.TryGetProperty("unresolved", out var u)
                    && u.ValueKind == JsonValueKind.True) continue;

                var nodes = new List<object>();
                foreach (var step in ChildSteps(child.Value))
                {
                    if (StrOf(step, "node_id") is not { } id) continue;
                    var snippetId = System.Guid.NewGuid();
                    db.Snippets.Add(new SnippetModel
                    {
                        SnippetId = snippetId,
                        Name = $"conformance-child-{id}",
                        Type = "ssh",
                        TargetMode = "once",
                        Idempotency = StrOf(step, "tier"),
                        IsActive = true,
                    });
                    nodes.Add(new { id, snippet_id = snippetId.ToString() });
                }

                db.Workflows.Add(new WorkflowModel
                {
                    WorkflowId = childWorkflowId,
                    Name = $"conformance-child-{child.Name}",
                    Nodes = TestJson.Element(JsonSerializer.Serialize(nodes)),
                    Edges = TestJson.Element("[]"),
                    Environment = "draft",
                    SchemaVersion = "v1",
                    IsActive = true,
                });
            }
        }

        // parent node id -> the workflow id its `subflow_workflow_id` names.
        private static Dictionary<string, System.Guid> SubflowNodeTargets(string graph)
        {
            var map = new Dictionary<string, System.Guid>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(graph);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
            foreach (var node in doc.RootElement.EnumerateArray())
            {
                if (StrOf(node, "id") is not { } id) continue;
                if (!node.TryGetProperty("config_overrides", out var ov)
                    || ov.ValueKind != JsonValueKind.Object) continue;
                if (StrOf(ov, "subflow_workflow_id") is { } raw
                    && System.Guid.TryParse(raw, out var g)) map[id] = g;
            }
            return map;
        }

        private static Dictionary<string, int> NodeOrder(JsonElement nodes)
        {
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            if (nodes.ValueKind != JsonValueKind.Array) return order;
            var i = 0;
            foreach (var node in nodes.EnumerateArray())
                if (StrOf(node, "id") is { } id) order.TryAdd(id, i++);
            return order;
        }

        // A `per_device` node fans out into several step_runs sharing a node id. The keyed
        // views are keyed BY node, so the first wins rather than the dictionary throwing.
        private static IEnumerable<StepRunModel> ByNode(IEnumerable<StepRunModel> steps) =>
            steps.GroupBy(s => s.NodeId, StringComparer.Ordinal).Select(g => g.First());

        // A plan that will not parse is reported empty rather than crashing the family.
        private static IReadOnlyList<string> Plan(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
            catch (JsonException) { return []; }
        }

        public void Dispose() => _sp.Dispose();
    }

    // Records what the executor emitted, in the order it emitted it.
    private sealed class CapturingAuditLogger : IAuditLogger
    {
        private readonly List<AuditV1Event> _events = [];

        public IReadOnlyList<AuditV1Event> Events => _events;

        public Task LogAsync(
            string entityType, System.Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            // Only the run's own node events. Anything else the orchestrator audits is not
            // part of this family.
            if (entityType == "workflow.node" && after is AuditV1Event e) _events.Add(e);
            return Task.CompletedTask;
        }
    }

    private static bool? BoolOf(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static System.Guid? GuidOf(JsonElement? e, string name) =>
        StrOf(e, name) is { } raw && System.Guid.TryParse(raw, out var g) ? g : null;

    private static string? StrOf(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? StrOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
