using System.Text;
using Xunit.Abstractions;

namespace flow_weaver_backend.Tests.Conformance;

// FlowWeaver checked against the contract it is the oracle for.
//
// Being the oracle is the reason for this gate, not an exemption from it. Until this file
// existed, nothing in this repository could tell the difference between changing the product
// and changing the contract — a divergence here does not violate the specification, it
// silently redefines it, and Nashira goes red for a decision nobody made.
//
// Two rules make the gate mean something:
//
//   1. A vector whose family the adapter cannot answer FAILS. Only the vector may declare a
//      skip (`"not_implemented": true`). Silence is not a skip — an adapter that answered
//      nothing is how a family with nothing behind it reports green.
//   2. The REQUIRED set is explicit and narrow. A gate that blocked on families known to
//      diverge would be red on its first run and switched off within a week; a gate that
//      blocks on nothing is decoration. It starts at what is established and widens by
//      decision, never by drift.
public class ConformanceGateTests
{
    // The families that block a merge. Adding one is a contract decision: it says the two
    // products' behaviour in that area is established, not merely untested.
    //
    // `templates` and `gate` were promoted after going green and STAYING green across a
    // second run, which is the promotion rule: one green run says the adapter was written
    // today, two say the behaviour holds.
    //
    // `executor` is deliberately NOT here. It is 22 of 33 — real behaviour, not a stub, but
    // eleven vectors still fail for reasons named in this change's follow-ups. Promoting it
    // would either block every merge or force those eleven to be argued away, and both are
    // worse than a floor.
    private static readonly string[] Required = ["canonicalization", "gate", "schema", "templates"];

    // The pass count each family must not drop below, required or not.
    //
    // This is the half of the gate that protects a REPORTING family. "No new failures" is not
    // the same promise as "these many behaviours are still checked": an adapter that started
    // answering null for half its family would report zero failures and zero passes, and a
    // gate watching only failures would call that green. Nashira's gate has carried floors
    // from the start; this one was watching failures alone.
    //
    // Raising a floor is routine and expected — it is how progress is locked in. LOWERING one
    // is a contract decision that has to be argued in the change that lowers it.
    private static readonly Dictionary<string, int> Floors = new(StringComparer.Ordinal)
    {
        ["bundle"] = 14,            // 4 short: two contract decisions, one kit edit, one over-specified vector
        ["canonicalization"] = 2,
        ["compiler"] = 0,           // the kit defines no vectors
        ["executor"] = 32,          // 1 short: parallel entry points, which is a decision (see followups)
        ["gate"] = 4,
        ["schema"] = 4,
        ["snippets"] = 32,          // 19 short: 12 probe vectors unanswered, 7 normalisation divergences
        ["templates"] = 79,
    };

    // Every family the kit defines, so the summary can print a zero for one with no vectors.
    // An omitted family reads as green to anyone scanning the report, and that is exactly
    // where undetected drift accumulates: `compiler` has none, so nothing here would notice
    // the two engines serialising numbers differently.
    private static readonly string[] AllFamilies =
        ["bundle", "canonicalization", "compiler", "executor", "gate", "schema", "snippets", "templates"];

    private readonly ITestOutputHelper _out;

    public ConformanceGateTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void The_required_families_have_no_failures()
    {
        var kit = Path.Combine(AppContext.BaseDirectory, "conformance");
        var vectorsDir = Path.Combine(kit, "vectors");
        Assert.True(Directory.Exists(vectorsDir), $"conformance vectors missing at {vectorsDir}");

        var all = Directory.GetFiles(vectorsDir, "*.json", SearchOption.AllDirectories);
        var parked = all.Where(IsParked).ToList();
        var vectors = all.Where(f => !IsParked(f)).ToList();

        var report = new ConformanceRunner(new FlowWeaverAdapter(SharedSchemaValidator.Instance))
            .Run(vectors);

        var summary = Summary(report, parked, kit);
        _out.WriteLine(summary);
        WriteArtifact(summary);

        // Only the required set blocks. The rest ran, and their tallies are in the artifact —
        // which is where they have to be, because xUnit surfaces test output only for a
        // FAILING test, and a reporting family that diverges produces no failure at all.
        foreach (var family in Required)
        {
            Assert.True(
                report.FailedInFamily(family) == 0,
                $"required family '{family}' has {report.FailedInFamily(family)} failing vector(s).\n{summary}");
            Assert.True(
                report.PassedInFamily(family) > 0,
                $"required family '{family}' passed no vectors. A required family with nothing in it "
                + $"is a gate that cannot go red.\n{summary}");
        }

        foreach (var (family, floor) in Floors)
        {
            var passed = report.PassedInFamily(family);
            Assert.True(
                passed >= floor,
                $"family '{family}' passed {passed} vector(s), below its floor of {floor}. Either a "
                + "behaviour regressed or the adapter stopped answering; a family that answers "
                + $"nothing reports no failures, which is why the floor exists.\n{summary}");
        }
    }

    private static bool IsParked(string file) =>
        file.Replace('\\', '/').Contains("/_pending/", StringComparison.Ordinal);

    private static string Summary(ConformanceReport report, IReadOnlyList<string> parked, string kit)
    {
        var pinned = File.ReadAllLines(Path.Combine(kit, "PINNED"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Contains(':', StringComparison.Ordinal))
            .ToDictionary(l => l[..l.IndexOf(':')].Trim(), l => l[(l.IndexOf(':') + 1)..].Trim(),
                StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine($"CONFORMANCE workflow.v1 @ {pinned.GetValueOrDefault("contract_version", "?")} "
            + $"(oracle {pinned.GetValueOrDefault("oracle_commit", "?")})");

        foreach (var family in AllFamilies)
        {
            var required = Required.Contains(family, StringComparer.Ordinal);
            sb.AppendLine(
                $"  {family,-16} {report.PassedInFamily(family),3} pass  {report.FailedInFamily(family),3} fail  "
                + $"{report.SkippedInFamily(family),3} skip   {(required ? "REQUIRED" : "reporting")}");
        }

        sb.AppendLine($"  {"TOTAL",-16} {report.Passed,3} pass  {report.Failed,3} fail  {report.Skipped,3} skip");

        if (parked.Count > 0)
        {
            sb.AppendLine($"PARKED {parked.Count} vector(s), excluded from the gate — each is a decision "
                + "someone owes, not a passing check:");
            foreach (var f in parked.OrderBy(f => f, StringComparer.Ordinal))
                sb.AppendLine($"  {Path.GetFileName(f)}");
        }

        // Failures are grouped by reason, not listed one per vector. Fifty-one consecutive
        // lines saying the same thing about `snippets` is a report nobody reads, and a report
        // nobody reads is the same as no report — which is the failure this whole gate exists
        // to prevent, reproduced in its own output. One line per distinct reason, with the
        // count and one example id, keeps every reason visible and the file legible.
        var failures = report.Summary().Split('\n')
            .Where(l => l.TrimStart().StartsWith("FAIL ", StringComparison.Ordinal))
            .Select(l => l.TrimEnd())
            .ToList();

        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{failures.Count} failing vector(s), by reason:");
            foreach (var group in failures
                .GroupBy(Reason, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"  [{group.Count(),3}] {group.Key}");
                // Every id, not an example. Grouping is what makes the file readable; naming
                // only one member of a group would make it unactionable, and "which vectors"
                // is the first question anyone reading this has. An unwritten adapter is the
                // one case where the list is noise — the reason already names the family.
                if (group.Key.Contains("does not handle family", StringComparison.Ordinal)) continue;
                foreach (var id in group.Select(VectorId).OrderBy(i => i, StringComparer.Ordinal))
                    sb.AppendLine($"        {id}");
            }
        }

        return sb.ToString();
    }

    // "  FAIL <id>: <reason>" -> the two halves.
    private static string VectorId(string line)
    {
        var i = line.IndexOf("FAIL ", StringComparison.Ordinal) + "FAIL ".Length;
        var colon = line.IndexOf(':', i);
        return colon < 0 ? line[i..] : line[i..colon];
    }

    private static string Reason(string line)
    {
        var i = line.IndexOf("FAIL ", StringComparison.Ordinal) + "FAIL ".Length;
        var colon = line.IndexOf(':', i);
        var reason = colon < 0 ? line : line[(colon + 1)..].Trim();

        // Collapse whitespace FIRST. A vector's `expected` is pretty-printed in its file and
        // arrives here spanning several lines, so truncating before collapsing produced the
        // useless group key "expected {" for every value mismatch in the run — a report that
        // grouped correctly and said nothing.
        reason = System.Text.RegularExpressions.Regex.Replace(reason, @"\s+", " ").Trim();

        // A value mismatch names the values, so grouping on the whole text would give one
        // group per vector and defeat the point. The head of the reason is what groups; the
        // full diff belongs to the individual vector and is in the test output above.
        return reason.Length > 150 ? reason[..150] + "…" : reason;
    }

    // The reporting families are the whole reason this is written to a file. They produce no
    // test failure by design, so their divergences would otherwise be visible only to someone
    // who ran the suite locally and thought to look.
    private static void WriteArtifact(string summary)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "conformance-summary.txt"), summary);
        }
        catch (IOException)
        {
            // A read-only or unusual working directory must not fail the gate. The summary is
            // still in the test output above.
        }
    }
}
