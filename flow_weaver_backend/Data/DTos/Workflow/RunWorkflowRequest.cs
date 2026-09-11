using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class RunWorkflowRequest
{
    [JsonPropertyName("target_devices")]
    public List<Guid> TargetDevices { get; set; } = new();

    [JsonPropertyName("target_pools")]
    public List<Guid> TargetPools { get; set; } = new();

    [JsonPropertyName("input")]
    public JsonElement Input { get; set; }

    /// <summary>
    /// Whether the run stops at a failure no `failure` edge consumes. Omit for the default,
    /// which is to stop.
    /// </summary>
    /// <remarks>
    /// Passing `false` asks the run to keep walking past a failed step — every entry point
    /// still to run will run, and an `always` edge out of the failed node will fire. That is
    /// the behaviour every run had before 2026-08, and it is now something a caller has to
    /// ask for rather than something they get.
    ///
    /// It does not affect `failure` edges, which fire either way: a graph that says what to
    /// do when a node fails is not "carrying on regardless", it is handling the failure.
    /// </remarks>
    [JsonPropertyName("stop_on_failure")]
    public bool? StopOnFailure { get; set; }
}
