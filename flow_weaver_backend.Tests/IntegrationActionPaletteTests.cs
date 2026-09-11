using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// The node editor's action palette. It joins actions to their parent
// integration so the picker can render `<integration> / <action>` in one
// round-trip.
//
// The filters are the substance: an action whose integration was disabled or
// soft-deleted must disappear from the palette, or a user picks something that
// can never dispatch — and that failure only surfaces at run time, on a device.
public class IntegrationActionPaletteTests
{

    // Only the palette endpoint is exercised here; the CRUD verbs delegate
    // straight to IIntegrationAction, which has its own tests.
    private sealed class UnusedActionService : IIntegrationAction
    {
        public Task<ActionResult<ListResponse<IntegrationActionResponse>>> GetAsync(int limit, int offset)
            => throw new NotSupportedException();
        public Task<ActionResult<IntegrationActionResponse>> GetByIdAsync(Guid id)
            => throw new NotSupportedException();
        public Task<ActionResult<IntegrationActionResponse>> PostAsync(CreateIntegrationAction dto)
            => throw new NotSupportedException();
        public Task<ActionResult<IntegrationActionResponse>> PostForIntegrationAsync(
            Guid integrationId, CreateIntegrationAction dto) => throw new NotSupportedException();
        public Task<ActionResult<IntegrationActionResponse>> UpdateAsync(Guid id, UpdateIntegrationAction dto)
            => throw new NotSupportedException();
        public Task<ActionResult<IntegrationActionResponse>> DeleteAsync(Guid id)
            => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public IntegrationActionController Build()
            => new(new UnusedActionService(), Db, new FakeUser ());

        public Guid SeedIntegration(
            string name = "Slack", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = name,
                Type = "generic_rest",
                BaseURL = "https://slack.test",
                AuthConfig = TestJson.Element("{}"),
                Headers = TestJson.Element("{}"),
                HealthCheck = TestJson.Element("{}"),
                Status = IntegrationStatus.Healthy,
                Enabled = true,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedAction(
            Guid integrationId, string name = "post_message", string category = "chat",
            bool enabled = true, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.IntegrationActions.Add(new IntegrationActionModel
            {
                IntegrationActionId = id,
                IntegrationId = integrationId,
                Name = name,
                Description = "sends a message",
                Method = "POST",
                Path = "/chat.postMessage",
                Category = category,
                Enabled = enabled,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static List<IntegrationActionPaletteItem> Palette(
        ActionResult<List<IntegrationActionPaletteItem>> result)
        => Assert.IsType<List<IntegrationActionPaletteItem>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task Palette_JoinsEachActionToItsParentIntegration()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration("Slack");
        var actionId = f.SeedAction(integrationId, "post_message");

        var items = Palette(await f.Build().ListAllForPalette());

        var item = Assert.Single(items);
        Assert.Equal(actionId, item.Id);
        Assert.Equal(integrationId, item.IntegrationId);
        Assert.Equal("Slack", item.IntegrationName);
        Assert.Equal("generic_rest", item.IntegrationType);
        Assert.Equal("post_message", item.Name);
        Assert.Equal("POST", item.Method);
        Assert.Equal("/chat.postMessage", item.Path);
        Assert.Equal("chat", item.Category);
    }

    [Fact]
    public async Task Palette_AnEmptyCatalogueYieldsAnEmptyPalette()
    {
        using var f = new Fixture();

        Assert.Empty(Palette(await f.Build().ListAllForPalette()));
    }

    // A disabled action is one the admin deliberately took out of service.
    [Fact]
    public async Task Palette_DisabledActionsAreHidden()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        f.SeedAction(integrationId, "live");
        f.SeedAction(integrationId, "retired", enabled: false);

        Assert.Equal("live", Assert.Single(Palette(await f.Build().ListAllForPalette())).Name);
    }

    [Fact]
    public async Task Palette_SoftDeletedActionsAreHidden()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        f.SeedAction(integrationId, "gone", active: false);

        Assert.Empty(Palette(await f.Build().ListAllForPalette()));
    }

    // An action whose parent integration was deleted can never dispatch, so
    // offering it in the picker would set the user up for a run-time failure.
    [Fact]
    public async Task Palette_ActionsOfADeletedIntegrationAreHidden()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(active: false);
        f.SeedAction(integrationId);

        Assert.Empty(Palette(await f.Build().ListAllForPalette()));
    }

    // The picker groups by integration then category, so the ordering is part
    // of the contract rather than incidental.
    [Fact]
    public async Task Palette_IsOrderedByIntegrationThenCategoryThenName()
    {
        using var f = new Fixture();
        var slack = f.SeedIntegration("Slack");
        var jira = f.SeedIntegration("Jira");
        f.SeedAction(slack, "post_message", category: "chat");
        f.SeedAction(jira, "create_issue", category: "issues");
        f.SeedAction(jira, "add_comment", category: "comments");

        var items = Palette(await f.Build().ListAllForPalette());

        Assert.Equal(
            new[] { "add_comment", "create_issue", "post_message" },
            items.Select(i => i.Name));
        Assert.Equal(new[] { "Jira", "Jira", "Slack" }, items.Select(i => i.IntegrationName));
    }
}
