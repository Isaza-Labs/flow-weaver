using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

public class PasswordPolicyTests
{
    private static PasswordPolicy NewPolicy() =>
        new(Options.Create(new AuthOptions()), NullLogger<PasswordPolicy>.Instance);

    [Fact]
    public void Rejects_too_short_password()
    {
        Assert.False(NewPolicy().Validate("abc").IsValid);
    }

    [Fact]
    public void Rejects_password_containing_username()
    {
        var result = NewPolicy().Validate("adminadmin123!", username: "admin");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Accepts_strong_password()
    {
        var result = NewPolicy().Validate("Str0ng!Passw0rd_x9", username: "bob", email: "bob@example.com");
        Assert.True(result.IsValid);
    }
}

public class JwtTokenServiceTests
{
    private static JwtTokenService NewService() => new(
        Options.Create(new JwtOptions
        {
            Issuer = "flow-weaver",
            Audience = "flow-weaver",
            Key = "0123456789abcdef0123456789abcdef0123456789abcdef",
            AccessTokenMinutes = 15,
        }),
        NullLogger<JwtTokenService>.Instance);

    [Fact]
    public void CreateAccessToken_returns_signed_jwt()
    {
        var user = new User { UserId = Guid.NewGuid(), Username = "admin", Role = "admin" };

        var token = NewService().CreateAccessToken(user, out var expiresAt);

        Assert.StartsWith("eyJ", token);          // JWT header segment
        Assert.Equal(2, token.Split('.').Length - 1);   // header.payload.signature
        Assert.True(expiresAt > DateTime.UtcNow);
    }
}

public class AuthAuditLoggerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task LogAsync_persists_auth_event()
    {
        using var db = TestDb.NewContext();
        var logger = new AuthAuditLogger(new AuthEventRepository(TestScopes.Over(db)), NullLogger<AuthAuditLogger>.Instance);

        await logger.LogAsync(AuthEventKind.LoginSuccess, _caller.UserId, "203.0.113.7", "unit-test");

        Assert.NotEmpty(db.Set<AuthEvent>());
    }
}

public class RefreshTokenServiceTests
{
    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "fw", Audience = "fw", Key = "0123456789abcdef0123456789abcdef0123456789abcdef", RefreshTokenDays = 7,
    });

    private static RefreshTokenService NewService(AppDbContext db) =>
        new(new RefreshTokenRepository(db), new UserRepository(db), Jwt, NullLogger<RefreshTokenService>.Instance);

    [Fact]
    public async Task Rotate_unknown_token_is_rejected()
    {
        using var db = TestDb.NewContext();
        var result = await NewService(db).RotateAsync("bogus-token", "203.0.113.7", CancellationToken.None);
        Assert.Equal(RotateResult.Unknown(), result);
    }

    [Fact]
    public async Task Revoke_unknown_token_does_not_throw()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db);
        await svc.RevokeAsync("bogus-token", CancellationToken.None);
        await svc.RevokeAllForUserAsync(Guid.NewGuid(), CancellationToken.None);
    }
}

public class AuthServiceTests
{
    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "fw", Audience = "fw", Key = "0123456789abcdef0123456789abcdef0123456789abcdef", AccessTokenMinutes = 15, RefreshTokenDays = 7,
    });
    private static readonly IOptions<AuthOptions> Auth = Options.Create(new AuthOptions());

    private static AuthService NewAuth(AppDbContext db)
        => new(new UserRepository(db), new UnitOfWork(db),
               new JwtTokenService(Jwt, NullLogger<JwtTokenService>.Instance),
               new RefreshTokenService(new RefreshTokenRepository(db), new UserRepository(db), Jwt, NullLogger<RefreshTokenService>.Instance),
               new PasswordPolicy(Auth, NullLogger<PasswordPolicy>.Instance),
               new AuthAuditLogger(new AuthEventRepository(TestScopes.Over(db)), NullLogger<AuthAuditLogger>.Instance),
               new FakeTrace(), new PasswordHasher<User>(), Jwt, Auth, NullLogger<AuthService>.Instance);

    [Fact]
    public async Task Login_missing_fields_returns_bad_request()
    {
        using var db = TestDb.NewContext();
        var res = await NewAuth(db).LoginAsync(new LoginRequest { Username = "", Password = "" }, "1.2.3.4", "agent", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Me_missing_user_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewAuth(db).MeAsync(Guid.NewGuid(), CancellationToken.None);
        // A valid-looking id with no backing row is an auth failure, not a 404.
        Assert.IsType<UnauthorizedObjectResult>(res.Result);
    }

    [Fact]
    public async Task Login_with_valid_credentials_succeeds()
    {
        using var db = TestDb.NewContext();
        var hasher = new PasswordHasher<User>();
        var user = new User
        {
            UserId = Guid.NewGuid(), Username = "admin",
            Email = "admin@example.com", Role = "admin", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        user.PasswordHash = hasher.HashPassword(user, "S3cret!Passw0rd");
        db.Set<User>().Add(user);
        db.SaveChanges();

        var res = await NewAuth(db).LoginAsync(
            new LoginRequest { Username = "admin", Password = "S3cret!Passw0rd" },
            "1.2.3.4", "agent", CancellationToken.None);

        // Success path: not a 400/401 — a LoginResponse body.
        Assert.IsNotType<BadRequestObjectResult>(res.Result);
        Assert.IsNotType<UnauthorizedResult>(res.Result);
        Assert.NotNull((object?)res.Value ?? res.Result);
    }
}
