using WorkflowModel = flow_weaver_backend.Models.Workflow;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Services.Compiler;

// Renders a Workflow into an external artifact (script, playbook, file).
// Implementations are stateless and registered as singletons. The Export
// endpoint loads the workflow + every Snippet referenced by its nodes and
// passes both to ExportAsync — exporters MUST tolerate missing snippets
// (e.g. sentinel ids `__start__`, `__end__`) and skip them gracefully.
public interface IWorkflowExporter
{
    // Lower-case format key used in `?format=` query string (python, ansible, ...).
    string Format { get; }

    // MIME type used in the response Content-Type header.
    string ContentType { get; }

    // Suggested file extension WITHOUT leading dot (py, yml, json, ...).
    string FileExtension { get; }

    // `snippets` is keyed by SnippetId. Sentinel ids (__start__, __end__,
    // subflow) and any unresolved id will simply be missing from the map.
    Task<string> ExportAsync(
        WorkflowModel workflow,
        IReadOnlyDictionary<Guid, SnippetModel> snippets,
        CancellationToken ct);
}
