using System.Security.Claims;

namespace flow_weaver_backend.Services.Identity;

// Default ICurrentUser binding. Reads from JWT claims, but also exposes
// a Bind() method so a caller running outside any HTTP auth context
// (webhook receiver, scheduler) can populate the identity after creating
// a fresh DI scope.
//
// Bind() is an explicit one-shot — once set, claim-based reads are
// ignored, so the override never leaks past the scope that called it.
public sealed class MutableCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    private bool _bound;
    private Guid? _userId;
    private string? _username;
    private IReadOnlyList<string> _roles = Array.Empty<string>();
    private IReadOnlyCollection<string>? _capabilityCeiling;

    public MutableCurrentUser(IHttpContextAccessor http)
    {
        _http = http;
    }

    public void Bind(
        Guid? userId, string? username,
        IReadOnlyList<string>? roles = null,
        IReadOnlyCollection<string>? capabilityCeiling = null)
    {
        _bound = true;
        _userId = userId;
        _username = username;
        _roles = roles ?? Array.Empty<string>();
        _capabilityCeiling = capabilityCeiling;
    }

    public bool IsAuthenticated =>
        _bound || (_http.HttpContext?.User.Identity?.IsAuthenticated ?? false);

    public Guid UserId
    {
        get
        {
            if (_bound) return _userId ?? Guid.Empty;
            var claim = _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException(
                    "NameIdentifier claim missing — ICurrentUser accessed on an anonymous request.");
            return Guid.Parse(claim);
        }
    }

    public string? Username =>
        _bound ? _username : _http.HttpContext?.User.FindFirstValue(ClaimTypes.Name);

    public IReadOnlyList<string> Roles =>
        _bound
            ? _roles
            : (IReadOnlyList<string>?)_http.HttpContext?.User.FindAll(ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList()
            ?? Array.Empty<string>();

    public IReadOnlyCollection<string>? CapabilityCeiling
    {
        get
        {
            if (_bound) return _capabilityCeiling;
            var raw = _http.HttpContext?.User.FindFirstValue("cap_ceiling");
            return string.IsNullOrEmpty(raw)
                ? null
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
