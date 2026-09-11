using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Tools.Handlers.Git;
using flow_weaver_backend.Services.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The agent tool that wires a git webhook. The security-relevant piece is the
// secret contract: a missing argument AUTO-GENERATES one (so the agent can't
// accidentally create an unverified hook), while an explicit empty string is
// the deliberate opt-out. Conflating the two would let a JSON quirk silently
// disable HMAC verification. The plaintext secret is also returned exactly
// once — the row only ever stores its ciphertext.
public class GitCreateWebhookHandlerTests
{
    private sealed class ScriptedWebhookService : IGitWebhookService
    {
        public CreateGitWebhook? CreatedWith { get; private set; }
        public Guid RepoIdSeen { get; private set; }
        public ActionResult<GitWebhookResponse>? FailWith { get; set; }
        public string IngestionUrl { get; set; } = "https://fw.example.com/api/git/webhooks/abc";

        public Task<ActionResult<GitWebhookResponse>> CreateAsync(
            Guid repositoryId, CreateGitWebhook dto, CancellationToken ct)
        {
            RepoIdSeen = repositoryId;
            CreatedWith = dto;
            if (FailWith is not null) return Task.FromResult(FailWith);
            return Task.FromResult<ActionResult<GitWebhookResponse>>(new GitWebhookResponse
            {
                GitWebhookId = Guid.NewGuid(),
                GitRepositoryId = repositoryId,
                Name = dto.Name,
                Provider = dto.Provider,
                HasSecret = !string.IsNullOrEmpty(dto.Secret),
                OnPushWorkflowId = dto.OnPushWorkflowId,
                OnPushBranches = dto.OnPushBranches,
                AutoPull = dto.AutoPull,
                Enabled = dto.Enabled,
                AllowUnsigned = dto.AllowUnsigned,
                IngestionUrl = IngestionUrl,
            });
        }

        public Task<ActionResult<ListResponse<GitWebhookResponse>>> ListAsync(Guid repositoryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitWebhookResponse>> GetAsync(Guid repositoryId, Guid webhookId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitWebhookResponse>> UpdateAsync(Guid repositoryId, Guid webhookId, UpdateGitWebhook dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitWebhookResponse>> DeleteAsync(Guid repositoryId, Guid webhookId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<ListResponse<GitWebhookDeliveryResponse>>> ListDeliveriesAsync(
            Guid repositoryId, Guid webhookId, int limit, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static (GitCreateWebhookHandler Handler, ScriptedWebhookService Service) Build()
    {
        var service = new ScriptedWebhookService();
        return (new GitCreateWebhookHandler(service, NullLogger<GitCreateWebhookHandler>.Instance), service);
    }

    private static JsonElement Args(Guid? repoId = null, params (string Key, object? Value)[] extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["repository_id"] = (repoId ?? Guid.NewGuid()).ToString(),
            ["name"] = "push-hook",
        };
        foreach (var (k, v) in extra) payload[k] = v;
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string? ErrorOf(JsonElement result)
        => result.TryGetProperty("error", out var e) ? e.GetString() : null;

    // ─── argument validation ────────────────────────────────────────────

    [Fact]
    public async Task MissingRepositoryIdIsAnError()
    {
        var (handler, _) = Build();
        var args = JsonSerializer.SerializeToElement(new { name = "push-hook" });

        Assert.Contains("repository_id is required", ErrorOf(await handler.ExecuteAsync(args, default)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingNameIsAnError(string? name)
    {
        var (handler, _) = Build();
        var args = JsonSerializer.SerializeToElement(new
        {
            repository_id = Guid.NewGuid().ToString(),
            name,
        });

        Assert.Contains("name is required", ErrorOf(await handler.ExecuteAsync(args, default)));
    }

    [Theory]
    [InlineData("bitbucket")]
    [InlineData("gitea")]
    public async Task UnsupportedProviderIsAnError(string provider)
    {
        var (handler, _) = Build();

        var error = ErrorOf(await handler.ExecuteAsync(Args(extra: ("provider", provider)), default));

        Assert.Contains("github, gitlab, or generic", error);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("GitLab")]
    [InlineData("  GENERIC  ")]
    public async Task SupportedProvidersAreNormalised(string provider)
    {
        var (handler, service) = Build();

        await handler.ExecuteAsync(Args(extra: ("provider", provider)), default);

        Assert.Equal(provider.Trim().ToLowerInvariant(), service.CreatedWith!.Provider);
    }

    [Fact]
    public async Task ProviderDefaultsToGithub()
    {
        var (handler, service) = Build();

        await handler.ExecuteAsync(Args(), default);

        Assert.Equal("github", service.CreatedWith!.Provider);
    }

    // ─── the secret contract ────────────────────────────────────────────

    // No `secret` argument → generate one. This is what stops the agent from
    // silently creating a hook that accepts unsigned deliveries.
    [Fact]
    public async Task AbsentSecretIsAutoGenerated()
    {
        var (handler, service) = Build();

        var result = await handler.ExecuteAsync(Args(), default);

        Assert.True(result.GetProperty("secret_was_generated").GetBoolean());
        var secret = result.GetProperty("secret").GetString()!;
        Assert.NotEmpty(secret);
        Assert.Equal(secret, service.CreatedWith!.Secret);
    }

    [Fact]
    public async Task GeneratedSecretsAreUnique()
    {
        var (handler1, _) = Build();
        var (handler2, _) = Build();

        var first = (await handler1.ExecuteAsync(Args(), default)).GetProperty("secret").GetString();
        var second = (await handler2.ExecuteAsync(Args(), default)).GetProperty("secret").GetString();

        Assert.NotEqual(first, second);
    }

    // An explicit empty string is the deliberate opt-out; it must NOT be
    // confused with "argument absent".
    [Fact]
    public async Task ExplicitEmptySecretDisablesVerificationWithoutGenerating()
    {
        var (handler, service) = Build();

        var result = await handler.ExecuteAsync(Args(extra: ("secret", "")), default);

        Assert.False(result.GetProperty("secret_was_generated").GetBoolean());
        Assert.Equal("", result.GetProperty("secret").GetString());
        Assert.Equal("", service.CreatedWith!.Secret);
    }

    [Fact]
    public async Task ExplicitSecretIsUsedVerbatim()
    {
        var (handler, service) = Build();

        var result = await handler.ExecuteAsync(Args(extra: ("secret", "my-own-secret")), default);

        Assert.False(result.GetProperty("secret_was_generated").GetBoolean());
        Assert.Equal("my-own-secret", service.CreatedWith!.Secret);
        Assert.Equal("my-own-secret", result.GetProperty("secret").GetString());
    }

    // A non-string `secret` (a JSON quirk, e.g. null or a number) falls back to
    // generating — failing open here would disable HMAC.
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public async Task NonStringSecretFallsBackToGenerating(object? secret)
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Args(extra: ("secret", secret)), default);

        Assert.True(result.GetProperty("secret_was_generated").GetBoolean());
        Assert.NotEmpty(result.GetProperty("secret").GetString()!);
    }

    // ─── branch filter ──────────────────────────────────────────────────

    [Fact]
    public async Task BranchesAreForwardedTrimmed()
    {
        var (handler, service) = Build();

        await handler.ExecuteAsync(
            Args(extra: ("on_push_branches", new[] { "  main  ", "release" })), default);

        Assert.Equal(new[] { "main", "release" }, service.CreatedWith!.OnPushBranches);
    }

    [Fact]
    public async Task BlankAndNonStringBranchesAreDropped()
    {
        var (handler, service) = Build();
        var args = JsonSerializer.SerializeToElement(new
        {
            repository_id = Guid.NewGuid().ToString(),
            name = "hook",
            on_push_branches = new object?[] { "main", "", "   ", 42, null },
        });

        await handler.ExecuteAsync(args, default);

        Assert.Equal(new[] { "main" }, service.CreatedWith!.OnPushBranches);
    }

    [Fact]
    public async Task AbsentBranchesMeansNoFilter()
    {
        var (handler, service) = Build();

        await handler.ExecuteAsync(Args(), default);

        Assert.Empty(service.CreatedWith!.OnPushBranches);
    }

    // ─── flags + wiring ─────────────────────────────────────────────────

    // Auto-pull and enabled default on; unsigned deliveries default OFF.
    [Fact]
    public async Task FlagDefaultsFavourASecureEnabledHook()
    {
        var (handler, service) = Build();

        await handler.ExecuteAsync(Args(), default);

        Assert.True(service.CreatedWith!.AutoPull);
        Assert.True(service.CreatedWith.Enabled);
        Assert.False(service.CreatedWith.AllowUnsigned);
    }

    [Fact]
    public async Task FlagsAreHonouredWhenSupplied()
    {
        var (handler, service) = Build();

        await handler.ExecuteAsync(Args(extra:
            new (string, object?)[] { ("auto_pull", false), ("enabled", false), ("allow_unsigned", true) }), default);

        Assert.False(service.CreatedWith!.AutoPull);
        Assert.False(service.CreatedWith.Enabled);
        Assert.True(service.CreatedWith.AllowUnsigned);
    }

    [Fact]
    public async Task TheWorkflowAndRepositoryAreWiredThrough()
    {
        var (handler, service) = Build();
        var repoId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        await handler.ExecuteAsync(
            Args(repoId, ("on_push_workflow_id", workflowId.ToString())), default);

        Assert.Equal(repoId, service.RepoIdSeen);
        Assert.Equal(workflowId, service.CreatedWith!.OnPushWorkflowId);
    }

    // ─── response ───────────────────────────────────────────────────────

    // The ingestion URL plus paste instructions are what the agent relays so
    // the user can finish the setup in the provider's UI.
    [Fact]
    public async Task ResponseCarriesTheIngestionUrlAndPasteInstructions()
    {
        var (handler, service) = Build();
        service.IngestionUrl = "https://fw.example.com/api/git/webhooks/xyz";

        var result = await handler.ExecuteAsync(Args(), default);

        Assert.Equal(service.IngestionUrl, result.GetProperty("ingestion_url").GetString());
        var instructions = result.GetProperty("paste_instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains(service.IngestionUrl, instructions);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("gitlab")]
    [InlineData("generic")]
    public async Task PasteInstructionsAreProviderSpecific(string provider)
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Args(extra: ("provider", provider)), default);

        Assert.NotEmpty(result.GetProperty("paste_instructions").GetString()!);
    }

    [Fact]
    public async Task ResponseEchoesTheStoredConfiguration()
    {
        var (handler, _) = Build();
        var workflowId = Guid.NewGuid();

        var result = await handler.ExecuteAsync(Args(extra:
            new (string, object?)[]
            {
                ("on_push_workflow_id", workflowId.ToString()),
                ("on_push_branches", new[] { "main" }),
            }), default);

        Assert.Equal(workflowId, result.GetProperty("on_push_workflow_id").GetGuid());
        Assert.Equal("main", Assert.Single(result.GetProperty("on_push_branches").EnumerateArray()).GetString());
        Assert.True(result.GetProperty("enabled").GetBoolean());
        Assert.NotEqual(Guid.Empty, result.GetProperty("webhook_id").GetGuid());
    }

    // A service-level failure is relayed as-is so the agent can explain it.
    [Fact]
    public async Task ServiceFailureIsRelayed()
    {
        var (handler, service) = Build();
        service.FailWith = new NotFoundObjectResult(new { error = "git repository not found" });

        var result = await handler.ExecuteAsync(Args(), default);

        Assert.Contains("not found", result.GetRawText());
    }
}
