using flow_weaver_backend.Services.Observability;
using System.Security.Claims;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public AuthController(IAuthService auth, AppDbContext db, IWebHostEnvironment env)
    {
        _auth = auth;
        _db = db;
        _env = env;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConfiguration.AuthLogin)]
    public Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
        => _auth.LoginAsync(request, Ip(), UserAgent(), ct);

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConfiguration.AuthLogin)]
    public Task<ActionResult<LoginResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
        => _auth.RefreshAsync(request, Ip(), UserAgent(), ct);

    [HttpPost("logout")]
    [Authorize]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct)
        => _auth.LogoutAsync(request, CurrentUserId(), Ip(), UserAgent(), ct);

    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
        => _auth.ChangePasswordAsync(request, CurrentUserId(), Ip(), UserAgent(), ct);

    [HttpGet("me")]
    [Authorize]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult<MeResponse>> Me(CancellationToken ct)
        => _auth.MeAsync(CurrentUserId(), ct);

    // First-run bootstrap. Creates the initial admin user + tokens.
    //
    //   Development       → always available (the curl-the-API helper it
    //                       has always been).
    //   Any other env     → available EXACTLY ONCE: only while the users
    //                       table is empty (fresh install — nobody could
    //                       log in anyway). The deploy/setup wizard uses
    //                       this to create the operator's admin account.
    //                       As soon as any user exists it 404s again, so
    //                       there is no leak or takeover surface.
    //
    // Optional body sets the username/password; omitted → "admin" + a
    // generated one-time password returned in the response.
    [HttpPost("bootstrap")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConfiguration.AuthLogin)]
    public async Task<ActionResult<BootstrapResponse>> Bootstrap(
        [FromBody] BootstrapRequest? request, CancellationToken ct)
    {
        if (!_env.IsDevelopment() && await _db.Users.AnyAsync(ct))
            return NotFound();

        return await _auth.BootstrapAsync(request, Ip(), UserAgent(), ct);
    }

    [HttpGet("events")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<AuthEventResponse>>> Events(
        // Defaulted so callers can name only the filter they want, matching
        // AuditController.Events.
        [FromQuery] Guid? user_id = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool include_unattributed = false,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        // Sign-in failures for an unknown username never resolve to a user
        // row. They are kept for anomaly detection — a burst of them is
        // account enumeration — and are hidden by default so they do not
        // drown out failures on real accounts.
        var query = _db.AuthEvents.AsNoTracking()
            .Where(e => include_unattributed || e.UserId != null);

        if (user_id.HasValue) query = query.Where(e => e.UserId == user_id.Value);
        if (from.HasValue) query = query.Where(e => e.At >= from.Value);
        if (to.HasValue) query = query.Where(e => e.At <= to.Value);

        var events = await query
            .OrderByDescending(e => e.At)
            .Take(Math.Min(limit, 500))
            .ToListAsync(ct);

        return new OkObjectResult(events.Select(e => new AuthEventResponse
        {
            AuthEventId = e.AuthEventId,
            UserId = e.UserId,
            Event = e.Event,
            Ip = e.Ip,
            UserAgent = e.UserAgent,
            At = e.At,
            Metadata = e.Metadata,
        }));
    }

    // ─── helpers ────────────────────────────────────────────────────
    private string Ip() =>
        ClientIp.Resolve(HttpContext) ?? string.Empty;

    private string UserAgent() =>
        Request.Headers.UserAgent.ToString();

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

}
