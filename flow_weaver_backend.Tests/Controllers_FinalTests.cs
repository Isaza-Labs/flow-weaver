using flow_weaver_backend.Controllers;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

public class AdminSettingsControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Get_returns_settings()
    {
        var c = new AdminSettingsController(new FakeAppSettings(), _caller, new FakeAudit(), NullLogger<AdminSettingsController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };
        var res = await c.Get(CancellationToken.None);
        Assert.NotNull((object?)res.Value ?? res.Result);
    }
}

public class AiCatalogControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_catalog()
    {
        var c = new AiCatalogController(new FakeSkillPromptLoader(), new FakeApiSpecIndex(), _caller)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

        Assert.NotNull((object?)(await c.GetSystemPrompt(CancellationToken.None)).Value ?? (await c.GetSystemPrompt(CancellationToken.None)).Result);
        Assert.Equal(404, HttpResult.Status(c.GetOperation("does-not-exist").Result));
        Assert.NotNull((object?)(await c.Reload(CancellationToken.None)).Value ?? (await c.Reload(CancellationToken.None)).Result);
    }
}

public class UsersControllerTests
{
    private UsersController NewController(Data.Db.AppDbContext db)
        => new(db, new PasswordHasher<User>(),
               new PasswordPolicy(Options.Create(new AuthOptions()), NullLogger<PasswordPolicy>.Instance),
               new NoOpBuiltinGrantSync(), new FakeAudit())
        {
            ControllerContext = TestCtx.WithUser(),
        };

    [Fact]
    public async Task Read_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var c = NewController(db);

        Assert.IsType<OkObjectResult>((await c.Get(CancellationToken.None)).Result);
        Assert.Equal(404, HttpResult.Status((await c.GetById(Guid.NewGuid(), CancellationToken.None)).Result));
    }
}

public class SecretsControllerTests
{
    private readonly FakeUser _caller = new();

    private SecretsController NewController(Data.Db.AppDbContext db)
        => new(db, _caller, new FakeCrypto(), new FakeAudit(), new FakeTrace())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task List_create_get_forward()
    {
        using var db = TestDb.NewContext();
        var c = NewController(db);

        Assert.IsType<OkObjectResult>((await c.List(CancellationToken.None)).Result);
        var created = await c.Create(new CreateSecretRequest { Name = "db-pw", Value = "s3cret" }, CancellationToken.None);
        Assert.NotNull((object?)created.Value ?? created.Result);
    }
}
