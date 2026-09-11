using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// User administration. Three guardrails carry the weight: the password policy
// runs before anything is written, a role change is dual-written into the
// built-in grants (or the granular model drifts from the legacy role), and you
// cannot delete yourself — which is what stops an admin locking everyone out.
public class UsersControllerCrudTests
{
    private static readonly Guid Me = new("22222222-2222-2222-2222-222222222222");

    // Records the dual-write so the grant sync is observable.
    private sealed class RecordingGrantSync : flow_weaver_backend.Services.Permission.IBuiltinGrantSync
    {
        public List<(Guid UserId, string Role)> Synced { get; } = new();
        public Task EnsureGrantsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncUserAsync(Guid userId, string role, CancellationToken ct = default)
        {
            Synced.Add((userId, role));
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingGrantSync Grants { get; } = new();

        public UsersController Build(Guid? actingUserId = null)
            => new(Db, new PasswordHasher<User>(),
                new PasswordPolicy(Options.Create(new AuthOptions()), NullLogger<PasswordPolicy>.Instance),
                Grants, new FakeAudit())
            {
                ControllerContext = TestCtx.WithUser(actingUserId ?? Me),
            };

        public Guid SeedUser(
            string username = "alice", string role = "viewer",
            bool active = true, Guid? userId = null)
        {
            var id = userId ?? Guid.NewGuid();
            Db.Set<User>().Add(new User
            {
                UserId = id,
                Username = username,
                Email = $"{username}@example.com",
                Role = role,
                PasswordHash = "x",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static CreateUser Create(
        string username = "bob", string email = "bob@example.com",
        string password = "S3cret!Passw0rd", string? role = null)
        => new() { Username = username, Email = email, Password = password, Role = role };

    private static int StatusOf<T>(ActionResult<T> result)
        => Assert.IsType<ObjectResult>(result.Result).StatusCode!.Value;

    private static UserResponse Created(ActionResult<UserResponse> result)
        => Assert.IsType<UserResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);

    private static UserResponse Ok(ActionResult<UserResponse> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<UserResponse>(result.Value);
    }

    // The list endpoint wraps its payload in an OkObjectResult.
    private static List<UserResponse> ListOf(ActionResult<IEnumerable<UserResponse>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IEnumerable<UserResponse>>(ok.Value).ToList();
    }

    // ─── create: validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("", "bob@example.com")]
    [InlineData("   ", "bob@example.com")]
    [InlineData("bob", "")]
    [InlineData("bob", "   ")]
    public async Task MissingUsernameOrEmailIsRejected(string username, string email)
    {
        using var f = new Fixture();

        var result = await f.Build().Post(Create(username: username, email: email), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Empty(f.Db.Set<User>());
    }

    [Theory]
    [InlineData("superadmin")]
    [InlineData("root")]
    [InlineData("")]
    public async Task AnUnknownRoleIsRejected(string role)
    {
        using var f = new Fixture();

        var result = await f.Build().Post(Create(role: role), default);

        Assert.Equal(400, StatusOf(result));
    }

    // The policy runs before the row is written, so a weak password never
    // reaches the store even hashed.
    [Fact]
    public async Task AWeakPasswordIsRejectedBeforeAnyWrite()
    {
        using var f = new Fixture();

        var result = await f.Build().Post(Create(password: "short"), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Empty(f.Db.Set<User>());
        Assert.Empty(f.Grants.Synced);
    }

    // A username that contains the password (or vice versa) is what the policy
    // is really there to stop.
    [Fact]
    public async Task APasswordContainingTheUsernameIsRejected()
    {
        using var f = new Fixture();

        var result = await f.Build().Post(
            Create(username: "alicewonder", password: "alicewonder123!"), default);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task ADuplicateUsernameIsAConflict()
    {
        using var f = new Fixture();
        f.SeedUser("bob");

        var result = await f.Build().Post(Create(username: "bob"), default);

        Assert.Equal(409, StatusOf(result));
        Assert.Single(f.Db.Set<User>());
    }

    // A soft-deleted row still holds the name — the unique index spans it, so
    // reusing the name has to be an explicit reactivation, not a silent create.
    [Fact]
    public async Task ASoftDeletedUsernameStillBlocksTheName()
    {
        using var f = new Fixture();
        f.SeedUser("bob", active: false);

        Assert.Equal(409, StatusOf(await f.Build().Post(Create(username: "bob"), default)));
    }

    // ─── create: success ────────────────────────────────────────────────

    [Fact]
    public async Task CreatePersistsTheUserWithAHashedPassword()
    {
        using var f = new Fixture();

        var body = Created(await f.Build().Post(Create(), default));

        Assert.Equal("bob", body.Username);
        var row = await f.Db.Set<User>().SingleAsync();
        Assert.True(row.IsActive);
        Assert.NotEqual("S3cret!Passw0rd", row.PasswordHash);   // never stored raw
        Assert.NotEmpty(row.PasswordHash);
    }

    // The safest role is the default — a missing role must not mint an admin.
    [Fact]
    public async Task RoleDefaultsToViewer()
    {
        using var f = new Fixture();

        Assert.Equal("viewer", Created(await f.Build().Post(Create(role: null), default)).Role);
    }

    [Theory]
    [InlineData("Admin", "admin")]
    [InlineData("OPERATOR", "operator")]
    public async Task RoleIsNormalisedToLowercase(string input, string expected)
    {
        using var f = new Fixture();

        Assert.Equal(expected, Created(await f.Build().Post(Create(role: input), default)).Role);
    }

    // The dual-write keeps the granular model in step from creation — without
    // it the new user has a legacy role but no matching grants.
    [Fact]
    public async Task CreateDualWritesTheBuiltinGrant()
    {
        using var f = new Fixture();

        var body = Created(await f.Build().Post(Create(role: "operator"), default));

        var (userId, role) = Assert.Single(f.Grants.Synced);
        Assert.Equal(body.UserId, userId);
        Assert.Equal("operator", role);
    }

    [Fact]
    public async Task ThePasswordStampIsSetOnCreation()
    {
        using var f = new Fixture();

        await f.Build().Post(Create(), default);

        Assert.True((await f.Db.Set<User>().SingleAsync()).PasswordChangedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    // ─── read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListReturnsUsersOrderedByName()
    {
        using var f = new Fixture();
        f.SeedUser("zoe");
        f.SeedUser("alice");

        var users = ListOf(await f.Build().Get(default));

        Assert.Equal(new[] { "alice", "zoe" }, users.Select(u => u.Username));
    }

    [Fact]
    public async Task ListExcludesSoftDeletedUsers()
    {
        using var f = new Fixture();
        f.SeedUser("gone", active: false);

        Assert.Empty(ListOf(await f.Build().Get(default)));
    }

    [Fact]
    public async Task GetByIdReturnsTheUser()
    {
        using var f = new Fixture();
        var id = f.SeedUser("alice");

        Assert.Equal("alice", Ok(await f.Build().GetById(id, default)).Username);
    }

    [Fact]
    public async Task GetByIdUnknownIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().GetById(Guid.NewGuid(), default)));
    }

    // ─── update ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAppliesSuppliedFieldsOnly()
    {
        using var f = new Fixture();
        var id = f.SeedUser("alice", role: "viewer");

        var body = Ok(await f.Build().Update(id, new UpdateUser { Email = "new@example.com" }, default));

        Assert.Equal("new@example.com", body.Email);
        Assert.Equal("viewer", body.Role);
        Assert.Equal("alice", body.Username);
    }

    [Fact]
    public async Task UpdateUnknownIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().Update(Guid.NewGuid(), new UpdateUser(), default)));
    }

    [Fact]
    public async Task UpdateWithAnUnknownRoleIsRejected()
    {
        using var f = new Fixture();
        var id = f.SeedUser(role: "viewer");

        var result = await f.Build().Update(id, new UpdateUser { Role = "superadmin" }, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("viewer", (await f.Db.Set<User>().SingleAsync()).Role);
    }

    // A role change must reach the grants, or the granular model keeps the
    // stale privileges.
    [Fact]
    public async Task ARoleChangeIsDualWritten()
    {
        using var f = new Fixture();
        var id = f.SeedUser(role: "viewer");

        await f.Build().Update(id, new UpdateUser { Role = "admin" }, default);

        var (userId, role) = Assert.Single(f.Grants.Synced);
        Assert.Equal(id, userId);
        Assert.Equal("admin", role);
    }

    // Re-sending the same role is not a change — syncing again would be
    // pointless write amplification.
    [Fact]
    public async Task ReassigningTheSameRoleDoesNotResync()
    {
        using var f = new Fixture();
        var id = f.SeedUser(role: "operator");

        await f.Build().Update(id, new UpdateUser { Role = "operator" }, default);

        Assert.Empty(f.Grants.Synced);
    }

    [Fact]
    public async Task AnEmailOnlyUpdateDoesNotResync()
    {
        using var f = new Fixture();
        var id = f.SeedUser();

        await f.Build().Update(id, new UpdateUser { Email = "x@example.com" }, default);

        Assert.Empty(f.Grants.Synced);
    }

    // Deactivating through update is allowed (it is not a self-delete guard).
    [Fact]
    public async Task UpdateCanDeactivateAUser()
    {
        using var f = new Fixture();
        var id = f.SeedUser();

        await f.Build().Update(id, new UpdateUser { IsActive = false }, default);

        Assert.False((await f.Db.Set<User>().SingleAsync()).IsActive);
    }

    // ─── delete ─────────────────────────────────────────────────────────

    // The lockout guard: deleting yourself could remove the last admin.
    [Fact]
    public async Task DeletingYourselfIsRefused()
    {
        using var f = new Fixture();
        f.SeedUser("me", userId: Me);

        var result = await f.Build(actingUserId: Me).Delete(Me, default);

        Assert.Equal(400, StatusOf(result));
        Assert.True((await f.Db.Set<User>().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task DeleteIsASoftDelete()
    {
        using var f = new Fixture();
        var id = f.SeedUser("alice");

        var body = Ok(await f.Build().Delete(id, default));

        Assert.Equal(id, body.UserId);
        Assert.False((await f.Db.Set<User>().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task DeleteUnknownIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().Delete(Guid.NewGuid(), default)));
    }

    [Fact]
    public async Task DeleteIsRefusedTheSecondTime()
    {
        using var f = new Fixture();
        var id = f.SeedUser();
        var controller = f.Build();
        await controller.Delete(id, default);

        Assert.Equal(404, StatusOf(await controller.Delete(id, default)));
    }

    // A response must never leak the hash.
    [Fact]
    public async Task ResponsesNeverCarryThePasswordHash()
    {
        using var f = new Fixture();
        var body = Created(await f.Build().Post(Create(), default));

        Assert.DoesNotContain("PasswordHash",
            System.Text.Json.JsonSerializer.Serialize(body), StringComparison.OrdinalIgnoreCase);
    }
}
