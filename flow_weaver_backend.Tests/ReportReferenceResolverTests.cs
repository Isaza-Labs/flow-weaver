using System.IO.Compression;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Reports;
using flow_weaver_backend.Utils.Report;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// Covers the ${report:<id>} attachment reference: the REST executor expands
// it into base64 server-side so the agent can email a generated report (or
// several formats at once) by quoting the short id instead of copying the
// blob. Two layers: the resolver's substitution logic (fake service) and
// ReportService.LoadContentAsync's load (real InMemory db).
public class ReportReferenceResolverTests
{
    private static readonly FakeUser Caller = new();

    private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    private static ReportReferenceResolver Resolver(FakeReportService svc) =>
        new(svc, Caller, NullLogger<ReportReferenceResolver>.Instance);

    private static string Attachment(string filename, string reference) =>
        $$"""{"attachments":[{"filename":"{{filename}}","content_base64":"{{reference}}"}]}""";

    [Fact]
    public async Task Resolves_single_marker_to_base64()
    {
        var id = Guid.NewGuid();
        var svc = new FakeReportService { [(id)] = "hello-pdf"u8.ToArray() };
        var body = Attachment("r.pdf", "${report:" + id + "}");

        var result = await Resolver(svc).SubstituteAsync(body, CancellationToken.None);

        Assert.DoesNotContain("${report:", result);
        Assert.Contains(B64("hello-pdf"), result);
    }

    [Fact]
    public async Task Resolves_multiple_markers_for_several_formats()
    {
        var pdf = Guid.NewGuid();
        var csv = Guid.NewGuid();
        var svc = new FakeReportService
        {
            [(pdf)] = "pdf-bytes"u8.ToArray(),
            [(csv)] = "csv-bytes"u8.ToArray(),
        };
        var body = $$"""
            {"attachments":[
              {"filename":"r.pdf","content_base64":"${report:{{pdf}}}"},
              {"filename":"r.csv","content_base64":"${report:{{csv}}}"}
            ]}
            """;

        var result = await Resolver(svc).SubstituteAsync(body, CancellationToken.None);

        Assert.DoesNotContain("${report:", result);
        Assert.Contains(B64("pdf-bytes"), result);
        Assert.Contains(B64("csv-bytes"), result);
    }

    [Fact]
    public async Task Deduplicates_repeated_marker_to_one_load()
    {
        var id = Guid.NewGuid();
        var svc = new FakeReportService { [(id)] = "once"u8.ToArray() };
        var body = $$"""{"a":"${report:{{id}}}","b":"${report:{{id}}}"}""";

        var result = await Resolver(svc).SubstituteAsync(body, CancellationToken.None);

        Assert.Equal(1, svc.LoadCalls);
        Assert.Equal(2, CountOccurrences(result, B64("once")));
    }

    [Fact]
    public async Task Throws_on_unknown_id()
    {
        var id = Guid.NewGuid();
        var svc = new FakeReportService(); // empty store
        var body = Attachment("r.pdf", "${report:" + id + "}");

        var ex = await Assert.ThrowsAsync<ReportReferenceException>(
            () => Resolver(svc).SubstituteAsync(body, CancellationToken.None));

        Assert.Contains(id.ToString(), ex.UnresolvedIds);
    }

    [Fact]
    public async Task Throws_on_malformed_id_without_touching_store()
    {
        var svc = new FakeReportService();
        var body = Attachment("r.pdf", "${report:not-a-guid}");

        var ex = await Assert.ThrowsAsync<ReportReferenceException>(
            () => Resolver(svc).SubstituteAsync(body, CancellationToken.None));

        Assert.Contains("not-a-guid", ex.UnresolvedIds);
        Assert.Equal(0, svc.LoadCalls); // never queried — failed guid parse
    }

    [Fact]
    public async Task Throws_listing_every_unresolved_reference()
    {
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var svc = new FakeReportService { [(good)] = "ok"u8.ToArray() };
        var body = $$"""{"a":"${report:{{good}}}","b":"${report:{{bad}}}"}""";

        var ex = await Assert.ThrowsAsync<ReportReferenceException>(
            () => Resolver(svc).SubstituteAsync(body, CancellationToken.None));

        Assert.Contains(bad.ToString(), ex.UnresolvedIds);
        Assert.DoesNotContain(good.ToString(), ex.UnresolvedIds);
    }

    [Fact]
    public async Task Passes_body_through_untouched_when_no_marker()
    {
        var svc = new FakeReportService();
        var body = """{"attachments":[{"filename":"r.pdf","content_base64":"JVBERi0xLjQK"}]}""";

        var result = await Resolver(svc).SubstituteAsync(body, CancellationToken.None);

        Assert.Equal(body, result);
        Assert.Equal(0, svc.LoadCalls);
    }

    [Fact]
    public async Task Empty_body_is_a_noop()
    {
        var svc = new FakeReportService();

        Assert.Equal("", await Resolver(svc).SubstituteAsync("", CancellationToken.None));
        Assert.Equal(0, svc.LoadCalls);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    [Fact]
    public async Task Forwards_caller_context_to_the_service()
    {
        // The owner-or-admin decision lives in ReportService; here we just
        // confirm the resolver hands it the real caller identity (a
        // non-admin viewer, not a blanket admin).
        var viewer = new FakeUser { UserId = Guid.NewGuid(), Roles = new[] { "viewer" } };
        var id = Guid.NewGuid();
        var svc = new FakeReportService { [(id)] = "x"u8.ToArray() };
        var resolver = new ReportReferenceResolver(svc, viewer, NullLogger<ReportReferenceResolver>.Instance);

        await resolver.SubstituteAsync(Attachment("r.pdf", "${report:" + id + "}"), CancellationToken.None);

        Assert.False(svc.LastIsAdmin);
        Assert.Equal(viewer.UserId, svc.LastUserId);
    }

    // In-memory IReportService: keys artifacts by id, so an unknown id misses
    // exactly like the real DB lookup, and records the last authorization
    // context it was handed.
    private sealed class FakeReportService : IReportService
    {
        private readonly Dictionary<Guid, byte[]> _store = new();
        public int LoadCalls { get; private set; }
        public bool LastIsAdmin { get; private set; }
        public Guid? LastUserId { get; private set; }

        public byte[] this[Guid key] { set => _store[key] = value; }

        public Task<byte[]?> LoadContentAsync(
            Guid reportArtifactId, Guid? requestingUserId, bool requesterIsAdmin, CancellationToken ct)
        {
            LoadCalls++;
            LastIsAdmin = requesterIsAdmin;
            LastUserId = requestingUserId;
            return Task.FromResult(_store.TryGetValue(reportArtifactId, out var b) ? b : null);
        }

        public byte[] Decompress(ReportArtifact artifact) => artifact.ContentBytes;

        public Task<ReportArtifact> GenerateAndPersistAsync(
            GenerateReportRequest request, string source, ReportGenerationContext context, CancellationToken ct)
            => throw new NotSupportedException();
    }
}

// ReportService.LoadContentAsync exercised against a real InMemory context:
// the active filter and the compress round-trip that back the
// resolver above. Unused ctor deps stay null! — LoadContentAsync only
// touches _db + Decompress.
public class ReportServiceLoadContentTests
{
    private static readonly FakeUser Caller = new();

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static ReportService Svc(AppDbContext db) =>
        new(db, Caller, Array.Empty<IReportExporter>(), null!, null!, null!, null!,
            Options.Create(new ReportOptions()), NullLogger<ReportService>.Instance);

    private static Guid Seed(AppDbContext db, byte[] content, bool compressed,
        bool active = true, Guid? userId = null)
    {
        var id = Guid.NewGuid();
        db.ReportArtifacts.Add(new ReportArtifact
        {
            ReportArtifactId = id,
            UserId = userId ?? Caller.UserId,
            Title = "t",
            Filename = "t.bin",
            ContentType = "application/octet-stream",
            Format = "pdf",
            ContentBytes = content,
            IsCompressed = compressed,
            IsActive = active,
        });
        db.SaveChanges();
        return id;
    }

    private static byte[] Gzip(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
            gz.Write(input, 0, input.Length);
        return ms.ToArray();
    }

    [Fact]
    public async Task Returns_bytes_uncompressed()
    {
        using var db = NewDb(nameof(Returns_bytes_uncompressed));
        var raw = "raw-xlsx"u8.ToArray();
        var id = Seed(db, raw, compressed: false);

        var bytes = await Svc(db).LoadContentAsync(
            id, Caller.UserId, requesterIsAdmin: true, CancellationToken.None);

        Assert.Equal(raw, bytes);
    }

    [Fact]
    public async Task Decompresses_stored_gzip()
    {
        using var db = NewDb(nameof(Decompresses_stored_gzip));
        var raw = "the original pdf bytes"u8.ToArray();
        var id = Seed(db, Gzip(raw), compressed: true);

        var bytes = await Svc(db).LoadContentAsync(
            id, Caller.UserId, requesterIsAdmin: true, CancellationToken.None);

        Assert.Equal(raw, bytes);
    }

    [Fact]
    public async Task Returns_null_for_inactive_artifact()
    {
        using var db = NewDb(nameof(Returns_null_for_inactive_artifact));
        var id = Seed(db, "x"u8.ToArray(), compressed: false, active: false);

        var bytes = await Svc(db).LoadContentAsync(
            id, Caller.UserId, requesterIsAdmin: true, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task Owner_reads_own_report_without_admin()
    {
        using var db = NewDb(nameof(Owner_reads_own_report_without_admin));
        var raw = "mine"u8.ToArray();
        var id = Seed(db, raw, compressed: false, userId: Caller.UserId);

        var bytes = await Svc(db).LoadContentAsync(
            id, Caller.UserId, requesterIsAdmin: false, CancellationToken.None);

        Assert.Equal(raw, bytes);
    }

    [Fact]
    public async Task Non_admin_cannot_read_another_users_report()
    {
        using var db = NewDb(nameof(Non_admin_cannot_read_another_users_report));
        var id = Seed(db, "theirs"u8.ToArray(), compressed: false, userId: Guid.NewGuid());

        var bytes = await Svc(db).LoadContentAsync(
            id, Caller.UserId, requesterIsAdmin: false, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task Admin_reads_report_owned_by_another_user()
    {
        using var db = NewDb(nameof(Admin_reads_report_owned_by_another_user));
        var raw = "theirs"u8.ToArray();
        var id = Seed(db, raw, compressed: false, userId: Guid.NewGuid());

        var bytes = await Svc(db).LoadContentAsync(
            id, Caller.UserId, requesterIsAdmin: true, CancellationToken.None);

        Assert.Equal(raw, bytes);
    }
}
