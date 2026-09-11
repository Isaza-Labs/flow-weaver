using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Translators;

// Identity translator for our own v1 export. Returns the input
// unchanged. Round-trip with the JsonExporter is exact; YAML imports
// are normalised by the YAML parser before reaching us so they also
// land here in canonical JSON shape.
public sealed class FlowWeaverV1Translator : IDslTranslator
{
    public string FormatName => "flow_weaver_v1";

    public Task<TranslationResult> TranslateAsync(JsonElement document, CancellationToken ct)
    {
        return Task.FromResult(new TranslationResult
        {
            V1Workflow = document.Clone(),
            Notes = new[] { "Native v1 import — no translation applied." },
            Warnings = Array.Empty<string>(),
        });
    }
}
