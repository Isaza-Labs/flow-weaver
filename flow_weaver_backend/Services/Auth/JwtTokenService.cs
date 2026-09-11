using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using flow_weaver_backend.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace flow_weaver_backend.Services.Auth;

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenService(IOptions<JwtOptions> options, ILogger<JwtTokenService> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.Key) || _options.Key.Length < 32)
        {
            // Fail loudly at startup — better than discovering at first login
            // that tokens are being signed with a weak key.
            throw new InvalidOperationException(
                "Jwt:Key is missing or shorter than 32 characters. " +
                "Configure via env var Jwt__Key in production.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(_options.Key);
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);
    }

    public string CreateAccessToken(User user, out DateTime expiresAt)
        => CreateAccessToken(
            user.UserId, user.Username, new[] { user.Role }, null, out expiresAt);

    public string CreateAccessToken(
        Guid userId, string username,
        IEnumerable<string> roles, out DateTime expiresAt)
        => CreateAccessToken(userId, username, roles, null, out expiresAt);

    public string CreateAccessToken(
        Guid userId, string username,
        IEnumerable<string> roles, IEnumerable<string>? capabilityCeiling, out DateTime expiresAt)
    {
        try
        {
            var now = DateTime.UtcNow;
            expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

            var jti = Guid.NewGuid().ToString("N");
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new(JwtRegisteredClaimNames.Jti, jti),
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Name, username),
                new("preferred_username", username),
            };
            // One Role claim per role so the FallbackPolicy + role-based
            // policies (Admin/Operator/Viewer) see every role the caller holds.
            foreach (var role in roles)
                if (!string.IsNullOrWhiteSpace(role))
                    claims.Add(new Claim(ClaimTypes.Role, role));

            // RBAC-granular transport ceiling (messaging channel). Comma-joined
            // so the agent's execute_operation self-call re-applies it via the
            // cap_ceiling claim (ICurrentUser.CapabilityCeiling).
            if (capabilityCeiling is not null)
            {
                var joined = string.Join(",", capabilityCeiling.Where(c => !string.IsNullOrWhiteSpace(c)));
                if (joined.Length > 0) claims.Add(new Claim("cap_ceiling", joined));
            }

            var token = new JwtSecurityToken(
                issuer: _options.Issuer,
                audience: _options.Audience,
                claims: claims,
                notBefore: now,
                expires: expiresAt,
                signingCredentials: _signingCredentials);

            var wire = new JwtSecurityTokenHandler().WriteToken(token);

            _logger.LogDebug(
                "auth.jwt.issued user_id={UserId} jti={Jti} expires_at={ExpiresAt}",
                userId, jti, expiresAt);

            return wire;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "auth.jwt.issue_failed user_id={UserId}",
                userId);
            throw;
        }
    }
}
