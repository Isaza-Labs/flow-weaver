using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using GitRepoModel = flow_weaver_backend.Models.GitRepository;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Git;

public sealed class GitWebhookService : IGitWebhookService
{
    private static readonly string[] AllowedProviders =
        { GitWebhook.ProviderGithub, GitWebhook.ProviderGitlab, GitWebhook.ProviderGeneric };

    private readonly IGitWebhookRepository _webhooks;
    private readonly IRepository<GitRepoModel> _repos;
    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ICurrentUser _caller;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<GitWebhookService> _logger;

    public GitWebhookService(
        IGitWebhookRepository webhooks,
        IRepository<GitRepoModel> repos,
        IRepository<WorkflowModel> workflows,
        ICurrentUser caller,
        ICredentialEncryptionService crypto,
        IHttpContextAccessor http,
        ILogger<GitWebhookService> logger)
    {
        _webhooks = webhooks;
        _repos = repos;
        _workflows = workflows;
        _caller = caller;
        _crypto = crypto;
        _http = http;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<GitWebhookResponse>>> ListAsync(
        Guid repositoryId, CancellationToken ct)
    {
        if (!await _repos.ExistsAsync(repositoryId, ct: ct))
            return Err<ListResponse<GitWebhookResponse>>(404, "git repository not found");

        var rows = await _webhooks.ListByRepoAsync(repositoryId, ct);

        return new ListResponse<GitWebhookResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = rows.Count,
            Limit = rows.Count,
            Offset = 0,
        };
    }

    public async Task<ActionResult<GitWebhookResponse>> GetAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct)
    {
        var row = await _webhooks.FindByRepoAsync(repositoryId, webhookId, ct);
        if (row is null) return Err<GitWebhookResponse>(404, "webhook not found");
        return ToResponse(row);
    }

    public async Task<ActionResult<GitWebhookResponse>> CreateAsync(
        Guid repositoryId, CreateGitWebhook dto, CancellationToken ct)
    {
        if (!await _repos.ExistsAsync(repositoryId, ct: ct))
            return Err<GitWebhookResponse>(404, "git repository not found");

        var name = (dto.Name ?? string.Empty).Trim();
        if (name.Length == 0) return Err<GitWebhookResponse>(400, "name is required");
        var provider = NormalizeProvider(dto.Provider);
        if (provider is null) return Err<GitWebhookResponse>(400, "provider must be github, gitlab, or generic");

        if (dto.OnPushWorkflowId is { } wfid)
        {
            var wfExists = await _workflows.ExistsAsync(wfid, ct: ct);
            if (!wfExists) return Err<GitWebhookResponse>(400, "on_push_workflow_id not found");
        }

        var now = DateTime.UtcNow;
        var row = new GitWebhook
        {
            GitWebhookId = Guid.NewGuid(),
            GitRepositoryId = repositoryId,
            Name = name,
            Provider = provider,
            EncryptedSecret = string.IsNullOrEmpty(dto.Secret) ? null : _crypto.Encrypt(dto.Secret),
            OnPushWorkflowId = dto.OnPushWorkflowId,
            OnPushBranches = NormalizeBranches(dto.OnPushBranches),
            AutoPull = dto.AutoPull,
            Enabled = dto.Enabled,
            AllowUnsigned = dto.AllowUnsigned,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _webhooks.Add(row);
        await _webhooks.SaveChangesAsync(ct);

        _logger.LogInformation(
            "git.webhook.created webhook_id={WebhookId} repo_id={RepoId} provider={Provider}",
            row.GitWebhookId, repositoryId, provider);

        return new ObjectResult(ToResponse(row)) { StatusCode = 201 };
    }

    public async Task<ActionResult<GitWebhookResponse>> UpdateAsync(
        Guid repositoryId, Guid webhookId, UpdateGitWebhook dto, CancellationToken ct)
    {
        var row = await _webhooks.FindByRepoAsync(repositoryId, webhookId, ct);
        if (row is null) return Err<GitWebhookResponse>(404, "webhook not found");

        if (dto.Name is { } n && n.Trim().Length > 0) row.Name = n.Trim();
        if (dto.Provider is { } p)
        {
            var provider = NormalizeProvider(p);
            if (provider is null) return Err<GitWebhookResponse>(400, "provider must be github, gitlab, or generic");
            row.Provider = provider;
        }
        if (dto.Secret is not null)
        {
            // null = no change. "" = clear secret (signature checks become no-op).
            row.EncryptedSecret = dto.Secret.Length == 0 ? null : _crypto.Encrypt(dto.Secret);
        }
        if (dto.OnPushWorkflowId.HasValue) row.OnPushWorkflowId = dto.OnPushWorkflowId.Value;
        if (dto.OnPushBranches is not null) row.OnPushBranches = NormalizeBranches(dto.OnPushBranches);
        if (dto.AutoPull.HasValue) row.AutoPull = dto.AutoPull.Value;
        if (dto.Enabled.HasValue) row.Enabled = dto.Enabled.Value;
        if (dto.AllowUnsigned.HasValue) row.AllowUnsigned = dto.AllowUnsigned.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _webhooks.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    public async Task<ActionResult<GitWebhookResponse>> DeleteAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct)
    {
        var row = await _webhooks.FindByRepoAsync(repositoryId, webhookId, ct);
        if (row is null) return Err<GitWebhookResponse>(404, "webhook not found");
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _webhooks.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    public async Task<ActionResult<ListResponse<GitWebhookDeliveryResponse>>> ListDeliveriesAsync(
        Guid repositoryId, Guid webhookId, int limit, CancellationToken ct)
    {
        var hook = await _webhooks.FindByRepoAsync(repositoryId, webhookId, ct);
        if (hook is null) return Err<ListResponse<GitWebhookDeliveryResponse>>(404, "webhook not found");
        limit = Math.Clamp(limit, 1, 200);
        var rows = await _webhooks.ListDeliveriesAsync(webhookId, limit, ct);
        return new ListResponse<GitWebhookDeliveryResponse>
        {
            Data = rows.Select(d => new GitWebhookDeliveryResponse
            {
                GitWebhookDeliveryId = d.GitWebhookDeliveryId,
                At = d.At,
                Status = d.Status,
                Event = d.Event,
                Branch = d.Branch,
                CommitSha = d.CommitSha,
                WorkflowRunId = d.WorkflowRunId,
                Error = d.Error,
            }).ToList(),
            Total = rows.Count,
            Limit = limit,
            Offset = 0,
        };
    }

    // ─── helpers ────────────────────────────────────────────────────

    private static string? NormalizeProvider(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var lower = p.Trim().ToLowerInvariant();
        return AllowedProviders.Contains(lower) ? lower : null;
    }

    private static List<string> NormalizeBranches(IEnumerable<string>? input)
    {
        if (input is null) return new();
        return input
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private GitWebhookResponse ToResponse(GitWebhook w) => new()
    {
        GitWebhookId = w.GitWebhookId,
        GitRepositoryId = w.GitRepositoryId,
        Name = w.Name,
        Provider = w.Provider,
        HasSecret = w.EncryptedSecret is { Length: > 0 },
        OnPushWorkflowId = w.OnPushWorkflowId,
        OnPushBranches = w.OnPushBranches ?? new(),
        AutoPull = w.AutoPull,
        Enabled = w.Enabled,
        AllowUnsigned = w.AllowUnsigned,
        LastDeliveryAt = w.LastDeliveryAt,
        LastDeliveryStatus = w.LastDeliveryStatus,
        IngestionUrl = BuildIngestionUrl(w.GitWebhookId),
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
    };

    private string? BuildIngestionUrl(Guid webhookId)
    {
        var req = _http.HttpContext?.Request;
        if (req is null) return null;
        var scheme = req.Scheme;
        var host = req.Host.Value;
        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(host)) return null;
        return $"{scheme}://{host}/api/git/webhooks/{webhookId}";
    }

    private static ActionResult<T> Err<T>(int status, string error) =>
        new ObjectResult(new { error }) { StatusCode = status };
}
