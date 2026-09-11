using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The slug is the identity a workflow bundle uses to say "the NetBox
// integration" to an instance that has never seen this instance's GUIDs.
// Without it, a shared workflow's dependencies all read as missing and the
// import wizard's only remedy is a placeholder python snippet — which is
// exactly how sharing a workflow between two instances went wrong.
//
// Two properties carry that weight and are pinned here: it is DERIVED from the
// name once, and it is UNIQUE.
public class SlugIdentityTests
{
    [Theory]
    [InlineData("NetBox", "netbox")]
    [InlineData("Net Box", "net-box")]
    [InlineData("  NetBox  ", "netbox")]
    [InlineData("NetBox (prod)", "netbox-prod")]
    [InlineData("ServiceNow ITSM", "servicenow-itsm")]
    [InlineData("a---b", "a-b")]
    [InlineData("Ping / Device", "ping-device")]
    [InlineData("v2.1_api", "v2-1-api")]
    public void Derives_an_ascii_slug_from_a_name(string name, string expected)
    {
        Assert.Equal(expected, Slug.From(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    // Non-Latin script yields nothing in an ASCII-only scheme. Returning null
    // rather than "" forces the caller to fall back explicitly instead of
    // storing an empty identity that would collide with every other empty one.
    [InlineData("网络")]
    public void Returns_null_when_a_name_yields_nothing_usable(string? name)
    {
        Assert.Null(Slug.From(name));
    }

    [Fact]
    public void Caps_the_length()
    {
        var slug = Slug.From(new string('a', 200));
        Assert.NotNull(slug);
        Assert.True(slug!.Length <= Slug.MaxLength, $"length was {slug.Length}");
    }

    [Fact]
    public void Never_leaves_a_trailing_or_leading_dash()
    {
        // The length cap truncates mid-name, which can land on a separator.
        var slug = Slug.From(new string('a', Slug.MaxLength - 1) + " tail");
        Assert.NotNull(slug);
        Assert.False(slug!.StartsWith('-') || slug.EndsWith('-'), slug);
    }

    [Fact]
    public void Unique_suffixes_a_collision_instead_of_reusing_it()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "netbox" };
        Assert.Equal("netbox-2", Slug.Unique("NetBox", taken));
    }

    [Fact]
    public void Unique_walks_past_several_collisions()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "netbox", "netbox-2", "netbox-3" };
        Assert.Equal("netbox-4", Slug.Unique("NetBox", taken));
    }

    [Fact]
    public void Unique_keeps_the_suffix_within_the_length_cap()
    {
        // A name at the cap must still be able to take a suffix, or two long
        // names differing only past the cap would collide forever.
        var basis = new string('a', Slug.MaxLength);
        var taken = new HashSet<string>(StringComparer.Ordinal) { basis };

        var slug = Slug.Unique(basis, taken);

        Assert.True(slug.Length <= Slug.MaxLength, $"length was {slug.Length}");
        Assert.DoesNotContain(slug, taken);
    }

    [Fact]
    public void Unique_still_produces_an_identity_for_an_unusable_name()
    {
        var slug = Slug.Unique("!!!", new HashSet<string>(StringComparer.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(slug));
    }

    // ── allocation on create ──────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_snippet_allocates_a_slug()
    {
        using var db = TestDb.NewContext();
        var svc = new flow_weaver_backend.Services.Snippet.SnippetService(
            new SnippetRepository(db), new StepRunRepository(db), new FakeUser(),
            new FakeAudit(), new FakeTrace(),
            NullLogger<flow_weaver_backend.Services.Snippet.SnippetService>.Instance);

        await svc.PostAsync(new CreateSnippet
        {
            Name = "Ping Device", Type = "ping", TargetMode = "per_device",
        });

        Assert.Equal("ping-device", db.Set<SnippetModel>().Single().Slug);
    }

    [Fact]
    public async Task Two_snippets_with_the_same_name_get_distinct_slugs()
    {
        // The unique index would reject the second row otherwise — and a bundle
        // referring to "ping-device" must resolve to exactly one snippet.
        using var db = TestDb.NewContext();
        var svc = new flow_weaver_backend.Services.Snippet.SnippetService(
            new SnippetRepository(db), new StepRunRepository(db), new FakeUser(),
            new FakeAudit(), new FakeTrace(),
            NullLogger<flow_weaver_backend.Services.Snippet.SnippetService>.Instance);

        await svc.PostAsync(new CreateSnippet { Name = "Ping", Type = "ping", TargetMode = "once" });
        await svc.PostAsync(new CreateSnippet { Name = "Ping", Type = "ping", TargetMode = "once" });

        var slugs = db.Set<SnippetModel>().Select(s => s.Slug).ToList();
        Assert.Equal(2, slugs.Distinct().Count());
        Assert.Contains("ping", slugs);
        Assert.Contains("ping-2", slugs);
    }

    // ── backfill ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_assigns_slugs_to_rows_that_predate_the_column()
    {
        using var db = TestDb.NewContext();
        db.Integrations.Add(Integration("NetBox", DateTime.UtcNow.AddDays(-2)));
        db.Snippets.Add(Snippet("Ping Device", DateTime.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();

        await SlugBackfillService.BackfillAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal("netbox", db.Integrations.Single().Slug);
        Assert.Equal("ping-device", db.Snippets.Single().Slug);
    }

    [Fact]
    public async Task Backfill_gives_the_oldest_duplicate_the_clean_slug()
    {
        // Whoever already shared a bundle referring to "netbox" meant the
        // original row, so that one keeps the unsuffixed identity.
        using var db = TestDb.NewContext();
        var old = Integration("NetBox", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = Integration("NetBox", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Integrations.AddRange(old, recent);
        await db.SaveChangesAsync();

        await SlugBackfillService.BackfillAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal("netbox", old.Slug);
        Assert.Equal("netbox-2", recent.Slug);
    }

    [Fact]
    public async Task Backfill_is_idempotent_and_never_rewrites_an_existing_slug()
    {
        // A second boot must not renumber anything — a slug that moved would
        // silently break every bundle already referring to it.
        using var db = TestDb.NewContext();
        db.Integrations.Add(Integration("NetBox", DateTime.UtcNow));
        await db.SaveChangesAsync();

        await SlugBackfillService.BackfillAsync(TestScopes.Over(db), NullLogger.Instance);
        var first = db.Integrations.Single().Slug;
        await SlugBackfillService.BackfillAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal(first, db.Integrations.Single().Slug);
    }

    [Fact]
    public async Task Backfill_counts_a_soft_deleted_row_as_holding_its_slug()
    {
        // The unique index spans soft-deleted rows, so reusing a deleted row's
        // slug would throw on save.
        using var db = TestDb.NewContext();
        var deleted = Integration("NetBox", DateTime.UtcNow.AddDays(-1));
        deleted.IsActive = false;
        deleted.Slug = "netbox";
        db.Integrations.Add(deleted);
        db.Integrations.Add(Integration("NetBox", DateTime.UtcNow));
        await db.SaveChangesAsync();

        await SlugBackfillService.BackfillAsync(TestScopes.Over(db), NullLogger.Instance);

        var slugs = db.Integrations.Select(i => i.Slug).ToList();
        Assert.Equal(2, slugs.Distinct().Count());
    }

    private static Integration Integration(string name, DateTime createdAt) => new()
    {
        IntegrationId = Guid.NewGuid(),
        Name = name,
        Type = "generic_rest",
        BaseURL = "https://example.test",
        IsActive = true,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static SnippetModel Snippet(string name, DateTime createdAt) => new()
    {
        SnippetId = Guid.NewGuid(),
        Name = name,
        Type = "ping",
        TargetMode = "per_device",
        IsActive = true,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };
}
