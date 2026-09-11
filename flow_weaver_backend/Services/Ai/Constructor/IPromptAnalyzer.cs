namespace flow_weaver_backend.Services.Ai.Constructor;

public interface IPromptAnalyzer
{
    Task<PromptAnalysis> AnalyzeAsync(string prompt, CancellationToken ct);
}

public sealed class PromptAnalysis
{
    public required bool Sufficient { get; init; }
    public List<string> MissingAspects { get; init; } = new();
    public List<string> ClarifyingQuestions { get; init; } = new();
}
