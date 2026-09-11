using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Import.Detectors;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Import.Translators;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Identity;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;

namespace flow_weaver_backend.Services.Import;

// Runs the full pipeline against an ImportDraft:
//   1. Parse the upload (auto-detect yaml vs json).
//   2. Score every registered detector; pick the winner.
//   3. Hand off to the matching translator (specific > generic > agent).
//   4. Resolve missing dependencies.
//   5. Detect conflicts (name + structural duplicate).
//   6. Run the rollback risk analyzer.
//   7. Stash the report on the draft; flip status to Ready.
//
// Lives entirely inside one scope. The controller calls `RunAsync` from
// `Task.Run` so the request returns immediately with the token; the
// draft's Channel pushes status events to anyone subscribed via SSE.
public sealed class WorkflowImportPipeline
{
    private readonly IEnumerable<IDslDetector> _detectors;
    private readonly IEnumerable<IDslTranslator> _translators;
    private readonly DependencyResolver _dependencies;
    private readonly ConflictDetector _conflicts;
    private readonly WorkflowRollbackAnalyzer _rollbackAnalyzer;
    private readonly GenerateSnippetForImportHandler _snippetGenerator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentUser _caller;
    private readonly ILogger<WorkflowImportPipeline> _logger;

    public WorkflowImportPipeline(
        IEnumerable<IDslDetector> detectors,
        IEnumerable<IDslTranslator> translators,
        DependencyResolver dependencies,
        ConflictDetector conflicts,
        WorkflowRollbackAnalyzer rollbackAnalyzer,
        GenerateSnippetForImportHandler snippetGenerator,
        IServiceScopeFactory scopeFactory,
        ICurrentUser caller,
        ILogger<WorkflowImportPipeline> logger)
    {
        _detectors = detectors;
        _translators = translators;
        _dependencies = dependencies;
        _conflicts = conflicts;
        _rollbackAnalyzer = rollbackAnalyzer;
        _snippetGenerator = snippetGenerator;
        _scopeFactory = scopeFactory;
        _caller = caller;
        _logger = logger;
    }

    public async Task RunAsync(ImportDraft draft, CancellationToken ct)
    {
        try
        {
            draft.MarkAnalyzing("Parsing upload...");
            var document = ParseUpload(draft.RawBody);

            // A bundle is our own format and carries its own dependencies, so it
            // is answered before detection rather than by it. The ordering IS the
            // fix: a bundle has top-level nodes/edges and `snippet_id` on its
            // first node, so FlowWeaverV1Detector claims it at 0.85, the
            // translator keeps only nodes+edges, and `dependencies` is gone before
            // anything looks for it — after which a `ping` and an `email_send` are
            // indistinguishable from a `python_snippet`, the node being all that
            // is left to go on.
            if (Services.Workflow.WorkflowBundleReader.LooksLikeBundle(document))
            {
                await AnalyzeBundleAsync(draft, ct);
                return;
            }

            draft.MarkProgress("Detecting format...");
            var (detectedFormat, confidence) = ChooseFormat(document, draft.FormatHint);
            _logger.LogInformation(
                "import.detected token={Token} hint={Hint} format={Format} confidence={Confidence}",
                draft.Token, draft.FormatHint ?? "(auto)", detectedFormat, confidence);

            draft.MarkProgress($"Translating from {detectedFormat}...");
            var translator = ResolveTranslator(detectedFormat);
            var translation = await translator.TranslateAsync(document, ct);

            // Surface routing decisions so a user who picked
            // "Generic DAG" understands why we still ran a
            // deterministic translator (or vice versa). Without this
            // note the format-detected card looks like the system
            // ignored their choice.
            var routingNote = BuildFormatRoutingNote(draft.FormatHint, detectedFormat, confidence);
            if (routingNote is not null)
            {
                translation = new TranslationResult
                {
                    V1Workflow = translation.V1Workflow,
                    Notes = new[] { routingNote }
                        .Concat(translation.Notes ?? Array.Empty<string>())
                        .ToArray(),
                    Warnings = translation.Warnings,
                };
            }

            draft.MarkProgress("Resolving dependencies...");
            var dependencies = await _dependencies.ResolveAsync(translation.V1Workflow, ct);

            // Phase 2: pre-draft a body for every python_snippet missing
            // entry so the wizard's review opens with the snippet already
            // generated. The user just reviews + accepts instead of
            // clicking "Generate with AI" for each one. Fails open: if no
            // LLM provider is configured or generation errors out, the
            // entry stays as a regular missing snippet and the wizard's
            // existing manual button still works.
            draft.MarkProgress("Pre-drafting snippet bodies (AI)...");
            dependencies = await PregenerateSnippetBodiesAsync(translation.V1Workflow, dependencies, ct);

            draft.MarkProgress("Detecting conflicts...");
            var conflicts = await _conflicts.DetectAsync(translation.V1Workflow, "draft", ct);

            draft.MarkProgress("Analyzing rollback risk...");
            var rollbackRisk = await AnalyzeRollbackRiskAsync(translation.V1Workflow, ct);

            var report = new AnalysisReport
            {
                FormatDetected = detectedFormat,
                Confidence = confidence,
                ProposedWorkflow = translation.V1Workflow,
                TranslationNotes = translation.Notes,
                MissingDependencies = dependencies,
                Conflicts = conflicts,
                RollbackRisk = rollbackRisk,
                Warnings = translation.Warnings,
            };
            draft.MarkReady(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "import.pipeline.failed token={Token}",
                draft.Token);
            draft.MarkFailed($"Pipeline failed: {ex.Message}");
        }
    }

    // Walks the missing-snippet list and tries to LLM-draft a body for
    // every entry whose inferred type is python_snippet (the bucket
    // where Itential `transformation`/`evaluation`/`updateJobDescription`/
    // `view_data` land after Phase 1 mapping). Adds the drafted shape
    // to the new `pregenerated_snippet` field; the wizard renders it
    // as an already-filled action with a Preview / Regenerate /
    // Discard set of buttons.
    //
    // The handler itself is resilient to "no AI provider configured" —
    // it returns a fallback stub. We treat both real drafts and
    // fallback stubs as pre-generation results. If something blows
    // up (network, parse error), the entry stays untouched and the
    // wizard's existing manual button fills the gap.
    private async Task<DependenciesReport> PregenerateSnippetBodiesAsync(
        JsonElement proposedWorkflow,
        DependenciesReport dependencies,
        CancellationToken ct)
    {
        if (dependencies.Snippets.Count == 0) return dependencies;

        // LLM calls are independent; run them in parallel to keep the
        // analyze step responsive when several placeholders need
        // drafting at once (Itential imports commonly have 4–6 WFE
        // tasks). Index-based reassembly preserves the original
        // ordering so the wizard renders cards in the same sequence
        // the resolver returned.
        var slots = dependencies.Snippets.Select(m => (Original: m, Drafted: (JsonElement?)null)).ToArray();
        var pending = new List<(int Index, Task<JsonElement?> Task)>();
        for (var i = 0; i < slots.Length; i++)
        {
            var missing = slots[i].Original;
            if (!IsAutoDraftCandidate(missing.InferredType)) continue;
            // Don't pre-draft a bespoke python body when the task clearly maps
            // to an existing Integration — leave it for the wizard to map to an
            // integration_action instead.
            if (HasStrongIntegrationCandidate(missing)) continue;
            pending.Add((i, DraftOneAsync(missing, proposedWorkflow, ct)));
        }
        if (pending.Count > 0)
        {
            await Task.WhenAll(pending.Select(p => p.Task));
            foreach (var (index, task) in pending)
            {
                slots[index] = (slots[index].Original, task.Result);
            }
        }

        var enriched = slots
            .Select(s => s.Drafted is { } d
                ? new MissingSnippet
                {
                    IdInImport = s.Original.IdInImport,
                    InferredType = s.Original.InferredType,
                    ActionsAvailable = s.Original.ActionsAvailable,
                    CandidatesForMapping = s.Original.CandidatesForMapping,
                    PregeneratedSnippet = d,
                }
                : s.Original)
            .ToList();

        return new DependenciesReport
        {
            Snippets = enriched,
            Integrations = dependencies.Integrations,
            VendorCommands = dependencies.VendorCommands,
        };
    }

    private async Task<JsonElement?> DraftOneAsync(
        MissingSnippet missing,
        JsonElement proposedWorkflow,
        CancellationToken ct)
    {
        try
        {
            // Each parallel draft gets its own DI scope so the underlying
            // AppDbContext is not shared with sibling tasks. Without this,
            // Task.WhenAll trips EF's concurrency guard:
            //   "A second operation was started on this context instance
            //    before a previous operation completed."
            // The pipeline runs from Task.Run (no HttpContext), so the
            // new scope's MutableCurrentUser has to be bound explicitly
            // — otherwise the handler's identity reads explode.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var scopedUser = scope.ServiceProvider.GetRequiredService<MutableCurrentUser>();
            scopedUser.Bind(_caller.UserId, _caller.Username, _caller.Roles);
            var handler = scope.ServiceProvider.GetRequiredService<GenerateSnippetForImportHandler>();
            var args = JsonSerializer.SerializeToElement(new
            {
                id_in_import = missing.IdInImport,
                inferred_type = missing.InferredType,
                hint = (string?)null,
                proposed_workflow = proposedWorkflow,
            });
            var result = await handler.ExecuteAsync(args, ct);
            if (result.TryGetProperty("generated_snippet", out var g)
                && g.ValueKind == JsonValueKind.Object)
            {
                return g.Clone();
            }
            // `fallback_stub` means the LLM call failed or no provider
            // is configured. Keep the entry as a regular missing entry
            // so the user's manual "Generate with AI" still has a
            // first-class place to surface the underlying error.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "import.pregenerate.failed id={Id}",
                missing.IdInImport);
            return null;
        }
    }

    private static bool IsAutoDraftCandidate(string inferredType) =>
        inferredType is "python_snippet" or "transform";

    // 0.85 is the keyword-bridge / fuzzy auto-apply score the resolver assigns
    // a confident integration match — at/above it we treat the task as
    // "should be an integration_action" and skip python pre-generation.
    private const double IntegrationMappingThreshold = 0.85;

    private static bool HasStrongIntegrationCandidate(MissingSnippet missing) =>
        missing.CandidatesForMapping is { } candidates
        && candidates.Any(c =>
            string.Equals(c.Kind, "integration", StringComparison.OrdinalIgnoreCase)
            && c.SimilarityScore >= IntegrationMappingThreshold);

    // YAML auto-detected by content sniffing: if the byte stream starts
    // with a brace or bracket (after trimming), we treat it as JSON;
    // otherwise we try YAML.
    //
    // We use YamlStream (the representation model) instead of the typed
    // deserializer because Deserialize<object?>() collapses every scalar
    // to a string, which then fails the v1 JSON Schema (which requires
    // node.x / node.y to be numbers). YamlStream preserves scalar tags
    // and quoting style, so a YAML 1.2 plain scalar like `50` correctly
    // ends up as a JSON number.
    // The bundle path. Everything a bundle needs is already in the file, so
    // nothing here detects, translates, infers or generates: it reads what the
    // bundle declares and asks the SAME resolution the importer runs which of
    // those dependencies this instance already has.
    //
    // Deliberately built on IWorkflowBundleService.ResolveAsync rather than on a
    // local reimplementation. The preview and the commit have to agree about what
    // will happen, and the only way to guarantee that is for both to ask the same
    // question of the same code.
    private async Task AnalyzeBundleAsync(ImportDraft draft, CancellationToken ct)
    {
        draft.MarkProgress("Reading bundle...");
        var raw = System.Text.Encoding.UTF8.GetString(draft.RawBody);
        var bundle = Services.Workflow.WorkflowBundleReader.Parse(raw);

        _logger.LogInformation(
            "import.bundle token={Token} kind={Kind} schema={Schema} exported_by={Product}",
            draft.Token, bundle.Kind, bundle.SchemaVersion, bundle.ExportedBy?.Product ?? "(unknown)");

        draft.MarkProgress("Resolving against this instance...");
        using var scope = _scopeFactory.CreateScope();
        var bundles = scope.ServiceProvider
            .GetRequiredService<Services.Workflow.IWorkflowBundleService>();
        var resolution = await bundles.ResolveAsync(bundle, ct);

        // A type with no registered handler still imports — the node is offered a
        // resolution rather than the whole bundle being refused. `email_mailbox`
        // is the live case: a first-class type in the sibling product with no
        // handler here.
        var runnable = scope.ServiceProvider
            .GetServices<Services.Worker.ISnippetHandler>()
            .Select(h => h.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toCreate = resolution.SnippetsToCreate
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static BundleSnippetPlan Plan(Services.Workflow.BundleSnippet s, bool runnable) => new()
        {
            Name = s.Name,
            Slug = s.Slug,
            Type = s.Type,
            TargetMode = string.IsNullOrWhiteSpace(s.TargetMode) ? null : s.TargetMode,
            Runnable = runnable,
        };

        var declared = bundle.Dependencies.Snippets;
        var created = new List<BundleSnippetPlan>();
        var reused = new List<BundleSnippetPlan>();
        var unrunnable = new List<BundleSnippetPlan>();

        foreach (var s in declared)
        {
            var canRun = runnable.Contains(s.Type);
            var plan = Plan(s, canRun);
            if (!canRun) unrunnable.Add(plan);
            (toCreate.Contains(s.Name) ? created : reused).Add(plan);
        }

        var nodeCount = bundle.Nodes.ValueKind == JsonValueKind.Array ? bundle.Nodes.GetArrayLength() : 0;
        var edgeCount = bundle.Edges.ValueKind == JsonValueKind.Array ? bundle.Edges.GetArrayLength() : 0;

        var notes = new List<string>(resolution.Notes);
        foreach (var u in unrunnable)
        {
            notes.Add(
                $"'{u.Name}' is a {u.Type} snippet and this deployment has no handler for that type. "
                + "The node imports; generate an equivalent, map it to an existing snippet, or leave "
                + "it disabled — it cannot run until one of those.");
        }

        // The graph the reviewer sees, with node references already translated to
        // local ids by the resolution, so the preview shows what will exist.
        var proposed = BuildProposedFromBundle(bundle, resolution.Nodes);

        draft.MarkProgress("Detecting conflicts...");
        var conflicts = await _conflicts.DetectAsync(proposed, bundle.Workflow.Environment ?? "draft", ct);

        draft.MarkProgress("Analyzing rollback risk...");
        var rollbackRisk = await AnalyzeRollbackRiskAsync(proposed, ct);

        draft.MarkReady(new AnalysisReport
        {
            FormatDetected = bundle.Kind,
            Confidence = 1.0,
            ProposedWorkflow = proposed,
            TranslationNotes = Array.Empty<string>(),
            // A bundle carries its dependencies. Nothing is missing, and saying
            // otherwise is what made the wizard invent three python snippets.
            MissingDependencies = new DependenciesReport(),
            Conflicts = conflicts,
            RollbackRisk = rollbackRisk,
            Warnings = Array.Empty<string>(),
            Bundle = new BundlePlan
            {
                SchemaVersion = bundle.SchemaVersion,
                Kind = bundle.Kind,
                ExportedByProduct = bundle.ExportedBy?.Product,
                WorkflowName = bundle.Workflow.Name,
                NodeCount = nodeCount,
                EdgeCount = edgeCount,
                SentinelsPreserved = HasSentinels(bundle.Nodes),
                SnippetsToCreate = created,
                SnippetsReused = reused,
                UnrunnableTypes = unrunnable,
                Notes = notes,
            },
        });
    }

    // `__start__` / `__end__` are the engine's reserved node ids. They are not
    // snippets and never resolve to one; reporting that they survived is how the
    // reviewer sees the graph arrived whole.
    private static bool HasSentinels(JsonElement nodes)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return false;
        var start = false;
        var end = false;
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            if (!n.TryGetProperty("snippet_id", out var sid) || sid.ValueKind != JsonValueKind.String) continue;
            var v = sid.GetString();
            if (v == "__start__") start = true;
            if (v == "__end__") end = true;
        }
        return start && end;
    }

    // The v1 document the preview renders: the bundle's workflow header plus the
    // resolved graph. Not used to commit — the commit hands the raw bundle to the
    // importer — so this is a view, not a second translation.
    private static JsonElement BuildProposedFromBundle(
        Services.Workflow.WorkflowBundle bundle, JsonElement resolvedNodes)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("schema_version", "v1");
            w.WriteString("name", bundle.Workflow.Name);
            if (bundle.Workflow.Description is { } d) w.WriteString("description", d);
            if (bundle.Workflow.InputSchema is { } schema)
            {
                w.WritePropertyName("input_schema");
                schema.WriteTo(w);
            }
            if (bundle.Workflow.Metadata is { } meta)
            {
                w.WritePropertyName("metadata");
                meta.WriteTo(w);
            }
            w.WritePropertyName("nodes");
            resolvedNodes.WriteTo(w);
            w.WritePropertyName("edges");
            bundle.Edges.WriteTo(w);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement ParseUpload(byte[] body)
    {
        // Strip the UTF-8 BOM (U+FEFF) before TrimStart so an export
        // emitted by a Windows tool (which prepends the BOM) still
        // routes to the JSON branch when the actual payload begins
        // with `{` or `[`. Without this the BOM character makes the
        // first content char non-brace and we fall through to YAML,
        // which then fails on otherwise-valid JSON.
        var text = Encoding.UTF8.GetString(body).TrimStart('﻿').TrimStart();
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        using var reader = new StringReader(text);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            return JsonDocument.Parse("{}").RootElement.Clone();

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteYamlNode(writer, stream.Documents[0].RootNode);
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    // Convert a YamlNode tree into JSON, inferring scalar types from
    // the YAML 1.2 Core Schema. Quoted scalars stay as strings; bare
    // scalars are matched against the integer / float / bool / null
    // patterns first.
    private static void WriteYamlNode(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                WriteYamlScalar(writer, scalar);
                break;
            case YamlSequenceNode seq:
                writer.WriteStartArray();
                foreach (var item in seq.Children) WriteYamlNode(writer, item);
                writer.WriteEndArray();
                break;
            case YamlMappingNode map:
                writer.WriteStartObject();
                foreach (var kvp in map.Children)
                {
                    // YAML allows non-string keys in theory; in our
                    // workflow documents they're always strings. Coerce
                    // anything else via ToString() rather than throwing.
                    var keyText = kvp.Key is YamlScalarNode ks ? ks.Value ?? "" : kvp.Key.ToString();
                    writer.WritePropertyName(keyText ?? "");
                    WriteYamlNode(writer, kvp.Value);
                }
                writer.WriteEndObject();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    // YAML 1.2 Core Schema scalar resolution. Order matters: null first
    // (so `~` and bare `null` parse), then bool, then numbers, then
    // string fallback. An explicit `!!str` tag or any non-plain quoting
    // style forces string regardless of content.
    private static readonly Regex IntegerPattern =
        new("^[-+]?[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex FloatPattern =
        new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);

    private static void WriteYamlScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";

        // Quoted scalars are always strings, even if they look numeric.
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted
            or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
        {
            writer.WriteStringValue(value);
            return;
        }

        // Explicit tags win over content inference.
        var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
        if (tag == "tag:yaml.org,2002:str")
        {
            writer.WriteStringValue(value);
            return;
        }

        if (value.Length == 0 || value == "~"
            || value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteNullValue();
            return;
        }
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteBooleanValue(true);
            return;
        }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteBooleanValue(false);
            return;
        }
        if (IntegerPattern.IsMatch(value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            writer.WriteNumberValue(i);
            return;
        }
        if (FloatPattern.IsMatch(value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            writer.WriteNumberValue(d);
            return;
        }

        writer.WriteStringValue(value);
    }

    // Confidence at which a deterministic detector overrides a user
    // hint of "generic_dag" (which means "let the agent figure it
    // out", not "force the agent regardless"). 0.85 is the same
    // threshold the FlowWeaverV1Detector + ItentialDetector reach for
    // their canonical shapes.
    private const double GenericDagOverrideThreshold = 0.85;

    private static string? BuildFormatRoutingNote(string? hint, string detectedFormat, double confidence)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        if (hint.Equals(detectedFormat, StringComparison.OrdinalIgnoreCase)) return null;

        if (hint.Equals("generic_dag", StringComparison.OrdinalIgnoreCase))
        {
            return $"Routed to the deterministic '{detectedFormat}' translator " +
                   $"(detector confidence {confidence * 100:F0}%) instead of the LLM-driven generic_dag path. " +
                   "Deterministic translators are faster and more predictable; the LLM only runs when no detector recognises the shape.";
        }

        return $"You picked '{hint}' but the analyser detected '{detectedFormat}' at {confidence * 100:F0}% confidence and used that translator.";
    }

    private (string Format, double Confidence) ChooseFormat(JsonElement doc, string? hint)
    {
        // "generic_dag" is a special hint: it asks the pipeline to
        // route — preferring a deterministic translator when a
        // detector recognises the shape, falling through to the
        // AgentTranslator only when no detector reaches the override
        // threshold. The frontend label says "let the agent
        // translate", which we read as "let the system pick the best
        // path" rather than "force the LLM no matter what".
        var isGenericDagHint = !string.IsNullOrWhiteSpace(hint)
            && hint.Equals("generic_dag", StringComparison.OrdinalIgnoreCase);

        // Any other explicit hint forces that format outright — the
        // user knows their source DSL and we trust them.
        if (!string.IsNullOrWhiteSpace(hint) && !isGenericDagHint)
        {
            var forced = _detectors.FirstOrDefault(d =>
                string.Equals(d.FormatName, hint, StringComparison.OrdinalIgnoreCase));
            if (forced is not null) return (forced.FormatName, 1.0);
        }

        // Run every detector that ISN'T the agent fallback. Pick the
        // best score that clears the override threshold for the
        // generic_dag-hint case; for auto-detect (no hint), any
        // positive score wins.
        var ranked = _detectors
            .Where(d => !d.FormatName.Equals("generic_dag", StringComparison.OrdinalIgnoreCase))
            .Select(d => (Format: d.FormatName, Score: d.Detect(doc)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (ranked.Count > 0)
        {
            var winner = ranked[0];
            if (!isGenericDagHint || winner.Score >= GenericDagOverrideThreshold)
                return (winner.Format, winner.Score);
        }

        // Explicit generic_dag hint and no confident match → honour the
        // hint and let the AgentTranslator handle the unknown shape.
        if (isGenericDagHint) return ("generic_dag", 1.0);

        return ("unknown", 0.0);
    }

    private IDslTranslator ResolveTranslator(string format)
    {
        // Prefer the specific translator; fall back to the agent for
        // generic_dag / unknown. Every translator declares its
        // FormatName statically so this is a pure dictionary lookup.
        return _translators.FirstOrDefault(t =>
            string.Equals(t.FormatName, format, StringComparison.OrdinalIgnoreCase))
            ?? _translators.First(t =>
                string.Equals(t.FormatName, "generic_dag", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<RollbackRiskReport> AnalyzeRollbackRiskAsync(
        JsonElement v1, CancellationToken ct)
    {
        var projection = new flow_weaver_backend.Models.Workflow
        {
            Nodes = v1.TryGetProperty("nodes", out var n) ? n.Clone() : default,
            Edges = v1.TryGetProperty("edges", out var e) ? e.Clone() : default,
        };
        var report = await _rollbackAnalyzer.AnalyzeAsync(projection, ct);
        return new RollbackRiskReport
        {
            NonReversible = report.NonReversible.Select(r => new RollbackRiskItem
            {
                SnippetName = r.SnippetName,
                SnippetType = r.SnippetType,
            }).ToList(),
            RequiresCompensation = report.RequiresCompensation.Select(r => new RollbackRiskItem
            {
                SnippetName = r.SnippetName,
                SnippetType = r.SnippetType,
            }).ToList(),
            Compensated = report.CompensatingFailureEdges.Select(r => new RollbackRiskItem
            {
                SnippetName = r.SnippetName,
                SnippetType = r.SnippetType,
            }).ToList(),
        };
    }
}
