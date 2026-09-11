using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Detectors;

// Itential IAP / Operations Manager workflow shape:
//   {
//     "name": "...",
//     "tasks": { "task1": { "app": "...", "command": "...", ... }, ... },
//     "transitions": { "task1": { "success": [...], "failure": [...] }, ... }
//   }
// `tasks` and `transitions` are objects keyed by task name (not arrays).
// The presence of both keyed objects is the distinguishing feature.
public sealed class ItentialDetector : IDslDetector
{
    public string FormatName => "itential";

    public double Detect(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) return 0.0;

        var hasTasks = document.TryGetProperty("tasks", out var tasks)
            && tasks.ValueKind == JsonValueKind.Object
            && tasks.EnumerateObject().Any();
        var hasTransitions = document.TryGetProperty("transitions", out var transitions)
            && transitions.ValueKind == JsonValueKind.Object;
        if (!hasTasks || !hasTransitions) return 0.0;

        // Confirm a task object has the Itential-specific keys.
        var firstTask = tasks.EnumerateObject().FirstOrDefault();
        if (firstTask.Value.ValueKind != JsonValueKind.Object) return 0.5;
        var hasApp = firstTask.Value.TryGetProperty("app", out _);
        var hasType = firstTask.Value.TryGetProperty("type", out _);
        if (hasApp || hasType) return 0.95;

        return 0.6;
    }
}
