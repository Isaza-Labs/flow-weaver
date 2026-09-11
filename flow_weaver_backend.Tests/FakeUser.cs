using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Tests;

// Shared identity stub — tests pin UserId to a fixed GUID so assertions
// see the same value every time. Kept deliberately read-only; mutating
// identity mid-test usually means the test is doing too much.
internal sealed class FakeUser : ICurrentUser
{
    public Guid UserId { get; init; } = new("22222222-2222-2222-2222-222222222222");
    public string? Username { get; init; } = "tester";
    public IReadOnlyList<string> Roles { get; init; } = new[] { "admin" };
    public bool IsAuthenticated => true;

    // No transport capability ceiling by default (web-request identity).
    public IReadOnlyCollection<string>? CapabilityCeiling { get; init; }
}
