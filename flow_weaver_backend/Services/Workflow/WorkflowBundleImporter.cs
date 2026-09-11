using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Workflow;

public interface IWorkflowBundleImporter
{
    /// <summary>
    /// Creates everything a parsed bundle describes — snippets, sub-workflows,
    /// the workflow, its triggers — in one transaction, or nothing. The
    /// response carries the import notes and what was created.
    /// </summary>
    Task<WorkflowResponse> ImportAsync(WorkflowBundle bundle, CancellationToken ct);
}

/// <summary>
/// The write side of a bundle import (bundle/SPEC.md §7).
/// </summary>
/// <remarks>
/// Resolution (<see cref="IWorkflowBundleService.ResolveAsync"/>) is pure: it
/// reads this instance and decides. This class is the only place that writes,
/// and it writes in dependency order — snippets, then sub-workflows callee
/// first (so each caller's <c>subflow_workflow_id</c> can be remapped to a
/// row that now exists), then the workflow, then its triggers — inside one
/// transaction. A validation failure on the third sub-workflow therefore
/// leaves no orphaned snippets behind: the outcome is exactly one of
/// "refused, nothing created" or "imported, with notes".
///
/// Every workflow goes through the SAME create path as a hand-authored one
/// (<see cref="IWorkflow.PostAsync"/>), so schema + reference + DAG validation
/// still applies; every trigger through the same service as the UI, so it
/// gets a fresh signing secret the same way.
/// </remarks>
public sealed class WorkflowBundleImporter(
    IWorkflowBundleService bundles,
    IWorkflow workflows,
    IWorkflowTrigger triggers,
    ISnippetRepository snippets,
    IUnitOfWork uow,
    ILogger<WorkflowBundleImporter> logger) : IWorkflowBundleImporter
{
    public async Task<WorkflowResponse> ImportAsync(WorkflowBundle bundle, CancellationToken ct)
    {
        var resolution = await bundles.ResolveAsync(bundle, ct);
        var notes = resolution.Notes.ToList();
        var createdWorkflows = new List<CreatedRef>();
        var createdTriggers = new List<CreatedRef>();
        WorkflowResponse? created = null;

        await uow.ExecuteInTransactionAsync(async c =>
        {
            foreach (var snippet in resolution.SnippetsToCreate)
                snippets.Add(snippet);
            if (resolution.SnippetsToCreate.Count > 0)
                await snippets.SaveChangesAsync(c);

            // Sub-workflows first, callees before callers, remapping each
            // caller's subflow ids through what has been created so far.
            var idMap = new Dictionary<Guid, Guid>(resolution.IdMap);
            foreach (var sub in resolution.Subflows)
            {
                var def = sub.Definition;
                // Sub-workflows are workflows too, and get the same treatment.
                var (nodes, subEdges) = NodeReferences.AdoptLocalSentinels(
                    NodeReferences.Remap(sub.Nodes, idMap), def.Edges);

                // Compared in the adopted shape, because that is what would be
                // stored — comparing the incoming shape against local rows would
                // never match and would clone the sub-workflow on every import.
                var existing = await bundles.FindIdenticalWorkflowAsync(def.Name, nodes, subEdges, c);
                if (existing is { } localId)
                {
                    idMap[def.Id] = localId;
                    notes.Add($"sub-workflow '{def.Name}' already exists here with identical nodes and edges; reused.");
                    continue;
                }

                var response = await CreateOrThrowAsync(new CreateWorkflow
                {
                    Name = def.Name,
                    Description = def.Description,
                    Nodes = nodes,
                    Edges = subEdges,
                    InputSchema = def.InputSchema,
                    Metadata = WithIsSubflow(def.Metadata),
                }, $"sub-workflow '{def.Name}'");

                idMap[def.Id] = response.WorkflowId;
                createdWorkflows.Add(new CreatedRef(response.WorkflowId, response.Name));
            }

            // Adopt the ends rather than importing a second pair. A workflow here
            // is born with a start and an end; a graph that brings its own leaves
            // four, and only the imported two are on the edges.
            var (mainNodes, mainEdges) = NodeReferences.AdoptLocalSentinels(
                NodeReferences.Remap(resolution.Nodes, idMap), bundle.Edges);

            created = await CreateOrThrowAsync(new CreateWorkflow
            {
                Name = bundle.Workflow.Name,
                Description = bundle.Workflow.Description,
                Nodes = mainNodes,
                Edges = mainEdges,
                InputSchema = bundle.Workflow.InputSchema,
                Metadata = bundle.Workflow.Metadata,
            }, $"workflow '{bundle.Workflow.Name}'");

            // A trigger that this instance rejects (an unparseable cron, say)
            // is skipped with a note rather than refusing the whole import:
            // the workflow is still complete and runnable by hand.
            foreach (var dto in resolution.Triggers)
            {
                var result = await triggers.PostForWorkflowAsync(created.WorkflowId, dto);
                var trigger = Unwrap<WorkflowTriggerResponse>(result, out var error);
                if (trigger is null)
                {
                    notes.Add($"trigger '{dto.Name}' ({dto.Type}) was skipped: {error}");
                    continue;
                }
                createdTriggers.Add(new CreatedRef(trigger.WorkflowTriggerId, trigger.Name));
            }
        }, ct);

        created!.ImportNotes = notes;
        created.CreatedSnippets = resolution.SnippetsToCreate
            .Select(s => new CreatedRef(s.SnippetId, s.Name)).ToList();
        created.CreatedWorkflows = createdWorkflows;
        created.CreatedTriggers = createdTriggers;

        logger.LogInformation(
            "workflow.import.bundle workflow_id={WorkflowId} workflow_name={Name} snippets_created={Snippets} "
            + "subflows_created={Subflows} triggers_created={Triggers} notes={Notes}",
            created.WorkflowId, created.Name, resolution.SnippetsToCreate.Count,
            createdWorkflows.Count, createdTriggers.Count, notes.Count);

        return created;
    }

    private async Task<WorkflowResponse> CreateOrThrowAsync(CreateWorkflow dto, string what)
    {
        var result = await workflows.PostAsync(dto);
        var response = Unwrap<WorkflowResponse>(result, out var error);
        if (response is not null) return response;

        // The create path answers with a 4xx payload; inside a transaction
        // that has to become an exception so nothing stays half-created.
        throw new ValidationException(
            $"{what} was rejected by this instance's workflow validation: {error}",
            "bundle_workflow_invalid");
    }

    private static T? Unwrap<T>(ActionResult<T> result, out string error) where T : class
    {
        error = string.Empty;
        if (result.Value is { } direct) return direct;
        if (result.Result is ObjectResult obj)
        {
            if (obj.Value is T good) return good;
            error = Describe(obj.Value) ?? $"HTTP {obj.StatusCode}";
            return null;
        }
        error = "no response";
        return null;
    }

    // Service errors come back as anonymous `{ error, details? }` payloads.
    private static string? Describe(object? payload)
    {
        if (payload is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return root.ToString();
            var error = root.TryGetProperty("error", out var e) ? e.ToString() : null;
            if (root.TryGetProperty("details", out var d) && d.ValueKind != JsonValueKind.Null)
                error = $"{error}: {d.GetRawText()}";
            return error ?? root.GetRawText();
        }
        catch
        {
            return payload.ToString();
        }
    }

    // A sub-workflow is a workflow tagged `metadata.is_subflow = true`; the
    // tag travels, but a bundle written by a product without it must still
    // land its sub-workflows where the subflow picker can find them.
    private static JsonElement WithIsSubflow(JsonElement? metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (metadata is { ValueKind: JsonValueKind.Object } m)
                foreach (var p in m.EnumerateObject())
                    if (!p.NameEquals("is_subflow")) p.WriteTo(writer);
            writer.WriteBoolean("is_subflow", true);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
