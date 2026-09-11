using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Git;

// Threshold above which webhook-driven runs are temporarily refused to
// shed load. The job table sits in front of every executor, so once it
// passes this depth the system is already behind.
internal static class GitWebhookBackpressure
{
    public const int MaxQueueDepth = 500;
}

// Handles a verified webhook delivery: parses the payload, optionally
// pulls the repo, and optionally enqueues a workflow run. Persists a
// GitWebhookDelivery row regardless of outcome so admins can see what
// fired and what tripped.
//
// Concurrency: this method is invoked from the public webhook
// controller (no auth header), so the caller has NO ICurrentUser
// context — this service binds a synthetic identity onto the scope's
// MutableCurrentUser. The pattern matches what the scheduler hosted
// service already does for cron-driven runs.
public sealed class GitWebhookReceiver
{
    public sealed record Outcome(int StatusCode, string Message, Guid? WorkflowRunId);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitWebhookReceiver> _logger;

    public GitWebhookReceiver(
        IServiceScopeFactory scopeFactory,
        ILogger<GitWebhookReceiver> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<Outcome> ReceiveAsync(
        Guid webhookId, string? eventHeader, string? signatureHeader,
        byte[] body, CancellationToken ct)
    {
        // We need a fresh scope so we can rebind ICurrentUser for the
        // downstream services. The receiver runs without auth headers.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var webhooks = sp.GetRequiredService<IGitWebhookRepository>();
        var jobs = sp.GetRequiredService<IJobRepository>();
        var crypto = sp.GetRequiredService<ICredentialEncryptionService>();
        var userOverride = sp.GetRequiredService<MutableCurrentUser>();

        var hook = await webhooks.FindActiveByIdAsync(webhookId, ct);
        if (hook is null)
            return new Outcome(404, "webhook not found", null);

        if (!hook.Enabled)
        {
            await PersistDeliveryAsync(webhooks, hook, "rejected", null, null, null, null, "webhook disabled", ct);
            return new Outcome(403, "webhook disabled", null);
        }

        // Signature verification:
        //   • Secret on file → verify; reject on mismatch.
        //   • No secret AND AllowUnsigned=false (default) → reject.
        //   • No secret AND AllowUnsigned=true → admin opted into
        //     unauthenticated deliveries (testing scenarios), accept.
        var hasSecret = hook.EncryptedSecret is { Length: > 0 };
        if (hasSecret)
        {
            var secret = crypto.Decrypt(hook.EncryptedSecret) ?? string.Empty;
            var ok = hook.Provider switch
            {
                GitWebhook.ProviderGithub => GitWebhookSignature.VerifyGithub(body, signatureHeader, secret),
                GitWebhook.ProviderGitlab => GitWebhookSignature.VerifyGitlab(signatureHeader, secret),
                GitWebhook.ProviderGeneric => GitWebhookSignature.VerifyGeneric(body, signatureHeader, secret),
                _ => false,
            };
            if (!ok)
            {
                await PersistDeliveryAsync(webhooks, hook, "rejected", null, null, null, null, "signature mismatch", ct);
                _logger.LogWarning(
                    "git.webhook.signature_invalid webhook_id={WebhookId} provider={Provider}",
                    hook.GitWebhookId, hook.Provider);
                return new Outcome(401, "signature mismatch", null);
            }
        }
        else if (!hook.AllowUnsigned)
        {
            await PersistDeliveryAsync(webhooks, hook, "rejected", null, null, null, null, "no secret configured and allow_unsigned=false", ct);
            _logger.LogWarning(
                "git.webhook.unsigned_rejected webhook_id={WebhookId}",
                hook.GitWebhookId);
            return new Outcome(401, "webhook has no secret configured; set one or enable allow_unsigned", null);
        }

        var parsed = GitWebhookPayload.Parse(hook.Provider, eventHeader, body);

        // Ping-style handshake events (GitHub sends `ping` on creation,
        // GitLab sends `system_hook`/`tag_push` we don't handle) just
        // need a 200 with a verified row.
        var isPush = string.Equals(parsed.Event, "push", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(parsed.Event, "Push Hook", StringComparison.OrdinalIgnoreCase);
        if (!isPush)
        {
            await PersistDeliveryAsync(webhooks, hook, "verified", parsed.Event, parsed.Branch, parsed.CommitSha, null, null, ct);
            return new Outcome(200, $"event '{parsed.Event ?? "?"}' acknowledged (no action)", null);
        }

        // Branch filter — empty means "every branch passes".
        if (hook.OnPushBranches is { Count: > 0 } filter
            && parsed.Branch is { Length: > 0 } branch
            && !filter.Contains(branch, StringComparer.Ordinal))
        {
            await PersistDeliveryAsync(webhooks, hook, "verified", parsed.Event, parsed.Branch, parsed.CommitSha, null,
                $"branch '{branch}' not in filter", ct);
            return new Outcome(200, $"branch '{branch}' filtered out", null);
        }

        // Stamp a synthetic identity on this anonymous scope so anything
        // IGitService / IWorkflowExecutor attribute to a caller is labelled.
        userOverride.Bind(userId: null, username: "git-webhook");

        // Backpressure: if the job queue is already deep, refuse to
        // enqueue more work. The webhook source can retry later.
        var queueDepth = await jobs.CountByStatusesAsync(new[] { JobStatus.Pending, JobStatus.Claimed }, ct);
        if (queueDepth >= GitWebhookBackpressure.MaxQueueDepth)
        {
            await PersistDeliveryAsync(webhooks, hook, "rejected",
                evt: null, branch: null, commitSha: null,
                runId: null, error: $"queue full ({queueDepth} pending/running jobs)", ct);
            _logger.LogWarning(
                "git.webhook.backpressure webhook_id={WebhookId} queue_depth={QueueDepth}",
                hook.GitWebhookId, queueDepth);
            return new Outcome(503, "queue full, retry later", null);
        }

        // Optional auto-pull. Failures here don't block run dispatch —
        // an admin might want the run to fire even if pulling fails so
        // they get visibility on the broken repo state.
        if (hook.AutoPull)
        {
            var git = sp.GetRequiredService<IGitService>();
            try
            {
                await git.PullAsync(hook.GitRepositoryId, parsed.Branch, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "git.webhook.pull_failed webhook_id={WebhookId} repo_id={RepoId}",
                    hook.GitWebhookId, hook.GitRepositoryId);
            }
        }

        Guid? runId = null;
        string? error = null;
        if (hook.OnPushWorkflowId is { } targetWorkflowId)
        {
            try
            {
                var executor = sp.GetRequiredService<IWorkflowExecutor>();
                runId = await executor.EnqueueRunAsync(Guid.Empty,
                    targetWorkflowId,
                    new RunWorkflowRequest
                    {
                        Input = JsonDocument.Parse(JsonSerializer.Serialize(new
                        {
                            git_webhook_id = hook.GitWebhookId,
                            repository_id = hook.GitRepositoryId,
                            branch = parsed.Branch,
                            commit_sha = parsed.CommitSha,
                            provider = hook.Provider,
                        })).RootElement,
                    },
                    ct,
                    trigger: "webhook");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogError(ex,
                    "git.webhook.enqueue_failed webhook_id={WebhookId} workflow_id={WorkflowId}",
                    hook.GitWebhookId, targetWorkflowId);
            }
        }

        var status = error is null ? "dispatched" : "failed";
        await PersistDeliveryAsync(webhooks, hook, status, parsed.Event, parsed.Branch, parsed.CommitSha, runId, error, ct);

        return new Outcome(
            error is null ? 202 : 500,
            error ?? (runId is null ? "verified (no workflow configured)" : "workflow run enqueued"),
            runId);
    }

    private static async Task PersistDeliveryAsync(
        IGitWebhookRepository webhooks, GitWebhook hook,
        string status, string? evt, string? branch, string? commitSha,
        Guid? runId, string? error, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        webhooks.AddDelivery(new GitWebhookDelivery
        {
            GitWebhookDeliveryId = Guid.NewGuid(),
            GitWebhookId = hook.GitWebhookId,
            At = now,
            Status = status,
            Event = evt,
            Branch = branch,
            CommitSha = commitSha,
            WorkflowRunId = runId,
            Error = error,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Touch the parent row so the UI can show "last delivery" without
        // joining the history table.
        var tracked = await webhooks.FindTrackedByIdAsync(hook.GitWebhookId, ct);
        if (tracked is not null)
        {
            tracked.LastDeliveryAt = now;
            tracked.LastDeliveryStatus = status;
            tracked.UpdatedAt = now;
        }
        await webhooks.SaveChangesAsync(ct);
    }
}
