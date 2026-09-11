namespace flow_weaver_backend.Services.Auth;

// Append-only write surface for the auth audit trail. Each call persists
// one AuthEvent row. Never reads — reads go through the admin query
// endpoint directly against AppDbContext.
public interface IAuthAuditLogger
{
    Task LogAsync(
        AuthEventKind kind,
        Guid? userId,
        string ip,
        string userAgent,
        object? metadata = null,
        CancellationToken ct = default);
}

// Stringified into AuthEvent.Event. Using an enum (not raw strings) here
// keeps call sites from drifting.
public enum AuthEventKind
{
    LoginSuccess,
    LoginFailure,
    Logout,
    PasswordChange,
    Lockout,
    Refresh,
    TokenRevoked,
}

public static class AuthEventKindExtensions
{
    public static string ToWireString(this AuthEventKind kind) => kind switch
    {
        AuthEventKind.LoginSuccess => "login_success",
        AuthEventKind.LoginFailure => "login_failure",
        AuthEventKind.Logout => "logout",
        AuthEventKind.PasswordChange => "password_change",
        AuthEventKind.Lockout => "lockout",
        AuthEventKind.Refresh => "refresh",
        AuthEventKind.TokenRevoked => "token_revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
