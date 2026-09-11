using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Tests;

// Chat history. Conversations can hold anything the user typed, so the
// ownership rule is the point of this controller: a non-admin sees and deletes
// only their own, and an id belonging to someone else is refused rather than
// silently served. Admins see every conversation for support purposes.
public class AiConversationsControllerOwnershipTests
{
    private static readonly Guid Me = new("22222222-2222-2222-2222-222222222222");

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public AiConversationsController Build(string role = "operator", Guid? userId = null)
            => new(Db, new FakeUser
            {
                UserId = userId ?? Me,
                Roles = new[] { role },
            })
            {
                ControllerContext = TestCtx.WithUser(userId ?? Me, role),
            };

        public Guid SeedConversation(
            Guid? ownerId = null, string messages = "[]", bool active = true,
            DateTime? updatedAt = null, string status = "active")
        {
            var id = Guid.NewGuid();
            Db.AIConversations.Add(new AIConversation
            {
                AIConversationId = id,
                UserId = (ownerId ?? Me).ToString(),
                Status = status,
                Messages = TestJson.Element(messages),
                IsActive = active,
                UpdatedAt = updatedAt ?? DateTime.UtcNow,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    // The endpoint answers with the standard {data, total, limit, offset}
    // envelope; most tests only care about the page.
    private static JsonElement ListEnvelope(ActionResult<ListResponse<object>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return TestJson.Element(JsonSerializer.Serialize(ok.Value));
    }

    private static List<JsonElement> ListBody(ActionResult<ListResponse<object>> result)
        => ListEnvelope(result).GetProperty("data").EnumerateArray().ToList();

    private static JsonElement GetBody(ActionResult<object> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return TestJson.Element(JsonSerializer.Serialize(ok.Value));
    }

    // ─── list: ownership scoping ────────────────────────────────────────

    // The default posture: you see your own history and nobody else's.
    [Fact]
    public async Task ANonAdminSeesOnlyTheirOwnConversations()
    {
        using var f = new Fixture();
        f.SeedConversation(ownerId: Me);
        f.SeedConversation(ownerId: Guid.NewGuid());

        var rows = ListBody(await f.Build(role: "operator").List(ct: default));

        Assert.Single(rows);
        Assert.Equal(Me.ToString(), rows[0].GetProperty("user_id").GetString());
    }

    // Admins see everything — support needs to read a user's thread.
    [Fact]
    public async Task AnAdminSeesEveryConversation()
    {
        using var f = new Fixture();
        f.SeedConversation(ownerId: Me);
        f.SeedConversation(ownerId: Guid.NewGuid());

        Assert.Equal(2, ListBody(await f.Build(role: "admin").List(ct: default)).Count);
    }

    [Fact]
    public async Task SoftDeletedConversationsAreExcluded()
    {
        using var f = new Fixture();
        f.SeedConversation(active: false);

        Assert.Empty(ListBody(await f.Build().List(ct: default)));
    }

    // Most-recently-touched first is what the sidebar shows.
    [Fact]
    public async Task ListIsOrderedByMostRecentlyUpdated()
    {
        using var f = new Fixture();
        f.SeedConversation(messages: """[{"role":"user","content":"older"}]""",
            updatedAt: DateTime.UtcNow.AddHours(-2));
        f.SeedConversation(messages: """[{"role":"user","content":"newer"}]""",
            updatedAt: DateTime.UtcNow);

        var rows = ListBody(await f.Build().List(ct: default));

        Assert.Equal("newer", rows[0].GetProperty("title").GetString());
    }

    // ─── list: pagination ───────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(9999)]
    public async Task TheLimitIsClamped(int limit)
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++) f.SeedConversation();

        var rows = ListBody(await f.Build().List(limit, 0, default));

        Assert.InRange(rows.Count, 1, 3);
    }

    [Fact]
    public async Task ANegativeOffsetIsFloored()
    {
        using var f = new Fixture();
        f.SeedConversation();

        Assert.Single(ListBody(await f.Build().List(50, -5, default)));
    }

    [Fact]
    public async Task TheOffsetPaginates()
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++)
            f.SeedConversation(updatedAt: DateTime.UtcNow.AddMinutes(-i));

        Assert.Single(ListBody(await f.Build().List(2, 2, default)));
    }

    // ─── list: the envelope ─────────────────────────────────────────────

    // `total` counts the whole history, not the page. Without it a client can
    // only report how many rows it asked for — which is exactly how the /ai
    // dashboard came to show its page size (5) as the conversation count.
    [Fact]
    public async Task TotalCountsTheWholeHistoryNotThePage()
    {
        using var f = new Fixture();
        for (var i = 0; i < 7; i++) f.SeedConversation();

        var envelope = ListEnvelope(await f.Build().List(5, 0, default));

        Assert.Equal(5, envelope.GetProperty("data").GetArrayLength());
        Assert.Equal(7, envelope.GetProperty("total").GetInt32());
    }

    // The count is scoped exactly like the rows are, so a user is never told
    // how much history other people have.
    [Fact]
    public async Task TotalIsScopedToTheCallerForANonAdmin()
    {
        using var f = new Fixture();
        f.SeedConversation(ownerId: Me);
        f.SeedConversation(ownerId: Guid.NewGuid());
        f.SeedConversation(ownerId: Guid.NewGuid());

        Assert.Equal(1, ListEnvelope(await f.Build(role: "operator").List(ct: default))
            .GetProperty("total").GetInt32());
        Assert.Equal(3, ListEnvelope(await f.Build(role: "admin").List(ct: default))
            .GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task TotalExcludesSoftDeletedConversations()
    {
        using var f = new Fixture();
        f.SeedConversation();
        f.SeedConversation(active: false);

        Assert.Equal(1, ListEnvelope(await f.Build().List(ct: default))
            .GetProperty("total").GetInt32());
    }

    // The echoed paging reflects what the server actually applied, so a client
    // that sent limit=9999 or offset=-5 can tell what it really got.
    [Fact]
    public async Task TheEnvelopeEchoesTheClampedPaging()
    {
        using var f = new Fixture();
        f.SeedConversation();

        var envelope = ListEnvelope(await f.Build().List(9999, -5, default));

        Assert.Equal(200, envelope.GetProperty("limit").GetInt32());
        Assert.Equal(0, envelope.GetProperty("offset").GetInt32());
    }

    // ─── list: derived fields ───────────────────────────────────────────

    // The title lets the sidebar render without replaying every thread.
    [Fact]
    public async Task TheTitleIsTheFirstUserMessage()
    {
        using var f = new Fixture();
        f.SeedConversation(messages: """
            [{"role":"system","content":"you are helpful"},
             {"role":"user","content":"list the edge routers"},
             {"role":"assistant","content":"here they are"}]
            """);

        var rows = ListBody(await f.Build().List(ct: default));

        Assert.Equal("list the edge routers", rows[0].GetProperty("title").GetString());
        Assert.Equal(3, rows[0].GetProperty("message_count").GetInt32());
    }

    [Fact]
    public async Task AnEmptyConversationGetsAPlaceholderTitle()
    {
        using var f = new Fixture();
        f.SeedConversation(messages: "[]");

        var rows = ListBody(await f.Build().List(ct: default));

        Assert.Equal("(new conversation)", rows[0].GetProperty("title").GetString());
        Assert.Equal(0, rows[0].GetProperty("message_count").GetInt32());
    }

    // ─── get ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOwnerCanReadTheirConversation()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(ownerId: Me, messages: """[{"role":"user","content":"hi"}]""");

        var body = GetBody(await f.Build(role: "operator").Get(id, default));

        Assert.Equal(id, body.GetProperty("conversation_id").GetGuid());
        Assert.Single(body.GetProperty("messages").EnumerateArray());
    }

    // The leak that matters: another user's thread must be refused.
    [Fact]
    public async Task ANonOwnerIsForbidden()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(ownerId: Guid.NewGuid());

        Assert.IsType<ForbidResult>((await f.Build(role: "operator").Get(id, default)).Result);
    }

    [Fact]
    public async Task AnAdminMayReadAnyConversation()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(ownerId: Guid.NewGuid());

        Assert.IsType<OkObjectResult>((await f.Build(role: "admin").Get(id, default)).Result);
    }

    [Fact]
    public async Task AnUnknownConversationIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundResult>((await f.Build().Get(Guid.NewGuid(), default)).Result);
    }

    [Fact]
    public async Task ASoftDeletedConversationIs404()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(active: false);

        Assert.IsType<NotFoundResult>((await f.Build().Get(id, default)).Result);
    }

    // ─── delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOwnerCanSoftDeleteTheirConversation()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(ownerId: Me);

        var result = await f.Build(role: "operator").Delete(id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.False((await f.Db.AIConversations.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task ANonOwnerCannotDelete()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(ownerId: Guid.NewGuid());

        Assert.IsType<ForbidResult>(await f.Build(role: "operator").Delete(id, default));
        Assert.True((await f.Db.AIConversations.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task AnAdminMayDeleteAnyConversation()
    {
        using var f = new Fixture();
        var id = f.SeedConversation(ownerId: Guid.NewGuid());

        Assert.IsType<NoContentResult>(await f.Build(role: "admin").Delete(id, default));
    }

    [Fact]
    public async Task DeletingAnUnknownConversationIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundResult>(await f.Build().Delete(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task DeleteIsRefusedTheSecondTime()
    {
        using var f = new Fixture();
        var id = f.SeedConversation();
        var controller = f.Build();
        await controller.Delete(id, default);

        Assert.IsType<NotFoundResult>(await controller.Delete(id, default));
    }

    // Clearing the whole sidebar is an admin action — a regular user must not
    // be able to wipe everyone's history.
    [Fact]
    public async Task BulkDeleteIsAdminOnly()
    {
        using var f = new Fixture();
        f.SeedConversation();

        Assert.IsType<ForbidResult>((await f.Build(role: "operator").DeleteAll(default)).Result);
        Assert.True((await f.Db.AIConversations.SingleAsync()).IsActive);
    }

    // NOTE: the admin path of DeleteAll uses ExecuteUpdateAsync, which the
    // InMemory provider does not implement — its behaviour belongs to the
    // integration suite. Only the authorisation gate is asserted here.

    // ─── DeriveTitle / CountMessages ────────────────────────────────────

    private static string DeriveTitle(string messages)
        => (string)typeof(AiConversationsController)
            .GetMethod("DeriveTitle", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { TestJson.Element(messages) })!;

    [Fact]
    public void Title_TakesTheFirstUserMessageNotTheSystemPrompt()
    {
        var title = DeriveTitle("""
            [{"role":"system","content":"you are a network assistant"},
             {"role":"user","content":"the real question"}]
            """);

        Assert.Equal("the real question", title);
    }

    // A long first message would blow out the sidebar layout.
    [Fact]
    public void Title_IsTruncatedWithAnEllipsis()
    {
        var title = DeriveTitle(
            "[{\"role\":\"user\",\"content\":\"" + new string('x', 200) + "\"}]");

        Assert.Equal(81, title.Length);   // 80 chars + the ellipsis
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void Title_ExactlyEightyCharsIsNotTruncated()
    {
        var text = new string('x', 80);

        Assert.Equal(text, DeriveTitle("[{\"role\":\"user\",\"content\":\"" + text + "\"}]"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"not":"an array"}""")]
    [InlineData("""[{"role":"assistant","content":"only assistant"}]""")]
    [InlineData("""[{"role":"user"}]""")]
    [InlineData("""[{"role":"user","content":123}]""")]
    [InlineData("""["not an object"]""")]
    public void Title_FallsBackWhenThereIsNoUsableUserMessage(string messages)
    {
        Assert.Equal("(new conversation)", DeriveTitle(messages));
    }

    private static int CountMessages(string messages)
        => (int)typeof(AiConversationsController)
            .GetMethod("CountMessages", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { TestJson.Element(messages) })!;

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("""[{"a":1},{"b":2}]""", 2)]
    [InlineData("null", 0)]
    [InlineData("""{"not":"an array"}""", 0)]
    public void Count_HandlesEveryShape(string messages, int expected)
    {
        Assert.Equal(expected, CountMessages(messages));
    }
}
