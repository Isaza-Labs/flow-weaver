using System.Text;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Utils.Report;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Tests;

// The report download surface. Two audiences share these rows: an operator
// pulling a report they generated in chat, and an admin auditing everyone's.
// The owner-scoped endpoint is the one that must not leak — and reading a
// stored agent prompt is itself an auditable act.
public class ReportsControllerTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // Captures audit writes so "reading a prompt is auditable" is assertable.
    private sealed class RecordingAudit : flow_weaver_backend.Services.Audit.IAuditLogger
    {
        public List<(string EntityType, Guid? EntityId, string Action)> Entries { get; } = new();
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Entries.Add((entityType, entityId, action));
            return Task.CompletedTask;
        }
    }

    private sealed class StubReportService : IReportService
    {
        public byte[] Decompressed { get; set; } = Encoding.UTF8.GetBytes("payload");
        public Exception? ThrowOnGenerate { get; set; }
        public ReportArtifact? Generated { get; set; }
        public string? SourceSeen { get; private set; }
        public ReportGenerationContext? ContextSeen { get; private set; }

        public Task<ReportArtifact> GenerateAndPersistAsync(
            GenerateReportRequest request, string source, ReportGenerationContext context, CancellationToken ct)
        {
            SourceSeen = source;
            ContextSeen = context;
            if (ThrowOnGenerate is not null) throw ThrowOnGenerate;
            return Task.FromResult(Generated ?? new ReportArtifact
            {
                ReportArtifactId = Guid.NewGuid(),
                Title = "t",
                Filename = "t.html",
                ContentType = "text/html",
                Format = "html",
                ContentBytes = Decompressed,
                Sha256 = "x",
                Source = source,
            });
        }

        public byte[] Decompress(ReportArtifact artifact) => Decompressed;

        public Task<byte[]?> LoadContentAsync(
            Guid reportArtifactId, Guid? requestingUserId, bool requesterIsAdmin, CancellationToken ct)
            => Task.FromResult<byte[]?>(Decompressed);
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public StubReportService Reports { get; } = new();
        public RecordingAudit Audit { get; } = new();

        public ReportsController Build(string role = "admin", Guid? userId = null)
            => new(Db, new FakeUser { UserId = userId ?? User }, Reports, new FakeCrypto(), Audit)
            {
                ControllerContext = TestCtx.WithUser(userId ?? User, role),
            };

        public Guid SeedArtifact(
            Guid? userId = null, string title = "Device Audit", string format = "html",
            string source = "agent", bool active = true, byte[]? promptEncrypted = null, Guid? conversationId = null, Guid? workflowRunId = null,
            DateTime? createdAt = null)
        {
            var id = Guid.NewGuid();
            Db.ReportArtifacts.Add(new ReportArtifact
            {
                ReportArtifactId = id,
                UserId = userId,
                Title = title,
                Filename = $"{title}.{format}",
                ContentType = "text/html",
                Format = format,
                SizeBytes = 7,
                ContentBytes = Encoding.UTF8.GetBytes("payload"),
                Sha256 = "x",
                Source = source,
                AgentPromptEncrypted = promptEncrypted,
                AgentConversationId = conversationId,
                WorkflowRunId = workflowRunId,
                IsActive = active,
                CreatedAt = createdAt ?? DateTime.UtcNow,
            });
            Db.SaveChanges();
            return id;
        }

        public void SeedUser(Guid id, string username)
        {
            Db.Set<User>().Add(new User
            {
                UserId = id,
                Username = username,
                Email = $"{username}@x.com",
                Role = "operator",
                PasswordHash = "x",
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static GenerateReportRequest Request()
        => new() { Format = "html", Document = new ReportDocument { Title = "t" } };

    // ─── generate ───────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_ReturnsTheFileInline()
    {
        using var f = new Fixture();

        var result = await f.Build().Generate(Request(), null, null, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/html", file.ContentType);
        Assert.Equal("payload", Encoding.UTF8.GetString(file.FileContents));
    }

    // The source distinguishes an API-driven report from one a workflow step
    // produced — it's how the admin list separates them.
    [Fact]
    public async Task Generate_WithoutARunHeaderIsSourcedAsApi()
    {
        using var f = new Fixture();

        await f.Build().Generate(Request(), null, null, default);

        Assert.Equal("api", f.Reports.SourceSeen);
    }

    [Fact]
    public async Task Generate_WithARunHeaderIsSourcedAsWorkflow()
    {
        using var f = new Fixture();
        var runId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        await f.Build().Generate(Request(), runId, workflowId, default);

        Assert.Equal("workflow", f.Reports.SourceSeen);
        Assert.Equal(runId, f.Reports.ContextSeen!.WorkflowRunId);
        Assert.Equal(workflowId, f.Reports.ContextSeen.WorkflowId);
    }

    // A validation failure from the service becomes a 400 with the reason, not
    // a 500 — the caller can fix the document and retry.
    [Fact]
    public async Task Generate_InvalidRequestBecomesABadRequest()
    {
        using var f = new Fixture();
        f.Reports.ThrowOnGenerate = new InvalidOperationException("unsupported format 'docx'");

        var result = await f.Build().Generate(Request(), null, null, default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
    }

    // ─── admin list ─────────────────────────────────────────────────────

    private static (List<ReportArtifactSummary> Data, int Total) ListBody(ActionResult<object> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;
        var type = value.GetType();
        return (
            (List<ReportArtifactSummary>)type.GetProperty("data")!.GetValue(value)!,
            (int)type.GetProperty("total")!.GetValue(value)!);
    }

    [Fact]
    public async Task List_ReturnsArtifactsNewestFirst()
    {
        using var f = new Fixture();
        f.SeedArtifact(title: "older", createdAt: DateTime.UtcNow.AddHours(-2));
        f.SeedArtifact(title: "newer", createdAt: DateTime.UtcNow);

        var (data, total) = ListBody(await f.Build().List(
            null, null, null, null, null, null, null, null, ct: default));

        Assert.Equal(2, total);
        Assert.Equal("newer", data[0].Title);
        Assert.Equal("older", data[1].Title);
    }

    [Fact]
    public async Task List_ExcludesSoftDeleted()
    {
        using var f = new Fixture();
        f.SeedArtifact(active: false);

        var (_, total) = ListBody(await f.Build().List(
            null, null, null, null, null, null, null, null, ct: default));

        Assert.Equal(0, total);
    }

    // The username is joined live so a rename shows through instead of a stale
    // denormalised copy.
    [Fact]
    public async Task List_JoinsTheCurrentUsername()
    {
        using var f = new Fixture();
        var ownerId = Guid.NewGuid();
        f.SeedUser(ownerId, "alice");
        f.SeedArtifact(userId: ownerId);

        var (data, _) = ListBody(await f.Build().List(
            null, null, null, null, null, null, null, null, ct: default));

        Assert.Equal("alice", Assert.Single(data).Username);
    }

    // A deleted user must not make the row disappear from the audit list.
    [Fact]
    public async Task List_KeepsRowsWhoseUserIsGone()
    {
        using var f = new Fixture();
        f.SeedArtifact(userId: Guid.NewGuid());

        var (data, total) = ListBody(await f.Build().List(
            null, null, null, null, null, null, null, null, ct: default));

        Assert.Equal(1, total);
        Assert.Null(Assert.Single(data).Username);
    }

    [Fact]
    public async Task List_FiltersByUser()
    {
        using var f = new Fixture();
        var mine = Guid.NewGuid();
        f.SeedArtifact(userId: mine);
        f.SeedArtifact(userId: Guid.NewGuid());

        var (_, total) = ListBody(await f.Build().List(
            mine, null, null, null, null, null, null, null, ct: default));

        Assert.Equal(1, total);
    }

    [Fact]
    public async Task List_FiltersByFormatAndSource()
    {
        using var f = new Fixture();
        f.SeedArtifact(format: "html", source: "agent");
        f.SeedArtifact(format: "csv", source: "workflow");

        Assert.Equal(1, ListBody(await f.Build().List(
            null, "csv", null, null, null, null, null, null, ct: default)).Total);
        Assert.Equal(1, ListBody(await f.Build().List(
            null, null, "workflow", null, null, null, null, null, ct: default)).Total);
    }

    [Fact]
    public async Task List_FiltersByConversationAndRun()
    {
        using var f = new Fixture();
        var conversationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        f.SeedArtifact(conversationId: conversationId);
        f.SeedArtifact(workflowRunId: runId);
        f.SeedArtifact();

        Assert.Equal(1, ListBody(await f.Build().List(
            null, null, null, conversationId, null, null, null, null, ct: default)).Total);
        Assert.Equal(1, ListBody(await f.Build().List(
            null, null, null, null, runId, null, null, null, ct: default)).Total);
    }

    [Fact]
    public async Task List_FiltersByDateRange()
    {
        using var f = new Fixture();
        f.SeedArtifact(title: "old", createdAt: DateTime.UtcNow.AddDays(-10));
        f.SeedArtifact(title: "recent", createdAt: DateTime.UtcNow);

        var (data, _) = ListBody(await f.Build().List(
            null, null, null, null, null, null,
            from: DateTime.UtcNow.AddDays(-1), to: null, ct: default));

        Assert.Equal("recent", Assert.Single(data).Title);
    }

    // The page size is clamped so a caller cannot ask for the whole table.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(9999, 200)]
    [InlineData(25, 25)]
    public async Task List_LimitIsClamped(int requested, int expected)
    {
        using var f = new Fixture();

        var ok = Assert.IsType<OkObjectResult>((await f.Build().List(
            null, null, null, null, null, null, null, null, requested, 0, default)).Result);
        var limit = (int)ok.Value!.GetType().GetProperty("limit")!.GetValue(ok.Value)!;

        Assert.Equal(expected, limit);
    }

    [Fact]
    public async Task List_NegativeOffsetIsFloored()
    {
        using var f = new Fixture();

        var ok = Assert.IsType<OkObjectResult>((await f.Build().List(
            null, null, null, null, null, null, null, null, 50, -5, default)).Result);

        Assert.Equal(0, (int)ok.Value!.GetType().GetProperty("offset")!.GetValue(ok.Value)!);
    }

    [Fact]
    public async Task List_PaginatesWithTheOffset()
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++)
            f.SeedArtifact(title: $"r{i}", createdAt: DateTime.UtcNow.AddMinutes(-i));

        var (data, total) = ListBody(await f.Build().List(
            null, null, null, null, null, null, null, null, 2, 2, default));

        Assert.Equal(3, total);
        Assert.Single(data);
    }

    // ─── admin detail ───────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsTheArtifactWithItsContentBase64()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact();

        var ok = Assert.IsType<OkObjectResult>((await f.Build().Get(id, default)).Result);
        var body = Assert.IsType<ReportArtifactResponse>(ok.Value);

        Assert.Equal(id, body.ReportArtifactId);
        Assert.Equal("payload", Encoding.UTF8.GetString(Convert.FromBase64String(body.Base64)));
    }

    [Fact]
    public async Task Get_UnknownArtifactIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundResult>((await f.Build().Get(Guid.NewGuid(), default)).Result);
    }

    // Reading a stored agent prompt is itself auditable — someone else's
    // prompt may contain sensitive context.
    [Fact]
    public async Task Get_ReadingAStoredPromptIsAudited()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(promptEncrypted: Encoding.UTF8.GetBytes("the prompt"));

        var ok = Assert.IsType<OkObjectResult>((await f.Build().Get(id, default)).Result);
        var body = Assert.IsType<ReportArtifactResponse>(ok.Value);

        Assert.Equal("the prompt", body.AgentPrompt);
        Assert.Contains(f.Audit.Entries, e => e.Action == "read_prompt" && e.EntityId == id);
    }

    // ...but an artifact with no prompt must not produce a spurious audit row.
    [Fact]
    public async Task Get_WithoutAPromptWritesNoReadAudit()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(promptEncrypted: null);

        var ok = Assert.IsType<OkObjectResult>((await f.Build().Get(id, default)).Result);

        Assert.Null(Assert.IsType<ReportArtifactResponse>(ok.Value).AgentPrompt);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "read_prompt");
    }

    // ─── owner-scoped download ──────────────────────────────────────────

    [Fact]
    public async Task DownloadOwn_OwnerGetsTheFile()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(userId: User);

        var result = await f.Build(role: "operator").DownloadOwn(id, default);

        Assert.IsType<FileContentResult>(result);
    }

    // The leak that matters: another user's report must be refused.
    [Fact]
    public async Task DownloadOwn_NonOwnerIsForbidden()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(userId: Guid.NewGuid());

        var result = await f.Build(role: "operator").DownloadOwn(id, default);

        Assert.IsType<ForbidResult>(result);
    }

    // An admin can still pull anyone's through this endpoint.
    [Fact]
    public async Task DownloadOwn_AdminMayReachAnyArtifact()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(userId: Guid.NewGuid());

        Assert.IsType<FileContentResult>(await f.Build(role: "admin").DownloadOwn(id, default));
    }

    // An ownerless (workflow-generated) report is admin-only.
    [Fact]
    public async Task DownloadOwn_OwnerlessArtifactIsForbiddenForNonAdmins()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(userId: null);

        Assert.IsType<ForbidResult>(await f.Build(role: "operator").DownloadOwn(id, default));
    }

    [Fact]
    public async Task DownloadOwn_UnknownArtifactIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundResult>(await f.Build(role: "operator").DownloadOwn(Guid.NewGuid(), default));
    }

    // ─── admin download ─────────────────────────────────────────────────

    [Fact]
    public async Task Download_ReturnsTheFileAndAuditsIt()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact(userId: Guid.NewGuid());

        var result = await f.Build().Download(id, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("payload", Encoding.UTF8.GetString(file.FileContents));
        Assert.Contains(f.Audit.Entries, e => e.Action == "download" && e.EntityId == id);
    }

    [Fact]
    public async Task Download_UnknownArtifactIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundResult>(await f.Build().Download(Guid.NewGuid(), default));
    }

    // ─── delete ─────────────────────────────────────────────────────────

    // Soft delete keeps the audit trail intact.
    [Fact]
    public async Task Delete_IsASoftDeleteAndIsAudited()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact();

        var result = await f.Build().Delete(id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.False((await f.Db.ReportArtifacts.SingleAsync()).IsActive);
        Assert.Contains(f.Audit.Entries, e => e.Action == "delete" && e.EntityId == id);
    }

    [Fact]
    public async Task Delete_UnknownArtifactIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundResult>(await f.Build().Delete(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Delete_IsRefusedTheSecondTime()
    {
        using var f = new Fixture();
        var id = f.SeedArtifact();
        await f.Build().Delete(id, default);

        Assert.IsType<NotFoundResult>(await f.Build().Delete(id, default));
    }

}
