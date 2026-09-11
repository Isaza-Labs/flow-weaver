using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Themes;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Themes;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// User-authored colour themes. The interesting surface is visibility: any
// user may keep private themes, only an admin may publish one, and a private
// theme must not be reachable — or even detectable — by anyone else.
public class ThemeServiceTests
{
    private static readonly Guid Alice = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = new("22222222-2222-2222-2222-222222222222");

    private static ThemeService For(
        flow_weaver_backend.Data.Db.AppDbContext db, Guid userId, bool admin = false)
        => new(
            new ThemeRepository(db),
            new FakeUser { UserId = userId, Roles = admin ? new[] { "admin" } : new[] { "editor" } },
            new FakeAudit());

    private static JsonElement Colors(string json = """{"primary":"#5C69AB"}""")
        => TestJson.Element(json);

    private static CreateTheme New(string name, bool shared = false, string? colors = null) => new()
    {
        Name = name,
        Colors = colors is null ? Colors() : Colors(colors),
        IsShared = shared,
    };

    private static ThemeResponse Value(ActionResult<ThemeResponse> result)
        => result.Value ?? throw new Xunit.Sdk.XunitException("expected a theme in the response");

    private static List<ThemeResponse> Rows(ActionResult<ListResponse<ThemeResponse>> result)
        => ((ListResponse<ThemeResponse>)((OkObjectResult)result.Result!).Value!).Data;

    // ─── Creating ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnyUserCanCreateAPrivateTheme()
    {
        using var db = TestDb.NewContext();

        var created = Value(await For(db, Alice).CreateAsync(New("Midnight")));

        Assert.False(created.IsShared);
        Assert.Equal(Alice, created.OwnerUserId);
        Assert.True(created.CanEdit);
    }

    [Fact]
    public async Task PublishingIsAdminOnly()
    {
        using var db = TestDb.NewContext();

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => For(db, Alice).CreateAsync(New("Corporate", shared: true)));

        Assert.Equal("theme_share_forbidden", ex.Code);
    }

    [Fact]
    public async Task AnAdminCanPublish()
    {
        using var db = TestDb.NewContext();

        var created = Value(await For(db, Alice, admin: true).CreateAsync(New("Corporate", shared: true)));

        Assert.True(created.IsShared);
    }

    // Hex is what the ramp generator parses; anything it can't read would save
    // fine and then render as an unstyled theme.
    [Theory]
    [InlineData("""{"primary":"5c69ab"}""")]     // no #
    [InlineData("""{"primary":"#5c6"}""")]       // short form
    [InlineData("""{"primary":"#5c69abff"}""")]  // 8-digit
    [InlineData("""{"primary":"rebeccapurple"}""")]
    [InlineData("""{"primary":123}""")]
    public async Task ABadColourIsRejected(string colors)
    {
        using var db = TestDb.NewContext();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => For(db, Alice).CreateAsync(New("Bad", colors: colors)));

        Assert.Equal("colors_invalid", ex.Code);
    }

    // A typo'd palette would otherwise save cleanly and do nothing, which
    // reads as a broken editor rather than a rejected input.
    [Fact]
    public async Task AnUnknownPaletteIsRejected()
    {
        using var db = TestDb.NewContext();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => For(db, Alice).CreateAsync(New("Typo", colors: """{"primarry":"#5c69ab"}""")));

        Assert.Equal("colors_unknown_palette", ex.Code);
    }

    [Fact]
    public async Task ColoursAreNormalisedToLowercase()
    {
        using var db = TestDb.NewContext();

        var created = Value(await For(db, Alice).CreateAsync(New("Case")));

        Assert.Equal("#5c69ab", created.Colors.GetProperty("primary").GetString());
    }

    [Fact]
    public async Task TwoUsersMayEachHaveATheme_WithTheSameName()
    {
        using var db = TestDb.NewContext();
        await For(db, Alice).CreateAsync(New("Midnight"));

        var bobs = Value(await For(db, Bob).CreateAsync(New("Midnight")));

        Assert.Equal(Bob, bobs.OwnerUserId);
    }

    [Fact]
    public async Task OneUserMayNotReuseTheirOwnThemeName()
    {
        using var db = TestDb.NewContext();
        await For(db, Alice).CreateAsync(New("Midnight"));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => For(db, Alice).CreateAsync(New("midnight")));

        Assert.Equal("theme_name_duplicate", ex.Code);
    }

    // ─── Style settings ────────────────────────────────────────────────

    [Fact]
    public async Task StyleSettingsRoundTrip()
    {
        using var db = TestDb.NewContext();

        var created = Value(await For(db, Alice).CreateAsync(new CreateTheme
        {
            Name = "Styled",
            Colors = Colors(),
            Settings = TestJson.Element(
                """{"roundness":1.5,"ui_scale":1.05,"font_body":"serif","heading_weight":800}"""),
        }));

        Assert.Equal(1.5, created.Settings!.Value.GetProperty("roundness").GetDouble());
        Assert.Equal("serif", created.Settings!.Value.GetProperty("font_body").GetString());
        Assert.Equal(800, created.Settings!.Value.GetProperty("heading_weight").GetInt32());
    }

    [Fact]
    public async Task AThemeWithoutSettingsIsStillValid()
    {
        using var db = TestDb.NewContext();

        var created = Value(await For(db, Alice).CreateAsync(New("Plain")));

        Assert.Null(created.Settings);
    }

    // Same philosophy as an unknown palette: a typo'd key must fail loudly,
    // not save cleanly and do nothing.
    [Fact]
    public async Task AnUnknownSettingIsRejected()
    {
        using var db = TestDb.NewContext();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => For(db, Alice).CreateAsync(new CreateTheme
            {
                Name = "Typo",
                Colors = Colors(),
                Settings = TestJson.Element("""{"roundnes":1}"""),
            }));

        Assert.Equal("settings_unknown_key", ex.Code);
    }

    // The vocabularies and ranges are the safety boundary — a saved theme is
    // injected into every reader's page, so a value outside them is an error.
    [Theory]
    [InlineData("""{"roundness":3}""")]           // above range
    [InlineData("""{"ui_scale":0.5}""")]          // below range
    [InlineData("""{"ui_scale":"big"}""")]        // wrong type
    [InlineData("""{"font_body":"comic-sans"}""")]
    [InlineData("""{"font_heading":"wingdings"}""")]
    [InlineData("""{"font_mono":"papyrus"}""")]
    [InlineData("""{"heading_weight":1000}""")]
    public async Task AnOutOfRangeSettingIsRejected(string settings)
    {
        using var db = TestDb.NewContext();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => For(db, Alice).CreateAsync(new CreateTheme
            {
                Name = "Bad",
                Colors = Colors(),
                Settings = TestJson.Element(settings),
            }));

        Assert.Equal("settings_invalid", ex.Code);
    }

    // Like colours, settings replace wholesale — an explicit empty object is
    // how "back to the app defaults" is expressed.
    [Fact]
    public async Task AnEmptySettingsObjectClearsEveryOverride()
    {
        using var db = TestDb.NewContext();
        var created = Value(await For(db, Alice).CreateAsync(new CreateTheme
        {
            Name = "Styled",
            Colors = Colors(),
            Settings = TestJson.Element("""{"roundness":1.5}"""),
        }));

        var updated = Value(await For(db, Alice).UpdateAsync(created.ThemeId, new UpdateTheme
        {
            Settings = TestJson.Element("{}"),
        }));

        Assert.NotNull(updated.Settings);
        Assert.Equal(0, updated.Settings!.Value.EnumerateObject().Count());
    }

    // ─── Visibility ────────────────────────────────────────────────────

    [Fact]
    public async Task APrivateThemeIsInvisibleToEveryoneElse()
    {
        using var db = TestDb.NewContext();
        await For(db, Alice).CreateAsync(New("Alice's"));

        Assert.Empty(Rows(await For(db, Bob).ListAsync(50, 0)));
    }

    [Fact]
    public async Task ASharedThemeIsVisibleToEveryone()
    {
        using var db = TestDb.NewContext();
        await For(db, Alice, admin: true).CreateAsync(New("Corporate", shared: true));

        var row = Assert.Single(Rows(await For(db, Bob).ListAsync(50, 0)));
        Assert.Equal("Corporate", row.Name);
        // Bob sees it but must not be offered edit affordances for it.
        Assert.False(row.CanEdit);
    }

    // 404, not 403: a 403 would confirm that a theme with that id exists and
    // belongs to somebody.
    [Fact]
    public async Task FetchingSomeoneElsesPrivateThemeIsANotFound()
    {
        using var db = TestDb.NewContext();
        var alices = Value(await For(db, Alice).CreateAsync(New("Alice's")));

        await Assert.ThrowsAsync<NotFoundException>(() => For(db, Bob).GetAsync(alices.ThemeId));
    }

    // ─── Editing ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheAuthorCanEditTheirOwnTheme()
    {
        using var db = TestDb.NewContext();
        var created = Value(await For(db, Alice).CreateAsync(New("Midnight")));

        var updated = Value(await For(db, Alice).UpdateAsync(created.ThemeId, new UpdateTheme
        {
            Name = "Midnight ops",
            Colors = Colors("""{"primary":"#ff0000","surface":"#1c1f2b"}"""),
        }));

        Assert.Equal("Midnight ops", updated.Name);
        Assert.Equal("#ff0000", updated.Colors.GetProperty("primary").GetString());
        Assert.Equal("#1c1f2b", updated.Colors.GetProperty("surface").GetString());
    }

    // Colours replace wholesale rather than merging, so dropping a palette
    // back to the brand default is expressible.
    [Fact]
    public async Task UpdatingColoursReplacesTheWholeMap()
    {
        using var db = TestDb.NewContext();
        var created = Value(await For(db, Alice).CreateAsync(
            New("Two", colors: """{"primary":"#ff0000","surface":"#1c1f2b"}""")));

        var updated = Value(await For(db, Alice).UpdateAsync(created.ThemeId, new UpdateTheme
        {
            Colors = Colors("""{"primary":"#00ff00"}"""),
        }));

        Assert.False(updated.Colors.TryGetProperty("surface", out _));
    }

    [Fact]
    public async Task ANonAdminCannotPublishAnExistingTheme()
    {
        using var db = TestDb.NewContext();
        var created = Value(await For(db, Alice).CreateAsync(New("Midnight")));

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => For(db, Alice).UpdateAsync(created.ThemeId, new UpdateTheme { IsShared = true }));

        Assert.Equal("theme_share_forbidden", ex.Code);
    }

    // Unpublishing yanks a theme out from under everyone using it, so it is
    // gated exactly like publishing.
    [Fact]
    public async Task UnpublishingIsAlsoAdminOnly()
    {
        using var db = TestDb.NewContext();
        var shared = Value(await For(db, Alice, admin: true).CreateAsync(New("Corporate", shared: true)));

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => For(db, Alice).UpdateAsync(shared.ThemeId, new UpdateTheme { IsShared = false }));

        Assert.Equal("theme_share_forbidden", ex.Code);
    }

    [Fact]
    public async Task ANonAdminCannotEditSomeoneElsesSharedTheme()
    {
        using var db = TestDb.NewContext();
        var shared = Value(await For(db, Alice, admin: true).CreateAsync(New("Corporate", shared: true)));

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => For(db, Bob).UpdateAsync(shared.ThemeId, new UpdateTheme { Name = "Hijacked" }));

        Assert.Equal("theme_edit_forbidden", ex.Code);
    }

    [Fact]
    public async Task AnAdminCanEditAPublishedTheme()
    {
        using var db = TestDb.NewContext();
        var shared = Value(await For(db, Alice, admin: true).CreateAsync(New("Corporate", shared: true)));

        var updated = Value(await For(db, Bob, admin: true).UpdateAsync(
            shared.ThemeId, new UpdateTheme { Name = "Corporate v2" }));

        Assert.Equal("Corporate v2", updated.Name);
    }

    // ─── Deleting ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingRemovesItFromTheList()
    {
        using var db = TestDb.NewContext();
        var created = Value(await For(db, Alice).CreateAsync(New("Midnight")));

        await For(db, Alice).DeleteAsync(created.ThemeId);

        Assert.Empty(Rows(await For(db, Alice).ListAsync(50, 0)));
    }

    [Fact]
    public async Task ANonAdminCannotDeleteSomeoneElsesSharedTheme()
    {
        using var db = TestDb.NewContext();
        var shared = Value(await For(db, Alice, admin: true).CreateAsync(New("Corporate", shared: true)));

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => For(db, Bob).DeleteAsync(shared.ThemeId));

        Assert.Equal("theme_delete_forbidden", ex.Code);
    }

    // Soft delete, so the name is free again but the row survives for anyone
    // still holding the id in localStorage.
    [Fact]
    public async Task TheNameIsFreeAfterDeleting()
    {
        using var db = TestDb.NewContext();
        var created = Value(await For(db, Alice).CreateAsync(New("Midnight")));
        await For(db, Alice).DeleteAsync(created.ThemeId);

        var again = Value(await For(db, Alice).CreateAsync(New("Midnight")));

        Assert.NotEqual(created.ThemeId, again.ThemeId);
    }
}
