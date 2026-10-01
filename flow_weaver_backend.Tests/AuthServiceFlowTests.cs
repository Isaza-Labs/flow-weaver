using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// End-to-end coverage of the AuthService flows. The invariants pinned here are
// the ones an attacker probes: every failure mode must be indistinguishable on
// the wire (same `invalid_credentials` body), lockout must not leak whether the
// password was right, and a password change must kill every live session.
public class AuthServiceFlowTests
{
    private const string GoodPassword = "S3cret!Passw0rd";
    private const string Ip = "1.2.3.4";
    private const string Agent = "test-agent";

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "fw",
        Audience = "fw",
        Key = "0123456789abcdef0123456789abcdef0123456789abcdef",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7,
    });

    // Refresh-token double so the rotate branches (unknown / expired / replay)
    // can be driven directly instead of manufacturing DB state for each.
    private sealed class ScriptedRefreshTokens : IRefreshTokenService
    {
        private readonly RotateResult? _rotateResult;
        public List<Guid> RevokedAllFor { get; } = new();
        public List<string> Revoked { get; } = new();
        public int Created { get; private set; }

        public ScriptedRefreshTokens(RotateResult? rotateResult = null) => _rotateResult = rotateResult;

        public Task<(string rawToken, RefreshToken entity)> CreateAsync(User user, string ip, CancellationToken ct)
        {
            Created++;
            return Task.FromResult(("raw-refresh-token", new RefreshToken()));
        }

        public Task<RotateResult> RotateAsync(string rawToken, string ip, CancellationToken ct)
            => Task.FromResult(_rotateResult ?? RotateResult.Unknown());

        public Task RevokeAsync(string rawToken, CancellationToken ct)
        {
            Revoked.Add(rawToken);
            return Task.CompletedTask;
        }

        public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
        {
            RevokedAllFor.Add(userId);
            return Task.CompletedTask;
        }
    }

    private static AuthService NewAuth(
        AppDbContext db,
        IRefreshTokenService? refreshTokens = null,
        AuthOptions? authOptions = null)
    {
        var auth = Options.Create(authOptions ?? new AuthOptions());
        return new AuthService(
            new UserRepository(db), new UnitOfWork(db),
            new JwtTokenService(Jwt, NullLogger<JwtTokenService>.Instance),
            refreshTokens ?? new RefreshTokenService(
                new RefreshTokenRepository(db), new UserRepository(db), Jwt,
                NullLogger<RefreshTokenService>.Instance),
            new PasswordPolicy(auth, NullLogger<PasswordPolicy>.Instance),
            new AuthAuditLogger(new AuthEventRepository(TestScopes.Over(db)), NullLogger<AuthAuditLogger>.Instance),
            new FakeTrace(), new PasswordHasher<User>(), Jwt, auth,
            NullLogger<AuthService>.Instance);
    }

    private static User SeedUser(
        AppDbContext db,
        string username = "admin",
        string password = GoodPassword,
        bool active = true,
        int failedCount = 0,
        DateTime? lockedUntil = null)
    {
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            Role = "admin",
            IsActive = active,
            FailedLoginCount = failedCount,
            LockedUntil = lockedUntil,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        db.Set<User>().Add(user);
        db.SaveChanges();
        return user;
    }

    private static LoginRequest Login(string user = "admin", string pass = GoodPassword)
        => new() { Username = user, Password = pass };

    private static LoginResponse OkLogin(ActionResult<LoginResponse> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<LoginResponse>(ok.Value);
    }

    // ─── login: input validation ────────────────────────────────────────

    [Theory]
    [InlineData("", GoodPassword)]
    [InlineData("  ", GoodPassword)]
    [InlineData("admin", "")]
    [InlineData("admin", "   ")]
    public async Task Login_MissingFields_IsBadRequest(string username, string password)
    {
        using var db = TestDb.NewContext();

        var res = await NewAuth(db).LoginAsync(Login(username, password), Ip, Agent, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    // ─── login: account resolution ──────────────────────────────────────

    [Fact]
    public async Task Login_Succeeds()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);

        var res = await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        var body = OkLogin(res);
        Assert.Equal(user.UserId, body.UserId);
        Assert.Equal("admin", body.Username);
        Assert.Equal("admin", body.Role);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.True(body.ExpiresIn > 0);
    }

    [Fact]
    public async Task Login_UnknownUser_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);

        var res = await NewAuth(db).LoginAsync(Login(user: "ghost"), Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    [Fact]
    public async Task Login_DeactivatedUser_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        SeedUser(db, active: false);

        var res = await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    [Fact]
    public async Task Login_WrongPassword_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);

        var res = await NewAuth(db).LoginAsync(Login(pass: "WrongPassw0rd!"), Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    // Every failure must present the same body — otherwise the response itself
    // becomes a user-enumeration oracle.
    [Fact]
    public async Task Login_AllFailureModes_ShareTheSameWireError()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);
        var svc = NewAuth(db);

        var results = new[]
        {
            await svc.LoginAsync(Login(user: "ghost"), Ip, Agent, default),
            await svc.LoginAsync(Login(pass: "WrongPassw0rd!"), Ip, Agent, default),
        };

        foreach (var r in results)
        {
            var unauthorized = Assert.IsType<UnauthorizedObjectResult>(r.Result);
            Assert.Equal("""{ error = invalid_credentials }""", unauthorized.Value!.ToString());
        }
    }

    // ─── lockout ────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_FailedAttemptIncrementsCounter()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);

        await NewAuth(db).LoginAsync(Login(pass: "nope-nope-nope"), Ip, Agent, default);

        Assert.Equal(1, (await db.Set<User>().SingleAsync()).FailedLoginCount);
    }

    // Hitting the threshold sets LockedUntil and resets the counter.
    [Fact]
    public async Task Login_ThresholdReached_LocksTheAccount()
    {
        using var db = TestDb.NewContext();
        var options = new AuthOptions
        {
            // Fully qualified: Microsoft.AspNetCore.Identity has its own LockoutOptions.
            Lockout = new flow_weaver_backend.Services.Auth.LockoutOptions
            {
                MaxFailedAttempts = 3,
                LockoutMinutes = 15,
            },
        };
        SeedUser(db, failedCount: 2);

        await NewAuth(db, authOptions: options).LoginAsync(Login(pass: "nope-nope-nope"), Ip, Agent, default);

        var row = await db.Set<User>().SingleAsync();
        Assert.NotNull(row.LockedUntil);
        Assert.Equal(0, row.FailedLoginCount);
        Assert.True(row.LockedUntil > DateTime.UtcNow.AddMinutes(14));
    }

    // A locked account gets 429 + Retry-After, and — critically — the SAME
    // error body as a bad password, so lockout doesn't confirm the username.
    [Fact]
    public async Task Login_WhileLocked_Returns429WithoutRevealingPasswordValidity()
    {
        using var db = TestDb.NewContext();
        SeedUser(db, lockedUntil: DateTime.UtcNow.AddMinutes(10));

        var res = await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        var obj = Assert.IsType<ObjectResult>(res.Result);
        Assert.Equal(429, obj.StatusCode);
        Assert.Contains("invalid_credentials", obj.Value!.ToString());
        Assert.Contains("retry_after_seconds", obj.Value.ToString());
    }

    // The correct password is still refused while the lock is live.
    [Fact]
    public async Task Login_WhileLocked_RefusesEvenTheCorrectPassword()
    {
        using var db = TestDb.NewContext();
        SeedUser(db, lockedUntil: DateTime.UtcNow.AddMinutes(10));

        var res = await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        Assert.IsNotType<OkObjectResult>(res.Result);
    }

    [Fact]
    public async Task Login_ExpiredLock_AllowsLoginAgain()
    {
        using var db = TestDb.NewContext();
        SeedUser(db, lockedUntil: DateTime.UtcNow.AddMinutes(-1));

        var res = await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        Assert.IsType<OkObjectResult>(res.Result);
    }

    // A successful login clears the lockout bookkeeping.
    [Fact]
    public async Task Login_Success_ResetsFailureCounterAndLock()
    {
        using var db = TestDb.NewContext();
        SeedUser(db, failedCount: 3, lockedUntil: DateTime.UtcNow.AddMinutes(-1));

        await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        var row = await db.Set<User>().SingleAsync();
        Assert.Equal(0, row.FailedLoginCount);
        Assert.Null(row.LockedUntil);
    }

    // ─── login: audit trail ─────────────────────────────────────────────

    [Fact]
    public async Task Login_Success_WritesAnAuthEvent()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);

        await NewAuth(db).LoginAsync(Login(), Ip, Agent, default);

        Assert.NotEmpty(await db.Set<AuthEvent>().ToListAsync());
    }

    [Fact]
    public async Task Login_Failure_WritesAnAuthEvent()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);

        await NewAuth(db).LoginAsync(Login(pass: "nope-nope-nope"), Ip, Agent, default);

        Assert.NotEmpty(await db.Set<AuthEvent>().ToListAsync());
    }

    // ─── refresh ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_MissingToken_IsBadRequest(string token)
    {
        using var db = TestDb.NewContext();

        var res = await NewAuth(db).RefreshAsync(new RefreshRequest { RefreshToken = token }, Ip, Agent, default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Refresh_UnknownToken_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db, new ScriptedRefreshTokens(RotateResult.Unknown()));

        var res = await svc.RefreshAsync(new RefreshRequest { RefreshToken = "x" }, Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    [Fact]
    public async Task Refresh_ExpiredToken_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db, new ScriptedRefreshTokens(RotateResult.Expired()));

        var res = await svc.RefreshAsync(new RefreshRequest { RefreshToken = "x" }, Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    // Replay means the chain was already used: reject and record it as a
    // revocation event, not a plain login failure.
    [Fact]
    public async Task Refresh_ReplayDetected_IsUnauthorizedAndAudited()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db, new ScriptedRefreshTokens(RotateResult.ReplayDetected()));

        var res = await svc.RefreshAsync(new RefreshRequest { RefreshToken = "x" }, Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
        var evt = Assert.Single(await db.Set<AuthEvent>().ToListAsync());
        Assert.Contains("replay", evt.Metadata.ToString());
    }

    [Fact]
    public async Task Refresh_Ok_ReturnsNewTokenPair()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);
        var rotate = RotateResult.Ok("brand-new-refresh", new RefreshToken(), user);
        var svc = NewAuth(db, new ScriptedRefreshTokens(rotate));

        var res = await svc.RefreshAsync(new RefreshRequest { RefreshToken = "old" }, Ip, Agent, default);

        var body = OkLogin(res);
        Assert.Equal("brand-new-refresh", body.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.Equal(user.UserId, body.UserId);
        Assert.Equal(user.Role, body.Role);
    }

    // A real round-trip through RefreshTokenService: log in, then refresh with
    // the token that login handed out.
    [Fact]
    public async Task Refresh_RoundTripsAgainstTheRealTokenService()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);
        var svc = NewAuth(db);
        var login = OkLogin(await svc.LoginAsync(Login(), Ip, Agent, default));

        var res = await svc.RefreshAsync(
            new RefreshRequest { RefreshToken = login.RefreshToken }, Ip, Agent, default);

        var body = OkLogin(res);
        Assert.NotEqual(login.RefreshToken, body.RefreshToken);  // rotated
    }

    // Reusing a rotated token is a replay and must be refused.
    [Fact]
    public async Task Refresh_ReusingARotatedTokenIsRefused()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);
        var svc = NewAuth(db);
        var login = OkLogin(await svc.LoginAsync(Login(), Ip, Agent, default));
        await svc.RefreshAsync(new RefreshRequest { RefreshToken = login.RefreshToken }, Ip, Agent, default);

        var replay = await svc.RefreshAsync(
            new RefreshRequest { RefreshToken = login.RefreshToken }, Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(replay.Result);
    }

    // ─── logout ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_RevokesTheSuppliedTokenAndAudits()
    {
        using var db = TestDb.NewContext();
        var tokens = new ScriptedRefreshTokens();
        var svc = NewAuth(db, tokens);

        var res = await svc.LogoutAsync(
            new RefreshRequest { RefreshToken = "tok" }, Guid.NewGuid(), Ip, Agent, default);

        Assert.IsType<NoContentResult>(res);
        Assert.Equal("tok", Assert.Single(tokens.Revoked));
        Assert.Single(await db.Set<AuthEvent>().ToListAsync());
    }

    // Logging out without a token is still a valid logout (the access token
    // simply expires) — it must not 400.
    [Fact]
    public async Task Logout_WithoutToken_StillSucceeds()
    {
        using var db = TestDb.NewContext();
        var tokens = new ScriptedRefreshTokens();
        var svc = NewAuth(db, tokens);

        var res = await svc.LogoutAsync(
            new RefreshRequest { RefreshToken = "" }, Guid.NewGuid(), Ip, Agent, default);

        Assert.IsType<NoContentResult>(res);
        Assert.Empty(tokens.Revoked);
    }

    // ─── change password ────────────────────────────────────────────────

    [Theory]
    [InlineData("", "NewS3cret!Pass")]
    [InlineData("old", "")]
    public async Task ChangePassword_MissingFields_IsBadRequest(string current, string next)
    {
        using var db = TestDb.NewContext();

        var res = await NewAuth(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = current, NewPassword = next },
            Guid.NewGuid(), Ip, Agent, default);

        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task ChangePassword_UnknownUser_IsUnauthorized()
    {
        using var db = TestDb.NewContext();

        var res = await NewAuth(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = GoodPassword, NewPassword = "NewS3cret!Pass" },
            Guid.NewGuid(), Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);

        var res = await NewAuth(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = "not-it-at-all", NewPassword = "NewS3cret!Pass" },
            user.UserId, Ip, Agent, default);

        Assert.IsType<UnauthorizedObjectResult>(res);
    }

    // The policy applies to changes too, not just to bootstrap.
    [Fact]
    public async Task ChangePassword_WeakNewPassword_IsBadRequest()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);

        var res = await NewAuth(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = GoodPassword, NewPassword = "short" },
            user.UserId, Ip, Agent, default);

        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task ChangePassword_Success_UpdatesHashAndStamp()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);
        var originalHash = user.PasswordHash;

        var res = await NewAuth(db).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = GoodPassword, NewPassword = "An0ther!SecretPw" },
            user.UserId, Ip, Agent, default);

        Assert.IsType<NoContentResult>(res);
        var row = await db.Set<User>().SingleAsync();
        Assert.NotEqual(originalHash, row.PasswordHash);
        Assert.True(row.PasswordChangedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    // The new password must actually work afterwards, and the old must not.
    [Fact]
    public async Task ChangePassword_NewPasswordWorksAndOldDoesNot()
    {
        using var db = TestDb.NewContext();
        SeedUser(db);
        var svc = NewAuth(db);
        var user = await db.Set<User>().SingleAsync();
        await svc.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = GoodPassword, NewPassword = "An0ther!SecretPw" },
            user.UserId, Ip, Agent, default);

        Assert.IsType<OkObjectResult>(
            (await svc.LoginAsync(Login(pass: "An0ther!SecretPw"), Ip, Agent, default)).Result);
        Assert.IsType<UnauthorizedObjectResult>(
            (await svc.LoginAsync(Login(pass: GoodPassword), Ip, Agent, default)).Result);
    }

    // Rotating the credential must invalidate every live refresh chain,
    // otherwise a stolen session survives the password change.
    [Fact]
    public async Task ChangePassword_RevokesEveryRefreshTokenForTheUser()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);
        var tokens = new ScriptedRefreshTokens();

        await NewAuth(db, tokens).ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = GoodPassword, NewPassword = "An0ther!SecretPw" },
            user.UserId, Ip, Agent, default);

        Assert.Equal(user.UserId, Assert.Single(tokens.RevokedAllFor));
    }

    // ─── me ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Me_ReturnsTheIdentityWithoutSecrets()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db);

        var res = await NewAuth(db).MeAsync(user.UserId, default);

        var ok = Assert.IsType<OkObjectResult>(res.Result);
        var body = Assert.IsType<MeResponse>(ok.Value);
        Assert.Equal(user.UserId, body.UserId);
        Assert.Equal("admin", body.Username);
        Assert.Equal("admin@example.com", body.Email);
        Assert.Equal("admin", body.Role);
    }

    [Fact]
    public async Task Me_UnknownUser_IsUnauthorized()
    {
        using var db = TestDb.NewContext();

        var res = await NewAuth(db).MeAsync(Guid.NewGuid(), default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    [Fact]
    public async Task Me_DeactivatedUser_IsUnauthorized()
    {
        using var db = TestDb.NewContext();
        var user = SeedUser(db, active: false);

        var res = await NewAuth(db).MeAsync(user.UserId, default);

        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    // ─── bootstrap ──────────────────────────────────────────────────────

    private static BootstrapResponse OkBootstrap(ActionResult<BootstrapResponse> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<BootstrapResponse>(ok.Value);
    }

    [Fact]
    public async Task Bootstrap_CreatesTheAdmin()
    {
        using var db = TestDb.NewContext();

        var body = OkBootstrap(await NewAuth(db).BootstrapAsync(null, Ip, Agent, default));

        Assert.Equal("admin", body.Username);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));

        var admin = await db.Set<User>().SingleAsync();
        Assert.Equal(body.UserId, admin.UserId);
        Assert.Equal("admin", admin.Username);
        Assert.Equal("admin", admin.Role);
        Assert.True(admin.IsActive);
    }

    // Usernames are globally unique, so a second bootstrap must not collide
    // with the admin the first one created — it gets a numeric suffix.
    [Fact]
    public async Task Bootstrap_ASecondBootstrapGetsASuffixedUsername()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db);
        await svc.BootstrapAsync(null, Ip, Agent, default);

        var second = OkBootstrap(await svc.BootstrapAsync(null, Ip, Agent, default));
        var third = OkBootstrap(await svc.BootstrapAsync(null, Ip, Agent, default));

        Assert.Equal("admin-2", second.Username);
        Assert.Equal("admin-3", third.Username);
    }

    // The generated password is shown exactly once, so it must be strong
    // enough to survive until the operator rotates it.
    [Fact]
    public async Task Bootstrap_InitialPasswordIsStrongAndUnique()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db);

        var first = OkBootstrap(await svc.BootstrapAsync(null, Ip, Agent, default));
        var second = OkBootstrap(await svc.BootstrapAsync(null, Ip, Agent, default));

        Assert.Equal(16, first.InitialPassword.Length);
        Assert.NotEqual(first.InitialPassword, second.InitialPassword);
        Assert.Contains(first.InitialPassword, char.IsUpper);
        Assert.Contains(first.InitialPassword, char.IsLower);
        Assert.Contains(first.InitialPassword, char.IsDigit);
        Assert.Contains(first.InitialPassword, c => "!@#$%^&*-_=+".Contains(c));
    }

    // The deploy wizard supplies the operator's own credentials — they must
    // be honored verbatim (and be the ones that log in afterwards).
    [Fact]
    public async Task Bootstrap_HonorsRequestedCredentials()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db);

        var body = OkBootstrap(await svc.BootstrapAsync(
            new BootstrapRequest { Username = "example", Password = "S3cure-pass!" },
            Ip, Agent, default));

        Assert.Equal("example", body.Username);
        Assert.Equal("S3cure-pass!", body.InitialPassword);

        var login = await svc.LoginAsync(
            new LoginRequest { Username = "example", Password = "S3cure-pass!" },
            Ip, Agent, default);
        Assert.IsType<OkObjectResult>(login.Result);
    }

    // A supplied-but-weak password fails loudly instead of minting an
    // account the operator can't defend.
    [Fact]
    public async Task Bootstrap_RejectsAShortPassword()
    {
        using var db = TestDb.NewContext();

        var result = await NewAuth(db).BootstrapAsync(
            new BootstrapRequest { Password = "short" }, Ip, Agent, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(db.Set<User>());
    }

    // The password handed back must be the one that actually logs in.
    [Fact]
    public async Task Bootstrap_InitialPasswordCanLogIn()
    {
        using var db = TestDb.NewContext();
        var svc = NewAuth(db);
        var body = OkBootstrap(await svc.BootstrapAsync(null, Ip, Agent, default));

        var res = await svc.LoginAsync(
            new LoginRequest
            {
                Username = body.Username,
                Password = body.InitialPassword,
            }, Ip, Agent, default);

        Assert.IsType<OkObjectResult>(res.Result);
    }
}
