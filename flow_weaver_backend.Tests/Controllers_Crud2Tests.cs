using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Services.AIProvider;
using flow_weaver_backend.Services.Credential;
using flow_weaver_backend.Services.Job;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.PythonModules;
using flow_weaver_backend.Services.Snippet;
using flow_weaver_backend.Services.VendorCommand;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AIProviderModel = flow_weaver_backend.Models.AIProvider;
using CredentialModel = flow_weaver_backend.Models.Credential;
using PolicyModel = flow_weaver_backend.Models.Policy;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Tests;

public class CredentialControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new CredentialController(new CredentialService(new RepositoryBase<CredentialModel>(db), new FakeCrypto(), _caller, new FakeAudit(), NullLogger<CredentialService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateCredential { Name = "c", Type = "ssh_password", AuthMethod = "password", Username = "u", Password = "p" })).Result);
        var id = ((CredentialResponse)created.Value!).CredentialId;
        Assert.IsType<CredentialResponse>((await c.GetById(id)).Value);
        Assert.IsType<CredentialResponse>((await c.Delete(id)).Value);
    }
}

public class AIProviderControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        // providerFactory is only used by the live "test connection" endpoint, not the CRUD forwarders.
        var c = new AIProviderController(new AIProviderService(new RepositoryBase<AIProviderModel>(db), new FakeCrypto(), _caller, new FakeAudit(), NullLogger<AIProviderService>.Instance), null!, NullLogger<AIProviderController>.Instance);

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateAIProvider { Name = "p", Type = "openai", DefaultModel = "gpt-4o" })).Result);
        var id = ((AIProviderResponse)created.Value!).AIProviderId;
        Assert.IsType<AIProviderResponse>((await c.GetById(id)).Value);
        Assert.IsType<AIProviderResponse>((await c.Delete(id)).Value);
    }
}

public class SnippetControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new SnippetController(new SnippetService(new SnippetRepository(db), new StepRunRepository(db), _caller, new FakeAudit(), new FakeTrace(), NullLogger<SnippetService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateSnippet { Name = "s", Type = "rest_call", TargetMode = "per_device" })).Result);
        var id = ((SnippetResponse)created.Value!).SnippetId;
        Assert.IsType<SnippetResponse>((await c.GetById(id)).Value);
        Assert.IsType<SnippetResponse>((await c.Delete(id)).Value);
    }
}

public class VendorCommandControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new VendorCommandController(new VendorCommandService(new VendorCommandRepository(db), _caller, new FakeAudit(), new FakeTrace(), new FakeVendorCommandRegistry(), NullLogger<VendorCommandService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get(null)).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateVendorCommand { DeviceType = "cisco_ios", Value = "show version" })).Result);
        var id = ((VendorCommandResponse)created.Value!).VendorCommandId;
        Assert.IsType<VendorCommandResponse>((await c.GetById(id)).Value);
        Assert.IsType<VendorCommandResponse>((await c.Delete(id)).Value);
    }
}

public class JobControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new JobController(new JobService(new JobRepository(db), _caller, NullLogger<JobService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
        Assert.NotNull((object?)(await c.GetQueueStats()).Value ?? (await c.GetQueueStats()).Result);
    }
}

public class AllowedPythonModuleControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new AllowedPythonModuleController(new AllowedPythonModuleService(new AllowedPythonModuleRepository(db), _caller, new FakeAudit()));

        Assert.IsType<OkObjectResult>((await c.Get(50, 0)).Result);
        var created = await c.Create(new CreateAllowedPythonModule { ImportName = "json", Source = "stdlib" });
        Assert.NotNull((object?)created.Value ?? created.Result);
    }
}

public class PolicyControllerTests
{
    private readonly FakeUser _caller = new();

    private PolicyController NewController(Data.Db.AppDbContext db)
        => new(new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance), db, _caller);

    [Fact]
    public async Task Crud_endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = NewController(db);

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreatePolicy
        {
            Name = "no-prod",
            Rule = TestJson.Element("""{"action":"deny"}"""),
            Enabled = true,
        })).Result);
        var id = ((PolicyResponse)created.Value!).PolicyId;
        Assert.IsType<PolicyResponse>((await c.GetById(id)).Value);
        Assert.IsType<PolicyResponse>((await c.Delete(id)).Value);
    }
}

public class SchemaControllerTests
{
    [Fact]
    public void Schema_endpoints_return_content()
    {
        // Latest()/Versioned() write a response header, so they need an HttpContext.
        var c = new SchemaController(new FakeSchemaValidator())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        Assert.IsType<ContentResult>(c.Latest());
        Assert.IsType<ContentResult>(c.Versioned("v1"));
        Assert.IsType<NotFoundObjectResult>(c.Versioned("v2"));
    }
}
