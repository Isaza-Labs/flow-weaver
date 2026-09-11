using System.Security.Claims;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The read-side admin controllers: the raw audit feed and the policy-block
// report. Both are pure query surfaces, so the filtering, ordering, clamping
// and grouping assertions here are the substance.
public class ReadControllerBatchTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    private static ControllerContext Context()
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, User.ToString()),
                }, "test")),
            },
        };

    // ─── AuditController ────────────────────────────────────────────────

    private sealed class AuditFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public AuditController Build() => new(Db) { ControllerContext = Context() };

        public AuditEvent Seed(
            string entityType = "workflow", string action = "create",
            Guid? userId = null, DateTime? at = null)
        {
            var row = new AuditEvent
            {
                AuditEventId = Guid.NewGuid(),
                UserId = userId ?? User,
                EntityType = entityType,
                EntityId = Guid.NewGuid(),
                Action = action,
                BeforeJson = TestJson.Element("null"),
                AfterJson = TestJson.Element("""{"x":1}"""),
                At = at ?? DateTime.UtcNow,
            };
            Db.AuditLogs.Add(row);
            Db.SaveChanges();
            return row;
        }

        public void Dispose() => Db.Dispose();
    }

    private static List<AuditEventResponse> Events(ActionResult<IEnumerable<AuditEventResponse>> result)
        => Assert.IsAssignableFrom<IEnumerable<AuditEventResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value).ToList();

    [Fact]
    public async Task Audit_ReturnsEventsNewestFirst()
    {
        using var f = new AuditFixture();
        var older = f.Seed(action: "older", at: DateTime.UtcNow.AddHours(-2));
        var newer = f.Seed(action: "newer", at: DateTime.UtcNow);

        var rows = Events(await f.Build().Events());

        Assert.Equal(new[] { newer.AuditEventId, older.AuditEventId },
            rows.Select(r => r.AuditEventId));
    }

    [Fact]
    public async Task Audit_FiltersByEntityType()
    {
        using var f = new AuditFixture();
        f.Seed(entityType: "workflow");
        f.Seed(entityType: "secret");

        var rows = Events(await f.Build().Events(entity_type: "secret"));

        Assert.Equal("secret", Assert.Single(rows).EntityType);
    }

    [Fact]
    public async Task Audit_FiltersByUser()
    {
        using var f = new AuditFixture();
        var other = Guid.NewGuid();
        f.Seed(userId: User);
        f.Seed(userId: other);

        var rows = Events(await f.Build().Events(user_id: other));

        Assert.Equal(other, Assert.Single(rows).UserId);
    }

    [Fact]
    public async Task Audit_FiltersByExactAction()
    {
        using var f = new AuditFixture();
        f.Seed(action: "create");
        f.Seed(action: "delete");

        var rows = Events(await f.Build().Events(action: "delete"));

        Assert.Equal("delete", Assert.Single(rows).Action);
    }

    [Fact]
    public async Task Audit_FiltersByTimeWindow()
    {
        using var f = new AuditFixture();
        f.Seed(action: "old", at: DateTime.UtcNow.AddDays(-10));
        f.Seed(action: "recent", at: DateTime.UtcNow);

        var rows = Events(await f.Build().Events(
            from: DateTime.UtcNow.AddDays(-1)));

        Assert.Equal("recent", Assert.Single(rows).Action);
    }

    [Fact]
    public async Task Audit_TheToBoundIsInclusiveOfEarlierRows()
    {
        using var f = new AuditFixture();
        f.Seed(action: "old", at: DateTime.UtcNow.AddDays(-10));
        f.Seed(action: "recent", at: DateTime.UtcNow);

        var rows = Events(await f.Build().Events(
            to: DateTime.UtcNow.AddDays(-1)));

        Assert.Equal("old", Assert.Single(rows).Action);
    }

    // The page size is clamped so a caller can't ask for the whole table.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9999, 3)]
    public async Task Audit_TheLimitIsClamped(int limit, int expected)
    {
        using var f = new AuditFixture();
        f.Seed();
        f.Seed();
        f.Seed();

        var rows = Events(await f.Build().Events(limit: limit));

        Assert.Equal(expected, rows.Count);
    }

    [Fact]
    public async Task Audit_ANegativeOffsetIsTreatedAsZero()
    {
        using var f = new AuditFixture();
        f.Seed();

        Assert.Single(Events(await f.Build().Events(limit: 50, offset: -3)));
    }

    [Fact]
    public async Task Audit_TheResponseCarriesTheStoredSnapshots()
    {
        using var f = new AuditFixture();
        f.Seed();

        var row = Assert.Single(Events(await f.Build().Events()));

        Assert.Equal(JsonValueKind.Null, row.BeforeJson.ValueKind);
        Assert.Equal(1, row.AfterJson.GetProperty("x").GetInt32());
    }


    // ─── PolicyController.Audit ─────────────────────────────────────────

    private sealed class StubPolicyService : IPolicy
    {
        public Task<ActionResult<ListResponse<PolicyResponse>>> GetAsync(int limit, int offset)
            => throw new NotSupportedException();
        public Task<ActionResult<PolicyResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<ActionResult<PolicyResponse>> PostAsync(CreatePolicy dto) => throw new NotSupportedException();
        public Task<ActionResult<PolicyResponse>> UpdateAsync(Guid id, UpdatePolicy dto) => throw new NotSupportedException();
        public Task<ActionResult<PolicyResponse>> DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class PolicyAuditFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public PolicyController Build()
            => new(new StubPolicyService(), Db, new FakeUser ())
            { ControllerContext = Context() };

        public void SeedBlock(
            string? policy = "default.qa_to_production", string? action = "promote",
            string? reason = "qa validation required",
            DateTime? at = null, string eventAction = "policy.blocked")
        {
            var metadata = new Dictionary<string, object?>();
            if (policy is not null) metadata["policy"] = policy;
            if (action is not null) metadata["action"] = action;
            if (reason is not null) metadata["reason"] = reason;

            Db.TraceEvents.Add(new TraceEvent
            {
                TraceEventId = Guid.NewGuid(),
                Action = eventAction,
                Category = "policy",
                Status = "blocked",
                Metadata = TestJson.Element(JsonSerializer.Serialize(metadata)),
                RequestId = "req-1",
                UserId = User,
                At = at ?? DateTime.UtcNow,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static PolicyController.PolicyAuditResponse PolicyBody(ActionResult<PolicyController.PolicyAuditResponse> result)
        => Assert.IsType<PolicyController.PolicyAuditResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task PolicyAudit_ReportsBlocksGroupedByPolicy()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock(policy: "gate-a");
        f.SeedBlock(policy: "gate-a");
        f.SeedBlock(policy: "gate-b");

        var body = PolicyBody(await f.Build().Audit());

        Assert.Equal(3, body.total);
        Assert.Equal(2, body.by_policy["gate-a"]);
        Assert.Equal(1, body.by_policy["gate-b"]);
        Assert.Equal(3, body.recent.Count);
    }

    [Fact]
    public async Task PolicyAudit_OnlyCountsPolicyBlockedEvents()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock();
        f.SeedBlock(eventAction: "workflow.run");

        Assert.Equal(1, PolicyBody(await f.Build().Audit()).total);
    }

    [Fact]
    public async Task PolicyAudit_HonoursTheDayWindow()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock(at: DateTime.UtcNow.AddDays(-30));
        f.SeedBlock(at: DateTime.UtcNow);

        Assert.Equal(1, PolicyBody(await f.Build().Audit(days: 7)).total);
        Assert.Equal(2, PolicyBody(await f.Build().Audit(days: 60)).total);
    }

    // The window is clamped so a caller can't ask for an unbounded scan.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task PolicyAudit_ANonPositiveWindowIsClampedToOneDay(int days)
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock(at: DateTime.UtcNow.AddDays(-2));

        Assert.Equal(0, PolicyBody(await f.Build().Audit(days)).total);
    }

    [Fact]
    public async Task PolicyAudit_AnOversizedWindowIsClampedTo90Days()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock(at: DateTime.UtcNow.AddDays(-120));

        Assert.Equal(0, PolicyBody(await f.Build().Audit(days: 9999)).total);
    }

    // A trace row whose metadata lost its policy name still counts toward the
    // total but can't be grouped — dropping it entirely would understate how
    // often policies fired.
    [Fact]
    public async Task PolicyAudit_ARowWithoutAPolicyNameIsCountedButNotGrouped()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock(policy: null);

        var body = PolicyBody(await f.Build().Audit());

        Assert.Equal(1, body.total);
        Assert.Empty(body.by_policy);
    }

    [Fact]
    public async Task PolicyAudit_TheEntryCarriesTheReasonAndRequestId()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock();

        var entry = Assert.Single(PolicyBody(await f.Build().Audit()).recent);

        Assert.Equal("promote", entry.action);
        Assert.Equal("qa validation required", entry.reason);
        Assert.Equal("req-1", entry.request_id);
        Assert.Equal(User, entry.user_id);
    }

    // Grouping is case-insensitive so a policy renamed only in casing doesn't
    // split into two buckets.
    [Fact]
    public async Task PolicyAudit_GroupingIsCaseInsensitive()
    {
        using var f = new PolicyAuditFixture();
        f.SeedBlock(policy: "Gate-A");
        f.SeedBlock(policy: "gate-a");

        var body = PolicyBody(await f.Build().Audit());

        Assert.Equal(2, Assert.Single(body.by_policy).Value);
    }
}


