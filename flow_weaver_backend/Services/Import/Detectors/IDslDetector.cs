namespace flow_weaver_backend.Services.Import.Detectors;

// Inspects the parsed JSON of an uploaded document and reports how
// strongly it matches a specific DSL. The pipeline picks the detector
// with the highest confidence (ties broken by registration order) and
// hands the document to the matching IDslTranslator.
//
// Implementations should be CHEAP — they get called for every candidate
// format on every upload. Heuristics over keys / known constants, not
// full semantic parsing.
public interface IDslDetector
{
    // Stable identifier used in the AnalysisReport and to pair with a
    // translator. Examples: "flow_weaver_v1", "n8n", "itential",
    // "generic_dag".
    string FormatName { get; }

    // 0.0 = clearly not this format. 1.0 = unambiguous match. The
    // pipeline picks the highest score; FlowWeaverV1Detector is
    // expected to dominate the field when its native shape is fed in.
    double Detect(System.Text.Json.JsonElement document);
}
