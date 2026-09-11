using System.Text.Json;
using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Common;

/// <summary>
/// Rewrites the editor marker that leaked into persisted workflows —
/// <c>snippet_id: "integration_action"</c> — to the seeded snippet's real id.
/// </summary>
/// <remarks>
/// <para>
/// The canvas represents an integration-action node by that literal string
/// while you are editing, and it used to be saved verbatim. The engine accepts
/// only a GUID or one of its own sentinels there, so those workflows saved
/// cleanly and then failed at run time with <c>invalid snippet_id</c>.
/// </para>
/// <para>
/// The canvas now writes the real id and heals the marker on load, so opening
/// and saving a workflow fixes it. This exists so nobody has to: an operator
/// with a dozen affected workflows should not have to open each one, and a
/// workflow nobody opens would stay broken until the next time it ran.
/// </para>
/// <para>
/// Idempotent and narrow: it only rewrites nodes whose <c>snippet_id</c> is
/// exactly the marker, and only when a seeded snippet of that type exists.
/// Everything else in the document is copied through untouched.
/// </para>
/// </remarks>
public static class LegacyNodeMarkerBackfill
{
    // The editor marker equals the seeded snippet's Type, which is what makes
    // it resolvable rather than merely wrong.
    private const string Marker = "integration_action";

    public static async Task RunAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var target = await db.Snippets
                .AsNoTracking()
                .Where(s => s.IsActive && s.Type == Marker)
                .Select(s => (Guid?)s.SnippetId)
                .FirstOrDefaultAsync();

            if (target is not Guid snippetId)
            {
                // No seeded snippet yet (first boot ordering, or a deployment
                // that removed it). Nothing to rewrite TO — the executor's
                // shim still resolves at run time if one appears later.
                return;
            }

            var workflows = 0;
            foreach (var wf in await db.Workflows.ToListAsync())
            {
                if (!TryRewrite(wf.Nodes, snippetId, out var rewritten)) continue;
                wf.Nodes = rewritten;
                wf.UpdatedAt = DateTime.UtcNow;
                workflows++;
            }

            // Versions matter too: a rollback restores this snapshot, and
            // restoring a broken one would reintroduce the failure.
            var versions = 0;
            foreach (var v in await db.WorkflowVersions.ToListAsync())
            {
                if (!TryRewrite(v.Nodes, snippetId, out var rewritten)) continue;
                v.Nodes = rewritten;
                versions++;
            }

            if (workflows + versions > 0)
            {
                await db.SaveChangesAsync();
                logger.LogInformation(
                    "workflow.legacy_marker_backfill.ok marker={Marker} snippet_id={SnippetId} "
                    + "workflows={Workflows} versions={Versions}",
                    Marker, snippetId, workflows, versions);
            }
        }
        catch (Exception ex)
        {
            // Never block boot. An unrewritten workflow still runs — the
            // executor resolves the marker at dispatch — it just cannot be
            // re-saved from the editor until someone opens it.
            logger.LogError(ex, "workflow.legacy_marker_backfill.failed");
        }
    }

    // Returns false when the document contains no marker, so an untouched
    // workflow is not marked dirty and does not get a new UpdatedAt.
    private static bool TryRewrite(JsonElement nodes, Guid snippetId, out JsonElement rewritten)
    {
        rewritten = default;
        if (nodes.ValueKind != JsonValueKind.Array) return false;

        var hit = nodes.EnumerateArray().Any(n =>
            n.ValueKind == JsonValueKind.Object
            && n.TryGetProperty("snippet_id", out var sid)
            && sid.ValueKind == JsonValueKind.String
            && sid.GetString() == Marker);
        if (!hit) return false;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    node.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("snippet_id")
                        && prop.Value.ValueKind == JsonValueKind.String
                        && prop.Value.GetString() == Marker)
                    {
                        writer.WriteString("snippet_id", snippetId.ToString());
                        continue;
                    }
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        rewritten = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        return true;
    }
}
