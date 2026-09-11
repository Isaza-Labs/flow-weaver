using flow_weaver_backend.Services.Ai.Tools;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// The real AppLinks over an explicit base url — handlers that hand the agent
// a page link take IAppLinks, and exercising the real one keeps the tests
// honest about the shape those links have.
internal static class TestAppLinks
{
    // No PublicBaseUrl configured: links come out as bare paths.
    internal static IAppLinks Relative() =>
        new AppLinks(Options.Create(new WorkflowExecutorOptions()));

    internal static IAppLinks WithBase(string baseUrl) =>
        new AppLinks(Options.Create(new WorkflowExecutorOptions { PublicBaseUrl = baseUrl }));
}
