using System.Text.Json;

namespace flow_weaver_backend.Services.Git;

// Best-effort parse of the bits we need from a webhook payload —
// branch name, head commit SHA, event kind. Extracting more would
// commit us to a provider-specific schema; we keep the minimum so
// the receiver stays provider-agnostic where possible.
public readonly record struct ParsedPayload(
    string? Event,
    string? Branch,
    string? CommitSha);

public static class GitWebhookPayload
{
    // GitHub event header:  X-GitHub-Event
    // GitLab object_kind:   in body { "object_kind": "push" }
    // Generic:              caller passes "push" / payload as-is.
    public static ParsedPayload Parse(string provider, string? eventHeader, ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
            return new ParsedPayload(eventHeader, null, null);

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(body.ToArray()); }
        catch (JsonException) { return new ParsedPayload(eventHeader, null, null); }

        using (doc)
        {
            var root = doc.RootElement;
            var evt = eventHeader;
            if (string.IsNullOrEmpty(evt)
                && root.TryGetProperty("object_kind", out var ok)
                && ok.ValueKind == JsonValueKind.String)
                evt = ok.GetString();

            // ref = "refs/heads/main" (push) or "refs/tags/v1" (tag push).
            // Branch is everything after "refs/heads/".
            string? branch = null;
            if (root.TryGetProperty("ref", out var refEl) && refEl.ValueKind == JsonValueKind.String)
            {
                var raw = refEl.GetString() ?? string.Empty;
                const string prefix = "refs/heads/";
                if (raw.StartsWith(prefix, StringComparison.Ordinal))
                    branch = raw[prefix.Length..];
            }

            // GitHub: head_commit.id ; GitLab: checkout_sha ; generic: after.
            string? sha = null;
            if (root.TryGetProperty("head_commit", out var hc)
                && hc.ValueKind == JsonValueKind.Object
                && hc.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String)
                sha = idEl.GetString();
            if (sha is null
                && root.TryGetProperty("checkout_sha", out var coEl)
                && coEl.ValueKind == JsonValueKind.String)
                sha = coEl.GetString();
            if (sha is null
                && root.TryGetProperty("after", out var afEl)
                && afEl.ValueKind == JsonValueKind.String)
                sha = afEl.GetString();
            if (sha == "0000000000000000000000000000000000000000") sha = null; // branch deletion

            return new ParsedPayload(evt, branch, sha);
        }
    }
}
