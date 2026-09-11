using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Ai.Security;

// Heuristic prompt injection detector. Does NOT block — only flags
// suspicious patterns so they can be logged in AuditEvent.metadata.
// The LLM's system prompt in Skills/base.md is the primary defense;
// this checker adds observability.
//
// Registered as a singleton so the compiled Regex and the injected
// ILogger live for the process lifetime.
public sealed partial class PromptSafetyChecker
{
    [GeneratedRegex(@"ignore\s+(previous|all|above)|system\s+prompt|you\s+are\s+now|disregard\s+(all|previous)|^---\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SuspiciousPattern();

    private readonly ILogger<PromptSafetyChecker> _logger;

    public PromptSafetyChecker(ILogger<PromptSafetyChecker> logger)
    {
        _logger = logger;
    }

    public PromptSafetyResult Check(string prompt)
    {
        try
        {
            if (string.IsNullOrEmpty(prompt))
            {
                _logger.LogDebug("ai.prompt.safety.ok prompt_chars={PromptChars}", 0);
                return new PromptSafetyResult(false, Array.Empty<string>());
            }

            var flags = new List<string>();
            string? firstReason = null;

            if (prompt.Length > 32_768)
            {
                flags.Add("prompt_too_long");
                firstReason = "prompt_too_long";
            }

            var matches = SuspiciousPattern().Matches(prompt);
            foreach (Match m in matches)
                flags.Add($"suspicious_pattern:{m.Value.Trim()[..Math.Min(40, m.Value.Trim().Length)]}");
            if (matches.Count > 0)
                firstReason ??= "injection_marker_detected";

            if (flags.Count == 0)
            {
                _logger.LogDebug("ai.prompt.safety.ok prompt_chars={PromptChars}", prompt.Length);
                return new PromptSafetyResult(false, Array.Empty<string>());
            }

            // Log the reason category only — the matched payload stays in
            // the returned Flags array for AuditEvent metadata, but we do
            // NOT echo attacker-controlled text through the structured
            // logging sink.
            _logger.LogWarning(
                "ai.prompt.safety.blocked reason={Reason} prompt_chars={PromptChars}",
                firstReason, prompt.Length);
            return new PromptSafetyResult(true, flags.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.prompt.safety.failed");
            throw;
        }
    }
}

public sealed record PromptSafetyResult(bool Flagged, string[] Flags);
