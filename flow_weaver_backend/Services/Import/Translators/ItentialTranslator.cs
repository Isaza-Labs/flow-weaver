using System.Text.Json;
using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Import.Translators;

// Deterministic Itential IAP / Operations Manager → FlowWeaver v1
// translator. Itential calls "apps" what FlowWeaver calls integrations.
//
// Real Itential export shape (this is what the canvas downloads):
//   tasks: {
//     <task_id>: {
//       name, canvasName, summary, description,
//       app, displayName, type,
//       variables: { incoming: { … }, outgoing: { … } },
//       nodeLocation: { x, y }
//     },
//     workflow_start: { name: "workflow_start", nodeLocation, … },
//     workflow_end:   { name: "workflow_end",   nodeLocation, … }
//   }
//   transitions: {
//     <from_task_id>: {
//       <to_task_id>: { type: "standard", state: "success|failure|error" }
//     }
//   }
//
// A synthetic shape `transitions[from][state] = ["t1", "t2"]` is also
// accepted for compatibility with hand-rolled fixtures and the
// pre-2026 Itential format. Detection happens per branch value.
public sealed class ItentialTranslator : IDslTranslator
{
    public string FormatName => "itential";

    private static readonly HashSet<string> HttpApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "@itential/adapter-http", "http", "rest", "request",
    };

    private static readonly HashSet<string> SshApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "@itential/adapter-ssh", "ssh", "netmiko", "cli",
    };

    // Itential's built-in sentinels. We map them to FlowWeaver's
    // `__start__` / `__end__` so the imported DAG keeps the existing
    // start/end semantics; the engine already knows those ids.
    private const string ItentialStart = "workflow_start";
    private const string ItentialEnd = "workflow_end";

    // $var.<scope>.<path> — Itential's template syntax. We translate
    // these to FlowWeaver's `{{ ... }}` form so the executor can
    // resolve them at run time. `$var.job.<path>` carries the run
    // input; everything else is a task output reference.
    private static readonly Regex VarRefRegex = new(
        @"\$var\.(?<scope>[A-Za-z0-9_]+)(?<path>(?:\.[A-Za-z0-9_]+|\[[^\]]+\])*)",
        RegexOptions.Compiled);

    public Task<TranslationResult> TranslateAsync(JsonElement document, CancellationToken ct)
    {
        var notes = new List<string>();
        var warnings = new List<string>();

        var workflowName = TryGetString(document, "name") ?? "Itential import";

        if (!document.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(EmptyResult(workflowName, "Itential document had no tasks object."));
        }
        var transitions = document.TryGetProperty("transitions", out var tr) && tr.ValueKind == JsonValueKind.Object
            ? tr : default;

        // Pass 1: collect task ids (canonical FlowWeaver ids). Itential
        // sentinels get rewritten to __start__/__end__ — we keep that
        // mapping so transitions referencing them resolve cleanly.
        // We also classify each task as a WorkFlowEngine special action
        // (evaluation, transformation, ViewData, …) because those need
        // either to be collapsed (evaluation → conditional edges) or
        // emitted as python_snippet placeholders rather than the
        // useless integration_action fallback.
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var taskOrder = new List<string>();
        var wfeKind = new Dictionary<string, WfeKind>(StringComparer.Ordinal);
        foreach (var prop in tasks.EnumerateObject())
        {
            var raw = prop.Name;
            if (string.Equals(raw, ItentialStart, StringComparison.OrdinalIgnoreCase))
            {
                idMap[raw] = "__start__";
                continue;
            }
            if (string.Equals(raw, ItentialEnd, StringComparison.OrdinalIgnoreCase))
            {
                idMap[raw] = "__end__";
                continue;
            }
            var sanitised = SanitiseId(raw);
            idMap[raw] = sanitised;
            taskOrder.Add(sanitised);
            wfeKind[sanitised] = ClassifyWfe(prop.Value);
        }

        // Pre-compute which task ids get collapsed (no node emitted;
        // their incoming + outgoing transitions get woven into
        // conditional edges in Pass 3). We only collapse simple
        // evaluations — one group with one comparison. Anything more
        // complex stays as a python_snippet placeholder that the
        // pipeline will offer to AI-generate.
        var collapsedEvaluations = new Dictionary<string, EvaluationExpr>(StringComparer.Ordinal);
        foreach (var prop in tasks.EnumerateObject())
        {
            var raw = prop.Name;
            if (!idMap.TryGetValue(raw, out var sid)) continue;
            if (!wfeKind.TryGetValue(sid, out var kind) || kind != WfeKind.Evaluation) continue;
            var expr = TryBuildSimpleEvaluation(prop.Value, idMap);
            if (expr is not null) collapsedEvaluations[sid] = expr.Value;
        }

        // Reverse map (sanitised id → raw task name) built once and
        // reused by every helper that needs to look up the raw name of
        // a sanitised id. The forward map can collide (two raws → same
        // sanitised) — we keep the first raw to win; the second is
        // unreachable but the workflow already had a duplicate id
        // problem upstream.
        var reverseIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in idMap)
            reverseIdMap.TryAdd(kv.Value, kv.Key);

        // Chained evaluations: if A → B and both are collapsed, weaving
        // A's edges through B is not safe (we'd lose B's expression
        // entirely). Demote the upstream evaluation back to a
        // python_snippet placeholder so the chain is preserved. The
        // user gets an AI-drafted body for A and the conditional edges
        // still spawn from B. We surface a note so the user understands
        // why the simpler upstream collapse didn't happen.
        if (collapsedEvaluations.Count > 1 && transitions.ValueKind == JsonValueKind.Object)
        {
            var chained = new List<string>();
            foreach (var (sid, _) in collapsedEvaluations)
            {
                if (!reverseIdMap.TryGetValue(sid, out var rawName)) continue;
                if (!transitions.TryGetProperty(rawName, out var outgoing)
                    || outgoing.ValueKind != JsonValueKind.Object) continue;
                foreach (var branch in outgoing.EnumerateObject())
                {
                    if (branch.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!idMap.TryGetValue(branch.Name, out var target)) continue;
                    if (collapsedEvaluations.ContainsKey(target))
                    {
                        chained.Add(sid);
                        break;
                    }
                }
            }
            foreach (var sid in chained)
            {
                collapsedEvaluations.Remove(sid);
                notes.Add(
                    $"Task with id '{sid}' is an evaluation that feeds another evaluation. " +
                    "The upstream evaluation was kept as a python_snippet placeholder so neither branch is lost; the downstream evaluation still collapses to conditional edges.");
            }
        }

        var v1Nodes = new List<object>();
        var v1Edges = new List<object>();

        v1Nodes.Add(new
        {
            id = "__start__",
            snippet_id = "__start__",
            x = 0,
            y = 0,
            type = "sentinel",
            config_overrides = new { },
        });

        // Pass 2: emit a node per real task (skipping the Itential
        // sentinels — we already represent them via __start__/__end__).
        // Collapsed evaluations are also skipped; Pass 3 rewires their
        // edges as conditional edges.
        foreach (var prop in tasks.EnumerateObject())
        {
            var raw = prop.Name;
            if (raw.Equals(ItentialStart, StringComparison.OrdinalIgnoreCase)
                || raw.Equals(ItentialEnd, StringComparison.OrdinalIgnoreCase))
                continue;

            var task = prop.Value;
            if (task.ValueKind != JsonValueKind.Object) continue;

            var sid = idMap[raw];
            if (collapsedEvaluations.ContainsKey(sid))
            {
                notes.Add(
                    $"Task '{raw}' (Itential evaluation) collapsed into conditional edges on its predecessor — no node emitted.");
                continue;
            }

            var app = TryGetString(task, "app")
                   ?? TryGetString(task, "displayName")
                   ?? "";
            // The user-visible action label. Itential stores it under
            // `name`/`canvasName` on real exports and `command` on
            // legacy/synthetic ones. Prefer the most specific.
            var actionLabel = TryGetString(task, "command")
                           ?? TryGetString(task, "canvasName")
                           ?? TryGetString(task, "name")
                           ?? raw;

            var kind = wfeKind.GetValueOrDefault(sid, WfeKind.None);
            var snippetId = MapWfeKindToSnippetId(kind, task, app, actionLabel)
                         ?? ResolveSnippetIdForTask(app, actionLabel);

            if (kind != WfeKind.None && snippetId == "python_snippet")
            {
                // The placeholder snippet_id is the task_name (Itential
                // transformations carry the real "what does this do"
                // label there) or the action label. DependencyResolver
                // will treat this as a missing snippet and the pipeline
                // (Phase 2) auto-generates the body from the rich
                // Itential context (description + variableMap +
                // outgoing). The user reviews and accepts.
                var placeholder = TryGetString(task, "task_name") ?? actionLabel;
                snippetId = SanitiseSnippetPlaceholder(placeholder, kind);
                notes.Add(
                    $"Task '{raw}' (Itential WorkFlowEngine {kind.ToString().ToLowerInvariant()}) mapped to python_snippet placeholder '{snippetId}'. " +
                    "Use the wizard's 'Generate with AI' to draft the body, or map it to an existing snippet.");
            }
            else if (snippetId == "integration_action" && !string.IsNullOrEmpty(app))
            {
                notes.Add(
                    $"Task '{raw}' uses Itential app '{app}' (action '{actionLabel}') — represented as integration_action. " +
                    "Map the integration in the wizard, then open the node in the editor to set `action_id` (the translator can resolve `integration_id` by name but not `action_id`).");
            }

            var configOverrides = MapTaskConfig(task, snippetId, app, actionLabel, kind);
            var (x, y) = ReadNodeLocation(task);

            v1Nodes.Add(new
            {
                id = sid,
                snippet_id = snippetId,
                x,
                y,
                type = "task",
                config_overrides = configOverrides,
            });
        }

        v1Nodes.Add(new
        {
            id = "__end__",
            snippet_id = "__end__",
            x = 1200,
            y = 0,
            type = "sentinel",
            config_overrides = new { },
        });

        // Pass 3: walk transitions, supporting both the real-Itential
        // shape (target → { state }) and the synthetic shape
        // (state → [targets]). The branch value's `ValueKind` is the
        // distinguishing signal.
        //
        // For transitions whose target is a collapsed evaluation, we
        // weave the evaluation's outgoing edges back here as conditional
        // edges: `state=success` keeps the original expression,
        // `state=failure` uses its negation.
        if (transitions.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in transitions.EnumerateObject())
            {
                if (!idMap.TryGetValue(prop.Name, out var fromId)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                // If the source is itself a collapsed evaluation, its
                // outgoing edges are emitted by the predecessor's
                // transition (we get there via the rewriting branch).
                // Skip emitting raw edges from the evaluation.
                if (collapsedEvaluations.ContainsKey(fromId)) continue;

                foreach (var branch in prop.Value.EnumerateObject())
                {
                    if (branch.Value.ValueKind == JsonValueKind.Object)
                    {
                        // Real shape: branch.Name is the target task id,
                        // branch.Value.state is the edge type.
                        if (!idMap.TryGetValue(branch.Name, out var realTarget)) continue;
                        var state = TryGetString(branch.Value, "state") ?? "success";

                        if (collapsedEvaluations.TryGetValue(realTarget, out var expr))
                        {
                            // Weave: emit one conditional edge per
                            // outgoing path of the collapsed evaluation.
                            foreach (var ev in BuildEvaluationEdges(fromId, realTarget, expr, transitions, idMap, reverseIdMap, collapsedEvaluations))
                                v1Edges.Add(ev);
                            continue;
                        }

                        v1Edges.Add(BuildEdge(fromId, realTarget, MapStateToEdge(state)));
                    }
                    else if (branch.Value.ValueKind == JsonValueKind.Array)
                    {
                        // Synthetic shape: branch.Name is the state,
                        // branch.Value is an array of target ids.
                        var edgeType = MapStateToEdge(branch.Name);
                        foreach (var target in branch.Value.EnumerateArray())
                        {
                            string? targetId = target.ValueKind switch
                            {
                                JsonValueKind.String => target.GetString(),
                                JsonValueKind.Object when target.TryGetProperty("task", out var t) => t.GetString(),
                                _ => null,
                            };
                            if (string.IsNullOrEmpty(targetId)) continue;
                            if (!idMap.TryGetValue(targetId!, out var arrTarget))
                            {
                                arrTarget = SanitiseId(targetId!);
                            }
                            if (collapsedEvaluations.TryGetValue(arrTarget, out var expr))
                            {
                                foreach (var ev in BuildEvaluationEdges(fromId, arrTarget, expr, transitions, idMap, reverseIdMap, collapsedEvaluations))
                                    v1Edges.Add(ev);
                                continue;
                            }
                            v1Edges.Add(BuildEdge(fromId, arrTarget, edgeType));
                        }
                    }
                }
            }
        }

        // Pass 4: connect orphans. A task with no incoming edge gets
        // wired from __start__; a task with no outgoing edge gets
        // wired to __end__. This is the only "fabrication" step and it
        // runs AFTER the real transitions are in place so we never
        // shadow them with a star pattern.
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in v1Edges)
        {
            var src = ReadAnonField(e, "source");
            var tgt = ReadAnonField(e, "target");
            if (!string.IsNullOrEmpty(src)) sourceIds.Add(src);
            if (!string.IsNullOrEmpty(tgt)) targetIds.Add(tgt);
        }
        foreach (var id in taskOrder)
        {
            // Collapsed evaluations have no node to orphan-wire to.
            if (collapsedEvaluations.ContainsKey(id)) continue;
            if (!targetIds.Contains(id))
                v1Edges.Add(BuildEdge("__start__", id, "success"));
            if (!sourceIds.Contains(id))
                v1Edges.Add(BuildEdge(id, "__end__", "success"));
        }
        // Edge case: workflow had only sentinels → connect them.
        if (taskOrder.Count == collapsedEvaluations.Count)
            v1Edges.Add(BuildEdge("__start__", "__end__", "success"));

        var v1 = JsonSerializer.SerializeToElement(new
        {
            schema_version = "v1",
            name = workflowName,
            description = TryGetString(document, "description"),
            input_schema = document.TryGetProperty("inputSchema", out var inSchema)
                && inSchema.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<object>(inSchema.GetRawText())
                : new { },
            nodes = v1Nodes,
            edges = v1Edges,
            metadata = new
            {
                source_format = "itential",
                original_task_count = taskOrder.Count,
            },
        });

        return Task.FromResult(new TranslationResult
        {
            V1Workflow = v1,
            Notes = notes.Count == 0
                ? new[] { "Translated from Itential format." }
                : notes.ToArray(),
            Warnings = warnings.ToArray(),
        });
    }

    // ─── WorkFlowEngine classification (Phase 1) ────────────────────

    private enum WfeKind
    {
        None,
        Evaluation,
        Transformation,
        ViewData,
        UpdateJobDescription,
        Stub, // failurePath
        Other,
    }

    private record struct EvaluationExpr(string SuccessCondition, string FailureCondition);

    private static WfeKind ClassifyWfe(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object) return WfeKind.None;
        var app = TryGetString(task, "app") ?? "";
        var display = TryGetString(task, "displayName") ?? "";
        var name = TryGetString(task, "name") ?? TryGetString(task, "canvasName") ?? "";
        // Require `app == "WorkFlowEngine"` (the authoritative signal in
        // every real Itential export we've seen). `displayName == "Tools"`
        // alone is too loose — it can appear on custom adapters too — so
        // we only honour it when `app` also indicates WFE. The
        // displayName=="WorkFlowEngine" check stays as a safety net for
        // exports where `app` got dropped during migration.
        var isWfe = app.Equals("WorkFlowEngine", StringComparison.OrdinalIgnoreCase)
                 || display.Equals("WorkFlowEngine", StringComparison.OrdinalIgnoreCase);
        if (!isWfe) return WfeKind.None;
        return name.ToLowerInvariant() switch
        {
            "evaluation" => WfeKind.Evaluation,
            "transformation" => WfeKind.Transformation,
            "viewdata" => WfeKind.ViewData,
            "updatejobdescription" => WfeKind.UpdateJobDescription,
            "stub" => WfeKind.Stub,
            _ => WfeKind.Other,
        };
    }

    // Maps a WFE classification to its preferred placeholder
    // snippet_id. Anything that genuinely runs custom logic becomes
    // python_snippet so the wizard's "Generate with AI" step takes
    // over. Returns null for `None` (caller falls back to the
    // app-based heuristic).
    private static string? MapWfeKindToSnippetId(
        WfeKind kind, JsonElement task, string app, string actionLabel)
    {
        return kind switch
        {
            WfeKind.None => null,
            WfeKind.Transformation => "python_snippet",
            WfeKind.ViewData => "python_snippet",
            WfeKind.UpdateJobDescription => "python_snippet",
            WfeKind.Stub => "python_snippet",
            WfeKind.Other => "python_snippet",
            // Evaluations are handled by the collapse pass; they
            // shouldn't reach this method because Pass 2 skips them.
            _ => "python_snippet",
        };
    }

    // Convert a WFE placeholder label to a snake_case-ish id that
    // DependencyResolver can carry into the missing list and that the
    // AI generator can use as a hint when drafting the body.
    private static string SanitiseSnippetPlaceholder(string raw, WfeKind kind)
    {
        var sanitised = SanitiseId(raw).ToLowerInvariant();
        if (string.IsNullOrEmpty(sanitised)) sanitised = kind.ToString().ToLowerInvariant();
        // Prefixes are designed to steer DependencyResolver.InferSnippetType:
        // - `python_…` → python_snippet (matches "python" keyword first)
        // - `notify_failure_…` → integration_action (matches "notify"); the
        //   wizard suggests existing email/slack integrations as candidates
        // - The `transform_` prefix is deliberately avoided because it
        //   collides with InferSnippetType's "transform" → JMESPath branch
        //   and WFE transformations need Python (case-insensitive compares,
        //   set ops, …) that JMESPath can't express.
        var prefix = kind switch
        {
            WfeKind.Transformation => "python_transform_",
            WfeKind.ViewData => "python_view_",
            WfeKind.UpdateJobDescription => "python_audit_log_",
            WfeKind.Stub => "notify_failure_",
            _ => "python_wfe_",
        };
        return sanitised.StartsWith(prefix) ? sanitised : prefix + sanitised;
    }

    // Try to build a FlowWeaver-evaluable expression from an Itential
    // `evaluation_groups` block. Only the simple case is collapsed —
    // one group with one comparison — because negating compound
    // expressions cleanly is awkward (FlowWeaver's condition grammar
    // has no `!`). Anything more complex returns null and the
    // evaluation stays as a python_snippet placeholder.
    private static EvaluationExpr? TryBuildSimpleEvaluation(
        JsonElement task,
        IReadOnlyDictionary<string, string> idMap)
    {
        if (task.ValueKind != JsonValueKind.Object) return null;
        if (!task.TryGetProperty("variables", out var vars)
            || vars.ValueKind != JsonValueKind.Object) return null;
        if (!vars.TryGetProperty("incoming", out var inc)
            || inc.ValueKind != JsonValueKind.Object) return null;
        if (!inc.TryGetProperty("evaluation_groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array
            || groups.GetArrayLength() != 1) return null;

        var group = groups[0];
        if (!group.TryGetProperty("evaluations", out var evals)
            || evals.ValueKind != JsonValueKind.Array
            || evals.GetArrayLength() != 1) return null;

        var ev = evals[0];
        if (!ev.TryGetProperty("operand_1", out var op1)
            || !ev.TryGetProperty("operand_2", out var op2)
            || !ev.TryGetProperty("operator", out var opEl)
            || opEl.ValueKind != JsonValueKind.String) return null;

        var op = opEl.GetString() ?? "";
        var successOp = NormaliseOperator(op);
        var failureOp = InvertOperator(successOp);
        if (successOp is null || failureOp is null) return null;

        var left = RenderOperand(op1, idMap);
        var right = RenderOperand(op2, idMap);
        if (left is null || right is null) return null;

        return new EvaluationExpr(
            SuccessCondition: $"{left} {successOp} {right}",
            FailureCondition: $"{left} {failureOp} {right}");
    }

    private static string? NormaliseOperator(string raw) => raw switch
    {
        ">" or "<" or ">=" or "<=" or "==" or "!=" => raw,
        "=" => "==",
        _ => null,
    };

    private static string? InvertOperator(string? op) => op switch
    {
        ">" => "<=",
        "<" => ">=",
        ">=" => "<",
        "<=" => ">",
        "==" => "!=",
        "!=" => "==",
        _ => null,
    };

    private static string? RenderOperand(JsonElement operand, IReadOnlyDictionary<string, string> idMap)
    {
        if (operand.ValueKind != JsonValueKind.Object) return null;
        var variable = TryGetString(operand, "variable");
        var taskRef = TryGetString(operand, "task");
        if (string.IsNullOrEmpty(taskRef) || string.IsNullOrEmpty(variable)) return null;
        if (taskRef.Equals("static", StringComparison.OrdinalIgnoreCase))
        {
            // Number-looking literal stays bare so the ConditionEvaluator
            // does a numeric compare. Booleans get the same treatment.
            if (long.TryParse(variable, out _)
                || double.TryParse(variable, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _)
                || variable!.Equals("true", StringComparison.OrdinalIgnoreCase)
                || variable.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return variable!.ToLowerInvariant() switch { "true" => "true", "false" => "false", _ => variable };
            }
            // Quoted string. FlowWeaver's ConditionEvaluator grammar has
            // no documented escape semantics, so reject anything that
            // could confuse the parser: template braces, backslashes,
            // newlines, or embedded quotes — fall back to leaving the
            // evaluation un-collapsed (python_snippet placeholder, the
            // AI handles the comparison).
            if (variable.IndexOfAny(new[] { '\\', '\n', '\r', '"' }) >= 0) return null;
            if (variable.Contains("{{") || variable.Contains("}}")) return null;
            if (variable.Contains('\'')) return null;
            return $"'{variable}'";
        }
        var resolved = idMap.TryGetValue(taskRef!, out var sid) ? sid : SanitiseId(taskRef!);
        return $"{{{{ steps.{resolved}.output.{variable} }}}}";
    }

    // For every (predecessor → evaluation) inbound transition, weave
    // the evaluation's outbound transitions into conditional edges
    // anchored at the predecessor. `success` keeps the original
    // expression; `failure` uses its negation.
    private static IEnumerable<object> BuildEvaluationEdges(
        string predecessorId,
        string evaluationId,
        EvaluationExpr expr,
        JsonElement transitions,
        IReadOnlyDictionary<string, string> idMap,
        IReadOnlyDictionary<string, string> reverseIdMap,
        IReadOnlyDictionary<string, EvaluationExpr> collapsedEvaluations)
    {
        if (transitions.ValueKind != JsonValueKind.Object) yield break;

        // O(1) reverse lookup of the raw task name from its sanitised id.
        if (!reverseIdMap.TryGetValue(evaluationId, out var rawEvalName)) yield break;

        if (!transitions.TryGetProperty(rawEvalName, out var evalTransitions)
            || evalTransitions.ValueKind != JsonValueKind.Object) yield break;

        foreach (var branch in evalTransitions.EnumerateObject())
        {
            // Itential real shape: branch.Name = target id, branch.Value.state = state.
            if (branch.Value.ValueKind != JsonValueKind.Object) continue;
            if (!idMap.TryGetValue(branch.Name, out var target)) continue;
            // Chained evaluations: if the target is itself collapsed,
            // skip — Pass 3 will pick it up when the predecessor's
            // edge weaves through it on its own.
            if (collapsedEvaluations.ContainsKey(target)) continue;

            var state = TryGetString(branch.Value, "state") ?? "success";
            var condition = state.Equals("success", StringComparison.OrdinalIgnoreCase)
                ? expr.SuccessCondition
                : state.Equals("failure", StringComparison.OrdinalIgnoreCase)
                    ? expr.FailureCondition
                    // Itential's "error" on an evaluation is the engine
                    // misfiring (not the boolean false path). Keep it
                    // as a real failure edge.
                    : null;

            if (condition is null)
            {
                yield return BuildEdge(predecessorId, target, MapStateToEdge(state));
            }
            else
            {
                yield return new
                {
                    source = predecessorId,
                    target,
                    type = "conditional",
                    condition,
                };
            }
        }
    }

    // ─── helpers ────────────────────────────────────────────────────

    // Itential's transition `state` → FlowWeaver edge type. `failure`
    // and `error` both indicate the "step raised" path; we preserve
    // them as `failure` so the engine's outcome routing kicks in.
    private static string MapStateToEdge(string state) =>
        state.ToLowerInvariant() switch
        {
            "success" => "success",
            "failure" or "error" => "failure",
            _ => "always",
        };

    private static object BuildEdge(string source, string target, string type) =>
        new { source, target, type };

    private static string ResolveSnippetIdForTask(string app, string command)
    {
        if (HttpApps.Contains(app)) return "rest_call";
        if (SshApps.Contains(app)) return "ssh";
        if (app.Contains("python", StringComparison.OrdinalIgnoreCase)) return "python_snippet";
        // Unknown app + command → assume integration_action so the user
        // resolves the integration mapping in the wizard.
        return string.IsNullOrEmpty(app) ? "python_snippet" : "integration_action";
    }

    private static object? MapTaskConfig(JsonElement task, string snippetId, string app, string actionLabel, WfeKind kind)
    {
        // Real Itential: variables.incoming. Synthetic / legacy:
        // parameters / params. Pull whichever exists; an empty object
        // is fine since the wizard / editor can fill it later.
        var incoming = ReadIncoming(task);
        var translated = TranslateVarReferences(incoming);

        // Itential's `stub` (failurePath) tasks carry useless control-
        // flow flags (`type: "error"`, `delay: ""`, `response: "true"`)
        // that make no sense in any FlowWeaver target. Whether the user
        // picks "generate with AI" or "map to existing notify-failure
        // integration_action", they'd just have to delete this junk.
        // Emit an empty config so the target's defaults apply cleanly.
        if (kind == WfeKind.Stub) return new Dictionary<string, object?>();

        return snippetId switch
        {
            "rest_call" => BuildRestCallConfig(translated, actionLabel),
            "ssh" => BuildSshConfig(translated, actionLabel),
            "integration_action" => new Dictionary<string, object?>
            {
                // The DependencyResolver matches `integration_id` against
                // existing Integration rows by literal string; if no GUID
                // match it falls back to a fuzzy name lookup. Pass
                // the app name so name-matching can find Netbox/Infoblox/
                // WorkFlowEngine integrations without the user mapping
                // each one manually.
                ["integration_id"] = app,
                ["integration_name"] = app,
                ["action_name"] = actionLabel,
                // `params` is the canonical key the executor reads for
                // integration_action; build by name to dodge the C#
                // `params` keyword.
                ["params"] = translated,
            },
            _ => translated is Dictionary<string, object?> dict
                ? (object)dict
                : new { },
        };
    }

    private static object? BuildRestCallConfig(object? incoming, string actionLabel)
    {
        if (incoming is Dictionary<string, object?> dict)
        {
            return new Dictionary<string, object?>
            {
                ["method"] = ReadString(dict, "method") ?? "GET",
                ["url"] = ReadString(dict, "url") ?? ReadString(dict, "uri") ?? "",
                ["headers"] = dict.TryGetValue("headers", out var h) ? h : new Dictionary<string, object?>(),
                ["body"] = dict.TryGetValue("body", out var b) ? b : null,
            };
        }
        return new { method = "GET", url = "", headers = new { }, body = (object?)null };
    }

    private static object? BuildSshConfig(object? incoming, string actionLabel)
    {
        if (incoming is Dictionary<string, object?> dict)
        {
            return new Dictionary<string, object?>
            {
                ["commands"] = dict.TryGetValue("commands", out var c) ? c
                              : !string.IsNullOrWhiteSpace(actionLabel) ? new List<object?> { actionLabel }
                              : new List<object?>(),
            };
        }
        return new { commands = string.IsNullOrWhiteSpace(actionLabel) ? Array.Empty<string>() : new[] { actionLabel } };
    }

    // Pull task.variables.incoming (real Itential) or task.parameters /
    // task.params (synthetic) into a plain Dictionary the rest of the
    // pipeline can mutate. Strings get $var.X.Y → {{ … }} translation
    // applied later by TranslateVarReferences.
    private static object? ReadIncoming(JsonElement task)
    {
        if (task.TryGetProperty("variables", out var vars)
            && vars.ValueKind == JsonValueKind.Object
            && vars.TryGetProperty("incoming", out var inc)
            && inc.ValueKind == JsonValueKind.Object)
        {
            return DeserialiseAnon(inc);
        }
        if (task.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
            return DeserialiseAnon(p);
        if (task.TryGetProperty("params", out var p2) && p2.ValueKind == JsonValueKind.Object)
            return DeserialiseAnon(p2);
        return new Dictionary<string, object?>();
    }

    private static object? DeserialiseAnon(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(
                p => p.Name,
                p => DeserialiseAnon(p.Value)),
            JsonValueKind.Array => el.EnumerateArray()
                .Select(DeserialiseAnon).ToList(),
            JsonValueKind.String => (object?)el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    // Recursively walk a value, rewriting every string occurrence of
    // `$var.<scope>.<path>` into the FlowWeaver template equivalent.
    //   $var.job.<path>       → {{ input.<path> }}
    //   $var.<task_id>.<path> → {{ steps.<task_id>.output.<path> }}
    // Bare `$var.X` (no path) maps to the root of that scope.
    private static object? TranslateVarReferences(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dict => dict.ToDictionary(
                kv => kv.Key,
                kv => TranslateVarReferences(kv.Value)),
            List<object?> list => list.Select(TranslateVarReferences).ToList(),
            string s => TranslateVarString(s),
            _ => value,
        };
    }

    private static string TranslateVarString(string raw) =>
        VarRefRegex.Replace(raw, m =>
        {
            var scope = m.Groups["scope"].Value;
            var path = m.Groups["path"].Value; // includes the leading dot when present
            return scope.Equals("job", StringComparison.OrdinalIgnoreCase)
                ? $"{{{{ input{path} }}}}"
                : $"{{{{ steps.{scope}.output{path} }}}}";
        });

    private static (int X, int Y) ReadNodeLocation(JsonElement task)
    {
        if (!task.TryGetProperty("nodeLocation", out var loc) || loc.ValueKind != JsonValueKind.Object)
            return (0, 0);
        var x = loc.TryGetProperty("x", out var xEl) && xEl.ValueKind == JsonValueKind.Number ? xEl.GetInt32() : 0;
        var y = loc.TryGetProperty("y", out var yEl) && yEl.ValueKind == JsonValueKind.Number ? yEl.GetInt32() : 0;
        return (x, y);
    }

    private static TranslationResult EmptyResult(string name, string warning) => new()
    {
        V1Workflow = JsonSerializer.SerializeToElement(new
        {
            schema_version = "v1",
            name,
            description = (string?)null,
            input_schema = new { },
            nodes = new object[]
            {
                new { id = "__start__", snippet_id = "__start__", x = 0, y = 0, type = "sentinel", config_overrides = new { } },
                new { id = "__end__", snippet_id = "__end__", x = 200, y = 0, type = "sentinel", config_overrides = new { } },
            },
            edges = new object[]
            {
                new { source = "__start__", target = "__end__", type = "success" },
            },
            metadata = new { source_format = "itential" },
        }),
        Notes = Array.Empty<string>(),
        Warnings = new[] { warning },
    };

    private static string? TryGetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string? ReadString(Dictionary<string, object?> dict, string key) =>
        dict.TryGetValue(key, out var v) && v is string s ? s : null;

    // Anonymous-object property accessor used by the orphan-edge pass.
    // The edges list contains `new { source, target, type }` instances
    // so we read via reflection rather than re-serialising.
    private static string ReadAnonField(object obj, string field)
    {
        var prop = obj.GetType().GetProperty(field);
        return prop?.GetValue(obj)?.ToString() ?? "";
    }

    private static string SanitiseId(string raw)
    {
        var s = new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(s) ? Guid.NewGuid().ToString("N")[..8] : s;
    }
}
