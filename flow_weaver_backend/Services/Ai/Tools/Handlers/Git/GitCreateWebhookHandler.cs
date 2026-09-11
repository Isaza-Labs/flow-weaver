using System.Security.Cryptography;
using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

// Creates an inbound webhook receiver for a registered Git repository
// and returns the public ingestion URL + the secret. The user must
// then paste both into GitHub/GitLab's webhook settings — we cannot
// install webhooks on their side without their provider credentials.
//
// If the caller doesn't supply a secret, we generate a 32-byte
// random one and surface it. The platform stores the encrypted
// version; this is the ONLY response that contains the plaintext, so
// the agent must show it to the user once and tell them it won't be
// retrievable later.
public sealed class GitCreateWebhookHandler : IToolHandler
{
    public string Name => "git_create_webhook";

    public string Description =>
        "Register a new inbound webhook receiver for a Git repository and " +
        "return the public ingestion URL + signing secret the user pastes " +
        "into GitHub/GitLab. Generates a random 32-byte secret when the " +
        "caller doesn't provide one — this is the ONLY time the plaintext " +
        "secret is returned, so the agent MUST show it to the user verbatim " +
        "once. After save, the secret is only accessible to the receiver. " +
        "Confirm with the user before calling — this is single_confirm tier.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id", "name", "provider"],
          "properties": {
            "repository_id": { "type": "string", "format": "uuid" },
            "name":          { "type": "string", "minLength": 1, "description": "Human label, e.g. 'ci-pipeline'." },
            "provider":      {
              "type": "string", "enum": ["github", "gitlab", "generic"],
              "description": "Determines header + signature format. github: HMAC-SHA256 in X-Hub-Signature-256. gitlab: literal token in X-Gitlab-Token. generic: HMAC-SHA256 in X-FlowWeaver-Signature."
            },
            "secret": {
              "type": "string",
              "description": "Signing secret. Omit to auto-generate a 32-byte URL-safe random string (recommended). Pass empty string ONLY together with allow_unsigned=true to disable signature verification — the receiver will reject unsigned deliveries by default."
            },
            "allow_unsigned": {
              "type": "boolean",
              "description": "Defaults to false. When true AND no secret is configured, the receiver accepts unauthenticated POSTs. Only set this for testing scenarios where the emitter cannot sign. Production webhooks should always have a secret."
            },
            "on_push_workflow_id": {
              "type": "string", "format": "uuid",
              "description": "Workflow to enqueue on every matching push. Omit to leave the webhook as 'pull-only' (auto_pull updates the local checkout but no run is fired)."
            },
            "on_push_branches": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Branch filter. Empty = match every push. Wildcards NOT supported — names match literally."
            },
            "auto_pull": {
              "type": "boolean",
              "description": "Pull the repository when a verified push arrives. Defaults to true."
            },
            "enabled": {
              "type": "boolean",
              "description": "Defaults to true. Set false to register without activating (useful for staged setup)."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitWebhookService _service;
    private readonly ILogger<GitCreateWebhookHandler> _logger;

    public GitCreateWebhookHandler(
        IGitWebhookService service,
        ILogger<GitCreateWebhookHandler> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var repoId = GitToolHelpers.GetGuid(args, "repository_id");
        if (repoId is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });

        var name = GitToolHelpers.GetString(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return JsonSerializer.SerializeToElement(new { error = "name is required" });

        var provider = (GitToolHelpers.GetString(args, "provider") ?? "github").Trim().ToLowerInvariant();
        if (provider is not ("github" or "gitlab" or "generic"))
            return JsonSerializer.SerializeToElement(new
            {
                error = "provider must be github, gitlab, or generic",
            });

        // Secret handling: a missing/null `secret` argument auto-generates;
        // an explicit empty string disables verification. Distinct from
        // null for that reason — we don't want JSON-deserialization quirks
        // to silently disable HMAC.
        string secret;
        var generated = false;
        if (args.TryGetProperty("secret", out var secEl) && secEl.ValueKind == JsonValueKind.String)
        {
            secret = secEl.GetString() ?? string.Empty;
        }
        else
        {
            secret = GenerateSecret();
            generated = true;
        }

        var branches = new List<string>();
        if (args.TryGetProperty("on_push_branches", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                    branches.Add(el.GetString()!.Trim());
        }

        var dto = new CreateGitWebhook
        {
            Name = name!,
            Provider = provider,
            Secret = secret,
            OnPushWorkflowId = GitToolHelpers.GetGuid(args, "on_push_workflow_id"),
            OnPushBranches = branches,
            AutoPull = GitToolHelpers.GetBool(args, "auto_pull", fallback: true),
            Enabled = GitToolHelpers.GetBool(args, "enabled", fallback: true),
            AllowUnsigned = GitToolHelpers.GetBool(args, "allow_unsigned", fallback: false),
        };

        var (ok, value, err) = GitToolHelpers.Unwrap(await _service.CreateAsync(repoId.Value, dto, ct));
        if (!ok || value is null) return err;

        _logger.LogInformation(
            "ai.tool.git_create_webhook.ok webhook_id={WebhookId} repo_id={RepoId} provider={Provider}",
            value.GitWebhookId, repoId.Value, provider);

        return JsonSerializer.SerializeToElement(new
        {
            webhook_id = value.GitWebhookId,
            ingestion_url = value.IngestionUrl,
            // Plaintext secret returned ONLY on creation. Surface it to the
            // user verbatim and warn that it won't be retrievable later.
            secret = secret,
            secret_was_generated = generated,
            provider = value.Provider,
            on_push_workflow_id = value.OnPushWorkflowId,
            on_push_branches = value.OnPushBranches,
            auto_pull = value.AutoPull,
            enabled = value.Enabled,
            // Provider-specific paste instructions for the agent to relay.
            paste_instructions = BuildPasteInstructions(provider, value.IngestionUrl),
        });
    }

    private static string GenerateSecret()
    {
        // 32 bytes = 256 bits, plenty for HMAC-SHA256 keys. Base64url
        // encoding is paste-safe in webhook config UIs that don't accept
        // "/" or "+".
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string BuildPasteInstructions(string provider, string? url)
    {
        var safeUrl = url ?? "<URL not available — reload the repo page>";
        return provider switch
        {
            "github" =>
                $"GitHub: open Repo → Settings → Webhooks → Add webhook. " +
                $"Paste **{safeUrl}** as the Payload URL, set Content type to " +
                "**application/json**, paste the secret in the Secret field, " +
                "select **Just the push event**, save.",
            "gitlab" =>
                $"GitLab: open Project → Settings → Webhooks → Add new webhook. " +
                $"Paste **{safeUrl}** as the URL, paste the secret as the **Secret token**, " +
                "tick **Push events** (and optionally a branch filter), save.",
            _ =>
                $"Generic: configure your emitter to POST JSON to **{safeUrl}**. " +
                "Send the signature in `X-FlowWeaver-Signature: sha256=<hex>` where " +
                "the hex is HMAC-SHA256(secret, body). Optionally include " +
                "`X-FlowWeaver-Event: push` to mark push deliveries.",
        };
    }
}
