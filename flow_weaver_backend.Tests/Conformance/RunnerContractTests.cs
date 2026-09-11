namespace flow_weaver_backend.Tests.Conformance;

// The gate guarding the gate.
//
// Both halves of the runner's first rule are BEHAVIOUR, so both are tested: a family the
// adapter cannot answer must FAIL, and only a vector that declares itself unimplemented may
// be skipped. Without this, someone tightening or loosening the runner later has nothing
// telling them which of the two they just broke — and the failure mode is silent by
// construction, because a lost check reports success.
public class RunnerContractTests
{
    private static string Vector(string id, bool declaresNotImplemented) => $$"""
        {
          "id": "{{id}}",
          "family": "no_such_family",
          {{(declaresNotImplemented ? "\"not_implemented\": true," : "")}}
          "input": {},
          "expected": { "anything": true },
          "equivalence": "subset"
        }
        """;

    [Fact]
    public void An_unhandled_family_fails_and_only_a_declared_skip_is_skipped()
    {
        var dir = Directory.CreateTempSubdirectory("workflow-v1-conformance-gate");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "unhandled.json"), Vector("gate.probe.unhandled", false));
            File.WriteAllText(Path.Combine(dir.FullName, "declared.json"), Vector("gate.probe.declared_skip", true));

            var report = new ConformanceRunner(new FlowWeaverAdapter(SharedSchemaValidator.Instance))
                .Run(Directory.GetFiles(dir.FullName, "*.json"));

            Assert.Equal(1, report.Failed);
            Assert.Equal(1, report.Skipped);
            Assert.Contains("does not handle family", report.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // A vector whose expectation does not match must fail. Obvious, and worth asserting: the
    // whole gate rests on the comparison actually comparing, and a runner that returned "pass"
    // for everything would satisfy every other test in this directory.
    [Fact]
    public void A_wrong_answer_fails()
    {
        var dir = Directory.CreateTempSubdirectory("workflow-v1-conformance-wrong");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "wrong.json"), """
                {
                  "id": "gate.probe.wrong_answer",
                  "family": "canonicalization",
                  "input": { "variants": [ { "nodes": [], "edges": [] },
                                           { "nodes": [ { "id": "a", "snippet_id": "__start__" } ], "edges": [] } ] },
                  "expected": { "all_schema_hashes_equal": true },
                  "equivalence": "subset"
                }
                """);

            var report = new ConformanceRunner(new FlowWeaverAdapter(SharedSchemaValidator.Instance))
                .Run(Directory.GetFiles(dir.FullName, "*.json"));

            // Two different graphs do not hash alike, so the vector's claim is false and the
            // runner must say so.
            Assert.Equal(1, report.Failed);
            Assert.Equal(0, report.Passed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
