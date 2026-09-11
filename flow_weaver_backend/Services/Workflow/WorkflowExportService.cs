using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Compiler;
using flow_weaver_backend.Services.Identity;
using Models = flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Workflow;

public interface IWorkflowExportService
{
    Task<ExportPayload> ExportAsync(Guid workflowId, string format, CancellationToken ct);
    ImportPayload ParseImport(string raw, string format);
}

// Result of a workflow export. The controller writes Content +
// Content-Type + filename to the HTTP response.
public sealed record ExportPayload(string Content, string ContentType, string Filename);

// Shape extracted from an uploaded YAML / JSON file. The controller
// hands these fields to IWorkflow.PostAsync so the import path goes
// through the same schema + reference validation as POST /workflow.
public sealed class ImportPayload
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonElement? Nodes { get; set; }
    public JsonElement? Edges { get; set; }
    public JsonElement? InputSchema { get; set; }
    public JsonElement? Metadata { get; set; }
}

// Owns the export/import (YAML/JSON/python/ansible/…) shape conversion
// so the controller stays a thin adapter. Throws NotFoundException for
// missing workflows and ValidationException for parse/format errors;
// the global handler maps both to problem+json.
public sealed class WorkflowExportService(
    IRepository<Models.Workflow> workflows,
    IRepository<Models.Snippet> snippets,
    IWorkflowYamlCompiler yamlCompiler,
    IEnumerable<IWorkflowExporter> exporters) : IWorkflowExportService
{
    public async Task<ExportPayload> ExportAsync(Guid workflowId, string format, CancellationToken ct)
    {
        var wf = await workflows.GetByIdAsync(workflowId, tracking: false, ct: ct)
            ?? throw new NotFoundException("workflow", workflowId);

        var safeName = SafeFilename(wf.Name);
        var fmt = format.ToLowerInvariant();

        switch (fmt)
        {
            case "yaml":
            case "yml":
                return new ExportPayload(yamlCompiler.Compile(wf),
                    "application/x-yaml", $"{safeName}.yaml");

            case "json":
                var json = JsonSerializer.Serialize(new
                {
                    version = 1,
                    workflow = new { id = wf.WorkflowId, wf.Name, wf.Description, wf.Environment, wf.SchemaVersion },
                    nodes = wf.Nodes,
                    edges = wf.Edges,
                    input_schema = wf.InputSchema,
                    metadata = wf.Metadata,
                });
                return new ExportPayload(json, "application/json", $"{safeName}.json");

            default:
                var exporter = exporters.FirstOrDefault(e =>
                    string.Equals(e.Format, fmt, StringComparison.OrdinalIgnoreCase));
                if (exporter is null)
                {
                    var supported = string.Join(", ", new[] { "yaml", "json" }
                        .Concat(exporters.Select(e => e.Format)));
                    throw new ValidationException(
                        $"unsupported format '{format}' ({supported})",
                        "format_unsupported");
                }

                // Resolve referenced snippets up front so the exporter
                // can embed bodies / pick modules without re-querying
                // the DB per node.
                var snippetIds = ExtractSnippetIds(wf.Nodes);
                var snippetList = await snippets.ListByIdsAsync(snippetIds, ct: ct);
                var snippetMap = snippetList.ToDictionary(s => s.SnippetId);

                var content = await exporter.ExportAsync(wf, snippetMap, ct);
                return new ExportPayload(content,
                    exporter.ContentType,
                    $"{safeName}.{exporter.FileExtension}");
        }
    }

    public ImportPayload ParseImport(string raw, string format)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ValidationException("request body is empty", "body_empty");

        ImportPayload? payload;
        try
        {
            payload = format.ToLowerInvariant() switch
            {
                "yaml" or "yml" => ParseYaml(raw),
                "json" => ParseJson(raw),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not DomainException)
        {
            throw new ValidationException(
                $"failed to parse {format}: {ex.Message}", "parse_failed");
        }

        if (payload is null)
            throw new ValidationException(
                $"unsupported import format '{format}' (yaml, json)",
                "format_unsupported");

        if (string.IsNullOrWhiteSpace(payload.Name))
            throw new ValidationException("workflow.name is required", "name_required");

        return payload;
    }

    // Round-trips YAML through JSON so we can hand JsonElements to the
    // create path without re-implementing the schema mapping.
    private static ImportPayload ParseYaml(string raw)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(
                YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .Build();
        var obj = deserializer.Deserialize<object?>(raw);
        var json = JsonSerializer.Serialize(obj);
        using var doc = JsonDocument.Parse(json);
        return ExtractPayload(doc.RootElement);
    }

    private static ImportPayload ParseJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return ExtractPayload(doc.RootElement);
    }

    private static ImportPayload ExtractPayload(JsonElement root)
    {
        var p = new ImportPayload();
        if (root.ValueKind != JsonValueKind.Object) return p;

        if (root.TryGetProperty("workflow", out var wf)
            && wf.ValueKind == JsonValueKind.Object)
        {
            if (wf.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                p.Name = name.GetString();
            if (wf.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                p.Description = desc.GetString();
        }
        // Top-level fall-backs let users hand-write a minimal import file.
        if (p.Name is null && root.TryGetProperty("name", out var topName) && topName.ValueKind == JsonValueKind.String)
            p.Name = topName.GetString();
        if (p.Description is null && root.TryGetProperty("description", out var topDesc) && topDesc.ValueKind == JsonValueKind.String)
            p.Description = topDesc.GetString();

        if (root.TryGetProperty("nodes", out var nodes)) p.Nodes = nodes.Clone();
        if (root.TryGetProperty("edges", out var edges)) p.Edges = edges.Clone();
        if (root.TryGetProperty("input_schema", out var ins)) p.InputSchema = ins.Clone();
        if (root.TryGetProperty("metadata", out var meta)) p.Metadata = meta.Clone();
        return p;
    }

    private static HashSet<Guid> ExtractSnippetIds(JsonElement nodes)
    {
        var ids = new HashSet<Guid>();
        if (nodes.ValueKind != JsonValueKind.Array) return ids;
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.TryGetProperty("snippet_id", out var sid)
                && sid.ValueKind == JsonValueKind.String
                && Guid.TryParse(sid.GetString(), out var g))
                ids.Add(g);
        }
        return ids;
    }

    private static string SafeFilename(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "workflow" : s;
    }
}
