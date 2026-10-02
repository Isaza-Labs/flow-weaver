using System.Security.Cryptography;
using System.Text;

namespace flow_weaver_backend.Tests.Conformance;

// The conformance kit in this repository is a COPY of an upstream kit, and a copy is only
// as trustworthy as the thing that notices it changed.
//
// `PINNED` records a digest over every vector and the schema, plus per-family counts. This
// asserts both, for two different failures:
//
//   - the DIGEST catches an edited or added file, including a vector quietly adjusted to make
//     a local build pass, which is the exact inversion the kit exists to prevent;
//   - the COUNTS catch a DELETED one, which a digest over "the files that are here" cannot
//     see at all — the remaining files still hash to a value, just not the recorded one, and
//     without counts the message would say "something changed" rather than "one is missing".
//
// What neither can catch is the upstream kit moving. See conformance/OWNERSHIP.md.
public class KitIntegrityTests
{
    private static string KitRoot => Path.Combine(AppContext.BaseDirectory, "conformance");

    private static Dictionary<string, string> Pinned()
    {
        var path = Path.Combine(KitRoot, "PINNED");
        Assert.True(File.Exists(path), $"conformance/PINNED missing at {path} — the kit was not copied to the test output.");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            map[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return map;
    }

    // Path AND content, sorted by path. The path is hashed because a vector renamed into
    // another family would otherwise leave the digest untouched while changing which family
    // its assertion counts toward.
    private static (string Digest, IReadOnlyList<string> Files) Digest()
    {
        var files = Directory
            .GetFiles(Path.Combine(KitRoot, "vectors"), "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(KitRoot, f).Replace('\\', '/'))
            .Append("schema/workflow.v1.schema.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        foreach (var rel in files)
        {
            var name = Encoding.UTF8.GetBytes(rel);
            sha.TransformBlock(name, 0, name.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            var content = File.ReadAllBytes(Path.Combine(KitRoot, rel));
            sha.TransformBlock(content, 0, content.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return (Convert.ToHexStringLower(sha.Hash!), files);
    }

    [Fact]
    public void The_vendored_kit_matches_its_recorded_digest()
    {
        var pinned = Pinned();
        var (digest, files) = Digest();

        Assert.True(
            pinned["digest"] == digest,
            $"""
            The vendored conformance kit does not match conformance/PINNED.

              recorded: {pinned["digest"]}
              actual:   {digest}
              files:    {files.Count}

            Something under conformance/ was edited, added or renamed. If that was deliberate —
            a contract change, or a re-sync from the shared kit — regenerate PINNED as part of the same
            change, and copy the change into the twin (conformance/OWNERSHIP.md). If it was not
            deliberate, this is the check working.
            """);
    }

    [Fact]
    public void Every_family_still_has_the_number_of_vectors_recorded()
    {
        var pinned = Pinned();
        var actual = Directory
            .GetFiles(Path.Combine(KitRoot, "vectors"), "*.json", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetFileName(Path.GetDirectoryName(f))!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var (key, value) in pinned)
        {
            if (!key.StartsWith("vectors.", StringComparison.Ordinal)) continue;
            var family = key["vectors.".Length..];
            if (family == "total") continue;

            var have = actual.GetValueOrDefault(family, 0);
            Assert.True(have == int.Parse(value),
                $"family '{family}': PINNED records {value} vectors, {have} are present. "
                + "A deleted vector is a check that stopped running, and the digest alone would "
                + "only say the kit changed.");
        }

        Assert.Equal(int.Parse(pinned["vectors.total"]), actual.Values.Sum());
    }

    // The kit is pinned to a commit of the oracle. That commit has to CONTAIN the behaviour the
    // kit describes — the pin said `fw@1a11ea3` for months while the v3 bundle work it reifies
    // was still uncommitted, so anyone checking it out to verify the contract would have found
    // none of it and concluded the specification was invented.
    [Fact]
    public void The_pin_names_a_contract_version_and_an_oracle_commit()
    {
        var pinned = Pinned();

        Assert.False(string.IsNullOrWhiteSpace(pinned.GetValueOrDefault("contract_version")));
        var commit = pinned.GetValueOrDefault("oracle_commit");
        Assert.False(string.IsNullOrWhiteSpace(commit), "PINNED must name the oracle commit the kit was reified from.");
        Assert.StartsWith("fw@", commit, StringComparison.Ordinal);
    }
}
