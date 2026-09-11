using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Auth;

// Creates signed JWT access tokens. Each token embeds claims the app
// relies on downstream: NameIdentifier (UserId), role,
// preferred_username, plus standard iss/aud/exp/iat/jti.
public interface IJwtTokenService
{
    string CreateAccessToken(User user, out DateTime expiresAt);

    // Mint a token from raw identity claims rather than a User row. Used by
    // background agent runs (e.g. the messaging worker) that have a bound
    // ICurrentUser but no HTTP request to borrow a bearer from. The roles
    // passed in are the EFFECTIVE roles already in force, so the token can
    // never grant more than the caller currently has.
    string CreateAccessToken(
        Guid userId, string username,
        IEnumerable<string> roles, out DateTime expiresAt);

    // As above, plus an optional capability ceiling embedded as a `cap_ceiling`
    // claim. Used by the messaging worker so a channel's cap survives the
    // agent's execute_operation self-call (ICurrentUser re-reads the claim).
    string CreateAccessToken(
        Guid userId, string username,
        IEnumerable<string> roles, IEnumerable<string>? capabilityCeiling, out DateTime expiresAt);
}
