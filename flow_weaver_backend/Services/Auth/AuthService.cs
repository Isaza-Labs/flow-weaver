using System.Security.Cryptography;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Observability;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Auth;

public class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IUnitOfWork _uow;
    private readonly IJwtTokenService _jwt;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly IAuthAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly IPasswordHasher<User> _hasher;
    private readonly JwtOptions _jwtOptions;
    private readonly LockoutOptions _lockout;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IUnitOfWork uow,
        IJwtTokenService jwt,
        IRefreshTokenService refreshTokens,
        IPasswordPolicy passwordPolicy,
        IAuthAuditLogger audit,
        ITraceLogger trace,
        IPasswordHasher<User> hasher,
        IOptions<JwtOptions> jwtOptions,
        IOptions<AuthOptions> authOptions,
        ILogger<AuthService> logger)
    {
        _users = users;
        _uow = uow;
        _jwt = jwt;
        _refreshTokens = refreshTokens;
        _passwordPolicy = passwordPolicy;
        _audit = audit;
        _trace = trace;
        _hasher = hasher;
        _jwtOptions = jwtOptions.Value;
        _lockout = authOptions.Value.Lockout;
        _logger = logger;
    }

    public async Task<ActionResult<LoginResponse>> LoginAsync(
        LoginRequest request, string ip, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            _logger.LogWarning("auth.login.invalid_credentials reason=missing_fields ip={Ip}", ip);
            return new BadRequestObjectResult(new { error = "username and password are required" });
        }

        // Username is globally unique, so it resolves the account on its own.
        var user = await _users.FindActiveByUsernameAsync(request.Username, ct);

        // Constant-ish timing: we still run the hasher even on missing user
        // so an attacker can't tell "user exists" from response timing.
        if (user is null)
        {
            _hasher.HashPassword(new User(), request.Password); // throwaway work
            await _audit.LogAsync(
                AuthEventKind.LoginFailure, null, ip, userAgent,
                new { reason = "unknown_user", attempted_username = request.Username },
                ct);
            _logger.LogWarning(
                "auth.login.invalid_credentials reason=unknown_user ip={Ip}",
                ip);
            return Unauthorized();
        }

        // Locked? Return 429 with Retry-After, but NEVER reveal whether the
        // password was right or wrong — that'd leak user existence too.
        if (user.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
        {
            await _audit.LogAsync(
                AuthEventKind.LoginFailure, user.UserId, ip, userAgent,
                new { reason = "locked", locked_until = lockedUntil },
                ct);
            _logger.LogWarning(
                "auth.login.locked user_id={UserId} locked_until={LockedUntil} ip={Ip}",
                user.UserId, lockedUntil, ip);
            return LockoutResponse(lockedUntil);
        }

        var check = _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (check == PasswordVerificationResult.Failed)
        {
            await RegisterFailedLoginAsync(user, ip, userAgent, ct);
            _logger.LogWarning(
                "auth.login.invalid_credentials reason=wrong_password user_id={UserId} ip={Ip}",
                user.UserId, ip);
            return Unauthorized();
        }

        // Success: reset lockout, write audit, mint tokens.
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _users.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuthEventKind.LoginSuccess, user.UserId, ip, userAgent,
            null, ct);

        await _trace.EventAsync("auth.login", "auth", "completed", new
        {
            user_id = user.UserId,
            username = user.Username,
        }, ct: ct);

        _logger.LogInformation(
            "auth.login.ok user_id={UserId} ip={Ip}",
            user.UserId, ip);

        return await IssueTokensAsync(user, ip, ct);
    }

    public async Task<ActionResult<LoginResponse>> RefreshAsync(
        RefreshRequest request, string ip, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            _logger.LogWarning("auth.refresh.invalid reason=missing_token ip={Ip}", ip);
            return new BadRequestObjectResult(new { error = "refresh_token is required" });
        }

        var rotate = await _refreshTokens.RotateAsync(request.RefreshToken, ip, ct);

        switch (rotate.Status)
        {
            case RotateStatus.Unknown:
            case RotateStatus.Expired:
                await _audit.LogAsync(
                    AuthEventKind.LoginFailure, null, ip, userAgent,
                    new { reason = "refresh_invalid", status = rotate.Status.ToString() },
                    ct);
                _logger.LogWarning(
                    "auth.refresh.invalid status={Status} ip={Ip}",
                    rotate.Status, ip);
                return Unauthorized();

            case RotateStatus.ReplayDetected:
                await _audit.LogAsync(
                    AuthEventKind.TokenRevoked, null, ip, userAgent,
                    new { reason = "replay_detected" },
                    ct);
                _logger.LogWarning(
                    "auth.refresh.invalid status=replay_detected ip={Ip}",
                    ip);
                return Unauthorized();

            case RotateStatus.Ok:
                var user = rotate.User!;
                var accessToken = _jwt.CreateAccessToken(user, out var expiresAt);

                await _audit.LogAsync(
                    AuthEventKind.Refresh, user.UserId, ip, userAgent,
                    null, ct);

                _logger.LogInformation(
                    "auth.refresh.ok user_id={UserId} ip={Ip}",
                    user.UserId, ip);

                return new OkObjectResult(new LoginResponse
                {
                    AccessToken = accessToken,
                    RefreshToken = rotate.NewRawToken!,
                    UserId = user.UserId,
                    Username = user.Username,
                    Role = user.Role,
                    ExpiresIn = (int)(expiresAt - DateTime.UtcNow).TotalSeconds,
                });

            default:
                _logger.LogError(
                    "auth.refresh.invalid status=unexpected:{Status} ip={Ip}",
                    rotate.Status, ip);
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<ActionResult> LogoutAsync(
        RefreshRequest request, Guid userId, string ip, string userAgent, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            await _refreshTokens.RevokeAsync(request.RefreshToken, ct);

        await _audit.LogAsync(AuthEventKind.Logout, userId, ip, userAgent, null, ct);

        _logger.LogInformation(
            "auth.logout.ok user_id={UserId} ip={Ip}",
            userId, ip);

        return new NoContentResult();
    }

    public async Task<ActionResult> ChangePasswordAsync(
        ChangePasswordRequest request, Guid userId,
        string ip, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            _logger.LogWarning(
                "auth.password_change.failed reason=missing_fields user_id={UserId} ip={Ip}",
                userId, ip);
            return new BadRequestObjectResult(new { error = "current_password and new_password are required" });
        }

        var user = await _users.FindActiveByIdAsync(userId, ct);
        if (user is null)
        {
            _logger.LogWarning(
                "auth.password_change.failed reason=user_not_found user_id={UserId} ip={Ip}",
                userId, ip);
            return Unauthorized();
        }

        var check = _hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
        if (check == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning(
                "auth.password_change.failed reason=wrong_current_password user_id={UserId} ip={Ip}",
                user.UserId, ip);
            return Unauthorized();
        }

        var policy = _passwordPolicy.Validate(request.NewPassword, user.Username, user.Email);
        if (!policy.IsValid)
        {
            _logger.LogWarning(
                "auth.password_change.failed reason=policy_violation user_id={UserId}",
                user.UserId);
            return new BadRequestObjectResult(new { error = policy.Error });
        }

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);
        user.PasswordChangedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await _users.SaveChangesAsync(ct);

        // Any active session must be forced back through login — the
        // credential that authorized its refresh chain no longer exists.
        await _refreshTokens.RevokeAllForUserAsync(user.UserId, ct);

        await _audit.LogAsync(
            AuthEventKind.PasswordChange, user.UserId, ip, userAgent, null, ct);

        _logger.LogInformation(
            "auth.password_change.ok user_id={UserId} ip={Ip}",
            user.UserId, ip);

        return new NoContentResult();
    }

    public async Task<ActionResult<MeResponse>> MeAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.FindActiveByIdAsync(userId, ct);
        if (user is null)
        {
            _logger.LogWarning("auth.me.not_found user_id={UserId}", userId);
            return Unauthorized();
        }

        return new OkObjectResult(new MeResponse
        {
            UserId = user.UserId,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role,
            PasswordChangedAt = user.PasswordChangedAt,
        });
    }

    public async Task<ActionResult<BootstrapResponse>> BootstrapAsync(
        BootstrapRequest? request, string ip, string userAgent, CancellationToken ct)
    {
        // Operator-chosen credentials (the deploy/setup wizard) — validated
        // here so a typo'd short password fails loudly instead of minting an
        // account the operator can't defend.
        var requestedUsername = request?.Username?.Trim();
        var requestedPassword = request?.Password;
        if (requestedPassword is not null && requestedPassword.Length < 8)
            return new BadRequestObjectResult(new { error = "password must be at least 8 characters" });
        if (requestedUsername is { Length: > 64 })
            return new BadRequestObjectResult(new { error = "username must be 64 characters or fewer" });

        // Username is globally unique, so a second bootstrap must not collide
        // with the admin the first one created.
        var username = string.IsNullOrWhiteSpace(requestedUsername) ? "admin" : requestedUsername;
        var baseUsername = username;
        var suffix = 1;
        while (await _users.FindActiveByUsernameAsync(username, ct) is not null)
        {
            suffix++;
            username = $"{baseUsername}-{suffix}";
        }

        var initialPassword = requestedPassword ?? GenerateInitialPassword();
        var admin = new User
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@local",
            Role = "admin",
            PasswordChangedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true,
        };
        admin.PasswordHash = _hasher.HashPassword(admin, initialPassword);

        _users.Add(admin);
        try
        {
            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "auth.register.validation_failed reason=persist_failed username={Username}",
                username);
            throw;
        }

        await _audit.LogAsync(
            AuthEventKind.LoginSuccess, admin.UserId, ip, userAgent,
            new { reason = "bootstrap" }, ct);

        var accessToken = _jwt.CreateAccessToken(admin, out _);
        var (refreshToken, _) = await _refreshTokens.CreateAsync(admin, ip, ct);

        _logger.LogInformation(
            "auth.register.ok user_id={UserId} ip={Ip}",
            admin.UserId, ip);

        return new OkObjectResult(new BootstrapResponse
        {
            UserId = admin.UserId,
            Username = admin.Username,
            InitialPassword = initialPassword,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
        });
    }

    // ─── helpers ────────────────────────────────────────────────────

    private async Task<ActionResult<LoginResponse>> IssueTokensAsync(
        User user, string ip, CancellationToken ct)
    {
        var accessToken = _jwt.CreateAccessToken(user, out var expiresAt);
        var (refreshToken, _) = await _refreshTokens.CreateAsync(user, ip, ct);
        return new OkObjectResult(new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserId = user.UserId,
            Username = user.Username,
            Role = user.Role,
            ExpiresIn = (int)(expiresAt - DateTime.UtcNow).TotalSeconds,
        });
    }

    private async Task RegisterFailedLoginAsync(
        User user, string ip, string userAgent, CancellationToken ct)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= _lockout.MaxFailedAttempts)
        {
            user.LockedUntil = DateTime.UtcNow.AddMinutes(_lockout.LockoutMinutes);
            user.FailedLoginCount = 0; // reset counter when lockout trips
            await _audit.LogAsync(
                AuthEventKind.Lockout, user.UserId, ip, userAgent,
                new { until = user.LockedUntil }, ct);
            _logger.LogWarning(
                "auth.login.locked reason=threshold_reached user_id={UserId} locked_until={LockedUntil} ip={Ip}",
                user.UserId, user.LockedUntil, ip);
        }
        else
        {
            await _audit.LogAsync(
                AuthEventKind.LoginFailure, user.UserId, ip, userAgent,
                new { reason = "wrong_password", count = user.FailedLoginCount }, ct);
        }
        user.UpdatedAt = DateTime.UtcNow;
        await _users.SaveChangesAsync(ct);
    }

    private static UnauthorizedObjectResult Unauthorized() =>
        new(new { error = "invalid_credentials" });

    private static ObjectResult LockoutResponse(DateTime lockedUntil)
    {
        var retrySeconds = Math.Max(1, (int)(lockedUntil - DateTime.UtcNow).TotalSeconds);
        var result = new ObjectResult(new
        {
            error = "invalid_credentials", // same wire error as bad password
            retry_after_seconds = retrySeconds,
        })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
        };
        return result;
    }

    // 16 chars, mix of upper/lower/digit/symbol. Safe to surface once via
    // BootstrapResponse — the user is expected to rotate it immediately.
    private static string GenerateInitialPassword()
    {
        const string upper = "ABCDEFGHJKMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digit = "23456789";
        const string symbol = "!@#$%^&*-_=+";
        const string all = upper + lower + digit + symbol;

        var chars = new char[16];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digit[RandomNumberGenerator.GetInt32(digit.Length)];
        chars[3] = symbol[RandomNumberGenerator.GetInt32(symbol.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Shuffle so required-classes aren't always in the first 4 positions.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static ObjectResult StatusCode(int code) =>
        new ObjectResult(new { error = "server_error" }) { StatusCode = code };
}
