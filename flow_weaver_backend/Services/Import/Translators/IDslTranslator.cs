using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Translators;

public sealed class TranslationResult
{
    // The proposed workflow in our v1 schema shape. The pipeline does
    // not promise the result is valid yet — SchemaValidator runs next.
    public JsonElement V1Workflow { get; init; }

    // Free-form notes about decisions the translator made (mappings,
    // dropped attributes, ambiguities resolved one way or the other).
    // Surfaced to the user in the wizard.
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    // Soft warnings — does not block commit but the user should see
    // them ("retry_policy not preserved", "branch ordering inferred").
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

// Paired with IDslDetector by FormatName. The pipeline picks the
// translator whose FormatName matches the winning detector and feeds
// it the same parsed document.
public interface IDslTranslator
{
    string FormatName { get; }

    Task<TranslationResult> TranslateAsync(
        JsonElement document,
        CancellationToken ct);
}
