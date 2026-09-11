using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Ai.Tools;

// Operator-facing links to frontend pages, handed to the agent inside tool
// results so its answer can carry a real URL instead of a bare uuid the user
// has to paste into a search box.
//
// Mirrors WorkflowExecutor.BuildRunUrl: absolute when `Workflow:PublicBaseUrl`
// is configured, otherwise the bare path. A relative link is still useful in
// the web chat (same origin, so it resolves), which is why the fallback isn't
// an empty string — but set PublicBaseUrl if answers also leave over a
// messaging channel, where relative links are dead text.
public interface IAppLinks
{
    // `<base>/workflows/<id>` — the workflow editor page.
    string Workflow(Guid workflowId);
}

public sealed class AppLinks : IAppLinks
{
    private readonly string? _base;

    public AppLinks(IOptions<WorkflowExecutorOptions> options)
    {
        _base = options.Value.PublicBaseUrl?.TrimEnd('/');
    }

    public string Workflow(Guid workflowId) => Build($"/workflows/{workflowId}");

    private string Build(string path) =>
        string.IsNullOrWhiteSpace(_base) ? path : _base + path;
}
