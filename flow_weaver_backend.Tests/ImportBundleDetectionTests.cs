using System.Text.Json;
using flow_weaver_backend.Services.Import.Detectors;
using flow_weaver_backend.Services.Workflow;

namespace flow_weaver_backend.Tests;

// A bundle exported by either product, put through the import wizard, used to be
// claimed by FlowWeaverV1Detector at 0.85 — it has top-level nodes/edges and
// `snippet_id` on its first node, which is the whole of that detector's evidence.
// The translator then kept only nodes+edges, `dependencies` went with the rest,
// and three snippets whose types the file DECLARED (`ping`, `python_snippet`,
// `email_send`) came back inferred as `python_snippet`, `python_snippet`,
// `python_snippet`, offered for AI generation.
//
// These are the cheap half of that fix: the pipeline short-circuits bundles before
// detection, and the detector refuses them anyway. No DB.
public class ImportBundleDetectionTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    // The shape that fooled it: a bundle looks exactly like a v1 export from the
    // outside, because the bundle embeds a v1 graph.
    private const string Bundle = """
        {
          "schema_version": "v3",
          "kind": "flow_weaver.workflow_bundle",
          "exported_by": { "product": "nashira", "version": "1.0.0" },
          "requires": { "snippet_types": ["email_send", "ping", "python_snippet"] },
          "workflow": { "name": "ping-devices-email-report" },
          "nodes": [
            { "id": "start", "snippet_id": "__start__", "type": "task", "x": 0, "y": 0 },
            { "id": "ping_devices", "snippet_id": "7fe97360-0b6c-459d-a57e-1f107aee3907", "type": "task", "x": 220, "y": 0 }
          ],
          "edges": [{ "source": "start", "target": "ping_devices", "type": "success" }],
          "dependencies": { "snippets": [] }
        }
        """;

    [Theory]
    [InlineData("flow_weaver.workflow_bundle")]
    [InlineData("nashira.workflow_bundle")]
    [InlineData("netora.workflow_bundle")]
    public void FlowWeaverV1Detector_refuses_a_bundle(string kind)
    {
        var doc = Parse(Bundle.Replace("flow_weaver.workflow_bundle", kind));

        Assert.Equal(0.0, new FlowWeaverV1Detector().Detect(doc));
    }

    [Fact]
    public void FlowWeaverV1Detector_still_recognises_a_plain_v1_export()
    {
        // The guard must not cost the detector its actual job: the same document
        // without the bundle marker is a v1 export and still scores.
        var doc = Parse("""
            {
              "schema_version": "v1",
              "name": "plain",
              "nodes": [{ "id": "n1", "snippet_id": "ssh", "x": 0, "y": 0, "type": "task" }],
              "edges": []
            }
            """);

        Assert.True(new FlowWeaverV1Detector().Detect(doc) > 0.0);
    }

    [Fact]
    public void LooksLikeBundle_answers_the_same_without_a_declared_format()
    {
        // The wizard receives a raw upload and an optional `format_hint` that is
        // normally empty, so it cannot use the format-guarded overload. Both must
        // reach the same conclusion or there are two answers to one question —
        // which is how the wizard came to disagree with the import endpoint.
        Assert.True(WorkflowBundleReader.LooksLikeBundle(Bundle));
        Assert.True(WorkflowBundleReader.LooksLikeBundle(Bundle, "json"));
        Assert.True(WorkflowBundleReader.LooksLikeBundle(Parse(Bundle)));
    }

    [Fact]
    public void LooksLikeBundle_rejects_a_v1_export_and_junk()
    {
        Assert.False(WorkflowBundleReader.LooksLikeBundle("""{"schema_version":"v1","nodes":[],"edges":[]}"""));
        Assert.False(WorkflowBundleReader.LooksLikeBundle("not json at all"));
        Assert.False(WorkflowBundleReader.LooksLikeBundle("[]"));
        Assert.False(WorkflowBundleReader.LooksLikeBundle("""{"kind":"something.else"}"""));
    }

    [Fact]
    public void No_detector_claims_a_bundle()
    {
        // The pipeline short-circuits before this runs, so this asserts the
        // fallback: if a bundle ever reaches the chain, nothing translates it.
        var doc = Parse(Bundle);
        IDslDetector[] detectors =
        [
            new FlowWeaverV1Detector(),
            new N8nDetector(),
            new ItentialDetector(),
            new GenericDagDetector(),
        ];

        var claimed = detectors.Where(d => d.Detect(doc) > 0.0).Select(d => d.FormatName).ToList();

        Assert.True(claimed.Count == 0,
            "a bundle must reach no translator; claimed by: " + string.Join(", ", claimed));
    }
}
