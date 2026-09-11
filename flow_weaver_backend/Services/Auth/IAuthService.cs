using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Auth;

// Auth surface for the AuthController. All methods return ActionResult so
// the controller stays a passthrough — the service owns the HTTP shape
// (200/201/400/401/409/429 with the right body).
//
// The `ip` and `userAgent` are captured by the controller from the request
// and passed in because the service layer shouldn't reach into
// IHttpContextAccessor (easier to test, clearer dependencies).
public interface IAuthService
{
    Task<ActionResult<LoginResponse>> LoginAsync(
        LoginRequest request, string ip, string userAgent, CancellationToken ct);

    Task<ActionResult<LoginResponse>> RefreshAsync(
        RefreshRequest request, string ip, string userAgent, CancellationToken ct);

    Task<ActionResult> LogoutAsync(
        RefreshRequest request, Guid userId, string ip, string userAgent, CancellationToken ct);

    Task<ActionResult> ChangePasswordAsync(
        ChangePasswordRequest request, Guid userId,
        string ip, string userAgent, CancellationToken ct);

    Task<ActionResult<MeResponse>> MeAsync(Guid userId, CancellationToken ct);

    // First-run bootstrap: creates the initial admin + returns tokens.
    // Availability (Development always; otherwise only while the users
    // table is empty) is guarded in the controller. `request` optionally
    // carries the operator-chosen username/password; null keeps the legacy
    // "admin" + generated-password behavior.
    Task<ActionResult<BootstrapResponse>> BootstrapAsync(
        BootstrapRequest? request, string ip, string userAgent, CancellationToken ct);
}
