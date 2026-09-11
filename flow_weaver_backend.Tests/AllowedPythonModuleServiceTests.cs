using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.PythonModules;

namespace flow_weaver_backend.Tests;

public class AllowedPythonModuleServiceTests
{
    private readonly FakeUser _caller = new();

    private static AllowedPythonModuleService NewSvc(Data.Db.AppDbContext db, FakeUser user)
        => new(new AllowedPythonModuleRepository(db), user, new FakeAudit());

    [Fact]
    public async Task Create_stdlib_persists()
    {
        using var db = TestDb.NewContext();
        await NewSvc(db, _caller).CreateAsync(new CreateAllowedPythonModule { ImportName = "json", Source = "stdlib" });
        Assert.Single(db.Set<AllowedPythonModule>());
    }

    [Fact]
    public async Task Create_pip_persists()
    {
        using var db = TestDb.NewContext();
        await NewSvc(db, _caller).CreateAsync(new CreateAllowedPythonModule { ImportName = "requests", Source = "pip" });
        Assert.Single(db.Set<AllowedPythonModule>());
    }

    [Fact]
    public async Task Create_rejects_import_name_with_dots()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ValidationException>(() =>
            NewSvc(db, _caller).CreateAsync(new CreateAllowedPythonModule { ImportName = "os.path", Source = "stdlib" }));
    }

    [Fact]
    public async Task Create_rejects_unknown_source()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ValidationException>(() =>
            NewSvc(db, _caller).CreateAsync(new CreateAllowedPythonModule { ImportName = "requests", Source = "conda" }));
    }

    [Fact]
    public async Task Create_rejects_duplicate_import_name()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.CreateAsync(new CreateAllowedPythonModule { ImportName = "requests", Source = "pip" });
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(new CreateAllowedPythonModule { ImportName = "requests", Source = "pip" }));
    }

    [Fact]
    public async Task List_returns_rows()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.CreateAsync(new CreateAllowedPythonModule { ImportName = "json", Source = "stdlib" });
        await svc.CreateAsync(new CreateAllowedPythonModule { ImportName = "csv", Source = "stdlib" });

        var result = await svc.ListAsync(50, 0);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var page = Assert.IsType<ListResponse<AllowedPythonModuleResponse>>(ok.Value);
        Assert.Equal(2, page.Total);
    }
}
