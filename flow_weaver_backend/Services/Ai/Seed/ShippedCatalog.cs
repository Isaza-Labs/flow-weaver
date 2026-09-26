using System.Security.Cryptography;
using System.Text;

namespace flow_weaver_backend.Services.Ai.Seed;

// What the boot sync may do to a catalog row (skill or spec) that shares its name with a
// shipped file under /Skills or /Specs.
//
// The sync used to overwrite every such row on every boot, so an admin's edit to a shipped
// skill lasted until the next restart. The rule now: a shipped update replaces a row only
// while the row still holds the shipped content it was given. Knowing that takes one fact
// the row did not carry — what the sync last wrote — which is ShippedContentHash.
internal static class ShippedCatalog
{
    public enum Decision
    {
        // Row already holds the current shipped content.
        Unchanged,

        // Row holds the current shipped content but has no hash yet: record it, touch nothing else.
        RecordHash,

        // Row is still a shipped version and the shipped file changed: take the new content.
        Update,

        // Row was edited, or was never the sync's to begin with: leave it.
        KeepEdited,
    }

    public static string Hash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static Decision Decide(string rowContent, string? rowShippedHash, Guid? rowCreatedBy, string diskContent)
    {
        // Already identical to the shipped file: at most the hash is missing or stale.
        if (rowContent == diskContent)
            return rowShippedHash == Hash(diskContent) ? Decision.Unchanged : Decision.RecordHash;

        if (rowShippedHash is null)
        {
            // A row somebody created (UI, integration catalog) is theirs even when a shipped
            // file later arrives with the same name.
            if (rowCreatedBy is not null) return Decision.KeepEdited;

            // Written by the sync before hashes existed. Whether it was edited since the last
            // boot cannot be known, so it gets the old behaviour once — the shipped content
            // wins — and from then on carries a hash. Nothing is lost that the previous boot
            // would not also have overwritten.
            return Decision.Update;
        }

        // Still exactly what the sync last wrote: a shipped version, safe to move forward.
        return Hash(rowContent) == rowShippedHash ? Decision.Update : Decision.KeepEdited;
    }
}
