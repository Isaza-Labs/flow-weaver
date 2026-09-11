using System.Collections.Concurrent;
using System.Text;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Identity;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CredentialModel = flow_weaver_backend.Models.Credential;
using GitRepoModel = flow_weaver_backend.Models.GitRepository;

namespace flow_weaver_backend.Services.Git;

// HTTPS+token implementation. SSH-key auth is intentionally out of
// scope for v1: deployments that need it should run an ssh-agent in
// the container and we'll plug LibGit2Sharp's CredentialsHandler with
// a Sshlocator branch later. Today every clone/fetch/push uses the
// PAT stored in the linked Credential row.
//
// Concurrency model: a per-repository SemaphoreSlim serializes index
// mutations within the process. Multiple processes (worker fan-out)
// will still race for the same checkout — production deployments must
// pin Git work to a single replica until we move to a queued worker.
public sealed class GitService : IGitService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    private readonly IGitRepositoryRepository _repos;
    private readonly IRepository<CredentialModel> _credentials;
    private readonly ICurrentUser _caller;
    private readonly ICredentialEncryptionService _crypto;
    private readonly flow_weaver_backend.Services.Audit.IAuditLogger _audit;
    private readonly ILogger<GitService> _logger;
    private readonly string _gitRoot;
    private readonly long _maxFileBytes;
    private readonly string _defaultAuthorName;
    private readonly string _defaultAuthorEmail;

    public GitService(
        IGitRepositoryRepository repos,
        IRepository<CredentialModel> credentials,
        ICurrentUser caller,
        ICredentialEncryptionService crypto,
        IConfiguration config,
        IHostEnvironment env,
        flow_weaver_backend.Services.Audit.IAuditLogger audit,
        ILogger<GitService> logger)
    {
        _repos = repos;
        _credentials = credentials;
        _caller = caller;
        _crypto = crypto;
        _audit = audit;
        _logger = logger;
        var configured = config["Git:Root"] ?? "./data/git";
        _gitRoot = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);
        _maxFileBytes = config.GetValue<long?>("Git:MaxFileBytes") ?? 5 * 1024 * 1024;
        _defaultAuthorName = config["Git:DefaultAuthorName"] ?? "FlowWeaver";
        _defaultAuthorEmail = config["Git:DefaultAuthorEmail"] ?? "flowweaver@localhost";
    }

    // Shared audit projection. No credential material — AuthCredentialId is
    // a reference, and the credential's own audit trail covers the secret.
    private static object RepoAudit(GitRepoModel r) => new
    {
        r.Name,
        r.Url,
        default_branch = r.DefaultBranch,
        auth_credential_id = r.AuthCredentialId,
        r.Description,
    };

    // ─── CRUD ───────────────────────────────────────────────────────

    public async Task<ActionResult<ListResponse<GitRepositoryResponse>>> ListAsync(
        int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        var total = await _repos.CountAsync(ct: ct);
        var rows = await _repos.ListOrderedByNameAsync(limit, offset, ct);
        return new ListResponse<GitRepositoryResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        };
    }

    public async Task<ActionResult<GitRepositoryResponse>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row is null) return Err<GitRepositoryResponse>(404, "git repository not found");
        return ToResponse(row);
    }

    public async Task<ActionResult<GitRepositoryResponse>> CreateAsync(
        CreateGitRepository dto, CancellationToken ct)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        var url = (dto.Url ?? string.Empty).Trim();
        if (name.Length == 0) return Err<GitRepositoryResponse>(400, "name is required");
        if (url.Length == 0) return Err<GitRepositoryResponse>(400, "url is required");
        if (!IsAllowedUrl(url))
            return Err<GitRepositoryResponse>(400, "url must be https:// (ssh and git:// are not supported)");

        var dup = await _repos.NameExistsAsync(name, ct);
        if (dup) return Err<GitRepositoryResponse>(409, "a repository with that name already exists");

        if (dto.AuthCredentialId is { } credId)
        {
            var credExists = await _credentials.ExistsAsync(credId, ct: ct);
            if (!credExists) return Err<GitRepositoryResponse>(400, "auth_credential_id not found");
        }

        var now = DateTime.UtcNow;
        var row = new GitRepoModel
        {
            GitRepositoryId = Guid.NewGuid(),
            Name = name,
            Url = url,
            DefaultBranch = string.IsNullOrWhiteSpace(dto.DefaultBranch) ? "main" : dto.DefaultBranch.Trim(),
            AuthCredentialId = dto.AuthCredentialId,
            Description = dto.Description,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        row.LocalPath = ComputeLocalPath(row);
        _repos.Add(row);
        await _repos.SaveChangesAsync(ct);

        // Registering a repo points the platform at an external destination
        // it will later push to, using the credential named here. Both halves
        // matter: repointing Url, or swapping AuthCredentialId, changes where
        // generated config ends up and under whose identity.
        await _audit.LogAsync("git_repository", row.GitRepositoryId, "create",
            after: RepoAudit(row), ct: ct);

        _logger.LogInformation(
            "git.repo.created repo_id={RepoId} name={Name} url={Url}",
            row.GitRepositoryId, row.Name, row.Url);

        return new ObjectResult(ToResponse(row)) { StatusCode = 201 };
    }

    public async Task<ActionResult<GitRepositoryResponse>> UpdateAsync(
        Guid id, UpdateGitRepository dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row is null) return Err<GitRepositoryResponse>(404, "git repository not found");

        // Snapshot before the dto lands (`row` is tracked).
        var auditBefore = RepoAudit(row);

        if (dto.Name is { } n && n.Trim().Length > 0) row.Name = n.Trim();
        if (dto.Url is { } u && u.Trim().Length > 0)
        {
            if (!IsAllowedUrl(u)) return Err<GitRepositoryResponse>(400, "url must be https://");
            row.Url = u.Trim();
        }
        if (dto.DefaultBranch is { } b && b.Trim().Length > 0) row.DefaultBranch = b.Trim();
        if (dto.AuthCredentialId.HasValue) row.AuthCredentialId = dto.AuthCredentialId.Value;
        if (dto.Description is not null) row.Description = dto.Description;
        row.UpdatedAt = DateTime.UtcNow;

        await _repos.SaveChangesAsync(ct);

        await _audit.LogAsync("git_repository", row.GitRepositoryId, "update",
            before: auditBefore, after: RepoAudit(row), ct: ct);

        return ToResponse(row);
    }

    public async Task<ActionResult<GitRepositoryResponse>> DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row is null) return Err<GitRepositoryResponse>(404, "git repository not found");
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _repos.SaveChangesAsync(ct);

        await _audit.LogAsync("git_repository", row.GitRepositoryId, "delete",
            before: RepoAudit(row), ct: ct);

        // Free the per-repo SemaphoreSlim so it doesn't accumulate in
        // the static dictionary across the process lifetime. Anyone
        // currently holding the lock will finish their op against the
        // (still-existing) on-disk checkout; new operations after
        // re-registering will get a fresh semaphore from GetOrAdd.
        if (_locks.TryRemove(id, out var sem))
        {
            try { sem.Dispose(); } catch { /* concurrent waiters; harmless */ }
        }

        // We deliberately leave the on-disk checkout in place — restoring an
        // accidentally-deleted repo registration is much less painful when
        // the working tree survives. A future janitor service can sweep
        // soft-deleted repos older than N days.
        return ToResponse(row);
    }

    // ─── Operations ─────────────────────────────────────────────────

    public Task<ActionResult<GitOpResult>> PullAsync(Guid id, string? branch, CancellationToken ct) =>
        WithRepoAsync<GitOpResult>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            var targetBranch = string.IsNullOrWhiteSpace(branch) ? row.DefaultBranch : branch!;

            if (auth.IsSsh)
            {
                if (string.IsNullOrEmpty(auth.PrivateKeyPem))
                    return new GitOpResult { Ok = false, Message = "SSH URL needs a key credential", Branch = targetBranch };
                using (var repoEarly = new Repository(repoPath))
                    CheckoutOrCreate(repoEarly, targetBranch);
                var pull = await SshGitCli.PullAsync(repoPath, targetBranch, auth.PrivateKeyPem, auth.Passphrase, _logger, ct);
                if (pull.Ok)
                {
                    using var repoAfter = new Repository(repoPath);
                    row.LastFetchedAt = DateTime.UtcNow;
                    row.UpdatedAt = DateTime.UtcNow;
                    await _repos.SaveChangesAsync(ct);
                    return new GitOpResult
                    {
                        Ok = true,
                        Message = pull.Message,
                        CommitSha = repoAfter.Head?.Tip?.Sha,
                        Branch = targetBranch,
                    };
                }
                return new GitOpResult { Ok = false, Message = pull.Message, Branch = targetBranch };
            }

            using var repo = new Repository(repoPath);
            var fetchOpts = new FetchOptions { CredentialsProvider = auth.CredentialsHandler };
            Commands.Fetch(repo, "origin", Array.Empty<string>(), fetchOpts, "fetch from FlowWeaver");
            CheckoutOrCreate(repo, targetBranch);

            var signature = BuildSignature(null, null);
            var mergeResult = Commands.Pull(repo, signature, new PullOptions
            {
                FetchOptions = fetchOpts,
                MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.Default },
            });

            row.LastFetchedAt = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
            await _repos.SaveChangesAsync(ct);

            return new GitOpResult
            {
                Ok = mergeResult.Status != MergeStatus.Conflicts,
                Message = mergeResult.Status.ToString(),
                CommitSha = mergeResult.Commit?.Sha,
                Branch = targetBranch,
            };
        });

    public Task<ActionResult<GitOpResult>> PushAsync(Guid id, string? branch, CancellationToken ct) =>
        WithRepoAsync<GitOpResult>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            string targetBranch;
            using (var repoForBranch = new Repository(repoPath))
            {
                targetBranch = string.IsNullOrWhiteSpace(branch)
                    ? (repoForBranch.Head?.FriendlyName ?? row.DefaultBranch)
                    : branch!;
                var localBranch = repoForBranch.Branches[targetBranch];
                if (localBranch is null)
                    return new GitOpResult { Ok = false, Message = $"branch '{targetBranch}' not found locally" };

                if (!auth.IsSsh)
                {
                    var pushOpts = new PushOptions { CredentialsProvider = auth.CredentialsHandler };
                    try
                    {
                        var published = PushBranch(repoForBranch, localBranch, pushOpts);
                        return new GitOpResult
                        {
                            Ok = true,
                            Message = published ? "pushed and set upstream" : "pushed",
                            Branch = targetBranch,
                            CommitSha = localBranch.Tip.Sha,
                        };
                    }
                    catch (LibGit2SharpException ex)
                    {
                        _logger.LogWarning(ex, "git.push.failed repo_id={RepoId} branch={Branch}", row.GitRepositoryId, targetBranch);
                        return new GitOpResult { Ok = false, Message = $"push failed: {ex.Message}", Branch = targetBranch };
                    }
                }
            }

            // SSH path — close the repo handle before invoking git CLI to
            // avoid Windows file-locking on the .git/index file.
            if (string.IsNullOrEmpty(auth.PrivateKeyPem))
                return new GitOpResult { Ok = false, Message = "SSH URL needs a key credential", Branch = targetBranch };
            var res = await SshGitCli.PushAsync(repoPath, targetBranch, auth.PrivateKeyPem, auth.Passphrase, _logger, ct);
            using var repoAfter = new Repository(repoPath);
            return new GitOpResult
            {
                Ok = res.Ok,
                Message = res.Message,
                Branch = targetBranch,
                CommitSha = repoAfter.Branches[targetBranch]?.Tip?.Sha,
            };
        });

    public Task<ActionResult<GitBranchesResponse>> ListBranchesAsync(Guid id, CancellationToken ct) =>
        WithRepoAsync<GitBranchesResponse>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            // Best-effort fetch so newly-created remote branches show up.
            // Failures are tolerated — we'll still report the local view.
            await TryFetchAsync(repoPath, auth, ct);
            using var repo = new Repository(repoPath);

            var current = repo.Head?.FriendlyName ?? row.DefaultBranch;
            var local = repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName);
            var remote = repo.Branches
                .Where(b => b.IsRemote && !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
                .Select(b => b.FriendlyName.StartsWith("origin/", StringComparison.Ordinal)
                    ? b.FriendlyName["origin/".Length..]
                    : b.FriendlyName);
            var all = local.Concat(remote)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return new GitBranchesResponse { Current = current, Branches = all };
        });

    public Task<ActionResult<GitOpResult>> CheckoutAsync(Guid id, string branch, CancellationToken ct) =>
        WithRepoAsync<GitOpResult>(id, ct, async (row, repoPath, auth) =>
        {
            if (string.IsNullOrWhiteSpace(branch))
                return new GitOpResult { Ok = false, Message = "branch is required" };
            await EnsureClonedAsync(row, repoPath, auth, ct);
            await TryFetchAsync(repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            CheckoutOrCreate(repo, branch);
            return new GitOpResult { Ok = true, Message = "checked out", Branch = branch };
        });

    public Task<ActionResult<GitListFilesResponse>> ListFilesAsync(
        Guid id, string? path, string? @ref, CancellationToken ct) =>
        WithRepoAsync<GitListFilesResponse>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            var refName = string.IsNullOrWhiteSpace(@ref) ? (repo.Head?.FriendlyName ?? row.DefaultBranch) : @ref!;
            var commit = ResolveCommit(repo, refName) ?? repo.Head?.Tip;
            if (commit is null)
                return new GitListFilesResponse { Ref = refName, Path = path ?? string.Empty, Entries = new() };

            var rel = NormalizePath(path);
            var tree = commit.Tree;
            if (!string.IsNullOrEmpty(rel))
            {
                var node = tree[rel];
                if (node is null || node.TargetType != TreeEntryTargetType.Tree)
                    return new GitListFilesResponse { Ref = refName, Path = rel, Entries = new() };
                tree = (Tree)node.Target;
            }

            var entries = tree
                .Where(t => t.TargetType == TreeEntryTargetType.Blob || t.TargetType == TreeEntryTargetType.Tree)
                .Select(t => new GitFileEntry
                {
                    Path = string.IsNullOrEmpty(rel) ? t.Name : $"{rel}/{t.Name}",
                    Type = t.TargetType == TreeEntryTargetType.Tree ? "tree" : "blob",
                    Size = t.TargetType == TreeEntryTargetType.Blob ? ((Blob)t.Target).Size : 0,
                })
                .OrderBy(e => e.Type != "tree") // dirs first
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .ToList();

            return new GitListFilesResponse { Ref = refName, Path = rel, Entries = entries };
        });

    public Task<ActionResult<GitReadFileResponse>> ReadFileAsync(
        Guid id, string path, string? @ref, CancellationToken ct) =>
        WithRepoAsync<GitReadFileResponse>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            var rel = NormalizePath(path);
            if (string.IsNullOrEmpty(rel))
                throw new BadHttpRequestException("path is required");

            var refName = string.IsNullOrWhiteSpace(@ref) ? (repo.Head?.FriendlyName ?? row.DefaultBranch) : @ref!;
            var commit = ResolveCommit(repo, refName) ?? repo.Head?.Tip
                ?? throw new BadHttpRequestException($"ref '{refName}' not found");
            var entry = commit[rel] ?? throw new FileNotFoundException($"path '{rel}' not in tree");
            if (entry.TargetType != TreeEntryTargetType.Blob)
                throw new BadHttpRequestException($"path '{rel}' is not a file");

            var blob = (Blob)entry.Target;
            if (blob.Size > _maxFileBytes)
                throw new BadHttpRequestException($"file exceeds max size ({_maxFileBytes} bytes)");
            var isBinary = blob.IsBinary;
            string content;
            if (isBinary)
            {
                using var stream = blob.GetContentStream();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                content = Convert.ToBase64String(ms.ToArray());
            }
            else
            {
                content = blob.GetContentText();
            }
            return new GitReadFileResponse
            {
                Path = rel,
                Ref = refName,
                Content = content,
                Size = blob.Size,
                IsBinary = isBinary,
            };
        });

    public Task<ActionResult<GitOpResult>> WriteFileAsync(
        Guid id, GitWriteFileRequest req, CancellationToken ct) =>
        WithRepoAsync<GitOpResult>(id, ct, async (row, repoPath, auth) =>
        {
            var rel = NormalizePath(req.Path);
            if (string.IsNullOrEmpty(rel))
                return new GitOpResult { Ok = false, Message = "path is required" };
            if (string.IsNullOrWhiteSpace(req.CommitMessage))
                return new GitOpResult { Ok = false, Message = "commit_message is required" };
            var bytes = Encoding.UTF8.GetByteCount(req.Content ?? string.Empty);
            if (bytes > _maxFileBytes)
                return new GitOpResult { Ok = false, Message = $"content exceeds max size ({_maxFileBytes} bytes)" };

            await EnsureClonedAsync(row, repoPath, auth, ct);
            string commitSha;
            string? branchName;
            using (var repo = new Repository(repoPath))
            {
                if (!string.IsNullOrWhiteSpace(req.Branch))
                    CheckoutOrCreate(repo, req.Branch!);

                var absolute = Path.GetFullPath(Path.Combine(repoPath, rel));
                if (!absolute.StartsWith(Path.GetFullPath(repoPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && absolute != Path.GetFullPath(repoPath))
                    return new GitOpResult { Ok = false, Message = "path escapes repository root" };
                var dir = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(absolute, req.Content ?? string.Empty, Encoding.UTF8, ct);

                Commands.Stage(repo, rel);
                var sig = BuildSignature(req.AuthorName, req.AuthorEmail);

                var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
                if (!status.IsDirty)
                    return new GitOpResult { Ok = true, Message = "no changes", Branch = repo.Head?.FriendlyName };

                var commit = repo.Commit(req.CommitMessage, sig, sig);
                commitSha = commit.Sha;
                branchName = repo.Head?.FriendlyName;
            }

            string? pushMsg = null;
            if (req.Push)
            {
                var pushRes = await PushCurrentBranchAsync(repoPath, auth, ct);
                pushMsg = pushRes.Ok ? " · pushed" : $" · push failed: {pushRes.Message}";
            }

            return new GitOpResult
            {
                Ok = true,
                Message = $"committed{pushMsg}",
                CommitSha = commitSha,
                Branch = branchName,
            };
        });

    public Task<ActionResult<GitOpResult>> CommitAsync(
        Guid id, GitCommitRequest req, CancellationToken ct) =>
        WithRepoAsync<GitOpResult>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            if (string.IsNullOrWhiteSpace(req.CommitMessage))
                return new GitOpResult { Ok = false, Message = "commit_message is required" };

            if (req.Paths is { Count: > 0 })
                foreach (var p in req.Paths)
                {
                    var rel = NormalizePath(p);
                    if (!string.IsNullOrEmpty(rel)) Commands.Stage(repo, rel);
                }
            else
                Commands.Stage(repo, "*");

            var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
            if (!status.IsDirty)
                return new GitOpResult { Ok = true, Message = "no changes", Branch = repo.Head?.FriendlyName };

            var sig = BuildSignature(req.AuthorName, req.AuthorEmail);
            var commit = repo.Commit(req.CommitMessage, sig, sig);
            var commitSha = commit.Sha;
            var branchName = repo.Head?.FriendlyName;
            // Release the libgit2 handle before invoking the SSH CLI —
            // Windows holds an exclusive lock on .git/index/packed-refs
            // for the lifetime of `repo`, which would block git push.
            repo.Dispose();
            string? pushMsg = null;
            if (req.Push)
            {
                var pushRes = await PushCurrentBranchAsync(repoPath, auth, ct);
                pushMsg = pushRes.Ok ? " · pushed" : $" · push failed: {pushRes.Message}";
            }
            return new GitOpResult
            {
                Ok = true,
                Message = $"committed{pushMsg}",
                CommitSha = commitSha,
                Branch = branchName,
            };
        });

    public Task<ActionResult<GitDiffResponse>> DiffAsync(
        Guid id, string? @from, string? to, string? path, CancellationToken ct) =>
        WithRepoAsync<GitDiffResponse>(id, ct, async (row, repoPath, auth) =>
        {
            await EnsureClonedAsync(row, repoPath, auth, ct);
            using var repo = new Repository(repoPath);
            // Default: working tree vs HEAD. Useful for "what's about to be committed".
            Patch patch;
            string fromRef, toRef;
            if (string.IsNullOrWhiteSpace(@from) && string.IsNullOrWhiteSpace(to))
            {
                fromRef = "HEAD";
                toRef = "WORKING";
                var paths = string.IsNullOrEmpty(path) ? null : new[] { NormalizePath(path) };
                patch = repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.WorkingDirectory, paths);
            }
            else
            {
                fromRef = string.IsNullOrWhiteSpace(@from) ? "HEAD" : @from!;
                toRef = string.IsNullOrWhiteSpace(to) ? (repo.Head?.FriendlyName ?? row.DefaultBranch) : to!;
                var fromCommit = ResolveCommit(repo, fromRef)
                    ?? throw new BadHttpRequestException($"ref '{fromRef}' not found");
                var toCommit = ResolveCommit(repo, toRef)
                    ?? throw new BadHttpRequestException($"ref '{toRef}' not found");
                var paths = string.IsNullOrEmpty(path) ? null : new[] { NormalizePath(path) };
                patch = repo.Diff.Compare<Patch>(fromCommit.Tree, toCommit.Tree, paths);
            }
            return await Task.FromResult(new GitDiffResponse
            {
                From = fromRef,
                To = toRef,
                Path = path,
                Patch = patch.Content ?? string.Empty,
            });
        });

    // ─── helpers ────────────────────────────────────────────────────

    private async Task<GitRepoModel?> Find(Guid id, CancellationToken ct) =>
        await _repos.GetByIdAsync(id, ct: ct);

    private async Task<ActionResult<T>> WithRepoAsync<T>(
        Guid id, CancellationToken ct,
        Func<GitRepoModel, string, GitAuthBundle, Task<T>> body)
    {
        var row = await Find(id, ct);
        if (row is null) return Err<T>(404, "git repository not found");

        var localPath = ComputeLocalPath(row);
        if (row.LocalPath != localPath)
        {
            row.LocalPath = localPath;
            await _repos.SaveChangesAsync(ct);
        }

        var auth = await BuildAuthBundleAsync(row, ct);
        var sem = _locks.GetOrAdd(row.GitRepositoryId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var result = await body(row, localPath, auth);
            return result;
        }
        catch (BadHttpRequestException bex)
        {
            return Err<T>(400, bex.Message);
        }
        catch (FileNotFoundException fnf)
        {
            return Err<T>(404, fnf.Message);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogWarning(ex, "git.op.failed repo_id={RepoId}", row.GitRepositoryId);
            return Err<T>(500, $"git operation failed: {ex.Message}");
        }
        finally
        {
            sem.Release();
        }
    }

    // Auth resolution. Selects the transport based on URL scheme + the
    // linked Credential's auth_method:
    //   • SSH URL + auth_method=key  → CLI transport (libgit2 has no ssh)
    //   • HTTPS URL + token (password) → libgit2 with username/password
    //   • Anything else                → unauthenticated (clone-from-public)
    private async Task<GitAuthBundle> BuildAuthBundleAsync(GitRepoModel row, CancellationToken ct)
    {
        var ssh = IsSshUrl(row.Url);
        if (row.AuthCredentialId is null)
        {
            return new GitAuthBundle(
                IsSsh: ssh,
                CredentialsHandler: (_, _, _) => new DefaultCredentials(),
                PrivateKeyPem: null,
                Passphrase: null);
        }
        var cred = await _credentials.GetByIdAsync(row.AuthCredentialId.Value, tracking: false, ct: ct);
        if (cred is null)
        {
            return new GitAuthBundle(
                IsSsh: ssh,
                CredentialsHandler: (_, _, _) => new DefaultCredentials(),
                PrivateKeyPem: null,
                Passphrase: null);
        }

        if (ssh && string.Equals(cred.AuthMethod, "key", StringComparison.OrdinalIgnoreCase))
        {
            var pem = _crypto.Decrypt(cred.EncryptedPrivateKey) ?? string.Empty;
            var passphrase = _crypto.Decrypt(cred.EncryptedKeyPassphrase);
            return new GitAuthBundle(
                IsSsh: true,
                CredentialsHandler: (_, _, _) => new DefaultCredentials(),
                PrivateKeyPem: pem,
                Passphrase: passphrase);
        }

        // HTTPS+PAT (or token-style — username may be the PAT scope label).
        var token = _crypto.Decrypt(cred.EncryptedPassword) ?? string.Empty;
        var username = string.IsNullOrWhiteSpace(cred.Username) ? "git" : cred.Username!;
        CredentialsHandler handler = (_, _, _) => new UsernamePasswordCredentials
        {
            Username = username,
            Password = token,
        };
        return new GitAuthBundle(
            IsSsh: ssh,
            CredentialsHandler: handler,
            PrivateKeyPem: null,
            Passphrase: null);
    }

    internal sealed record GitAuthBundle(
        bool IsSsh,
        CredentialsHandler CredentialsHandler,
        string? PrivateKeyPem,
        string? Passphrase);

    private async Task EnsureClonedAsync(
        GitRepoModel row, string repoPath, GitAuthBundle auth, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(repoPath, ".git"))) return;

        _logger.LogInformation(
            "git.clone.start repo_id={RepoId} url={Url} path={Path} ssh={Ssh}",
            row.GitRepositoryId, row.Url, repoPath, auth.IsSsh);

        if (auth.IsSsh)
        {
            if (string.IsNullOrEmpty(auth.PrivateKeyPem))
                throw new BadHttpRequestException(
                    "SSH URL requires a credential with auth_method='key' and a private key");
            // git clone creates the target dir itself; remove any
            // half-finished attempt so it doesn't fail with "destination
            // path already exists and is not an empty directory".
            if (Directory.Exists(repoPath))
            {
                try { Directory.Delete(repoPath, recursive: true); } catch { /* will let git fail clearly */ }
            }
            var res = await SshGitCli.CloneAsync(
                row.Url, repoPath, row.DefaultBranch,
                auth.PrivateKeyPem, auth.Passphrase, _logger, ct);
            if (!res.Ok) throw new LibGit2SharpException("ssh clone failed: " + res.Message);
        }
        else
        {
            Directory.CreateDirectory(repoPath);
            await Task.Run(() => Repository.Clone(row.Url, repoPath, new CloneOptions
            {
                BranchName = row.DefaultBranch,
                FetchOptions = { CredentialsProvider = auth.CredentialsHandler },
            }), ct);
        }
        row.LastFetchedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await _repos.SaveChangesAsync(ct);
    }

    private static void CheckoutOrCreate(Repository repo, string branchName)
    {
        var local = repo.Branches[branchName];
        if (local is not null)
        {
            Commands.Checkout(repo, local);
            return;
        }
        var remote = repo.Branches[$"origin/{branchName}"];
        if (remote is not null)
        {
            var tracking = repo.CreateBranch(branchName, remote.Tip);
            repo.Branches.Update(tracking,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = $"refs/heads/{branchName}");
            Commands.Checkout(repo, tracking);
            return;
        }
        // Brand-new branch off current HEAD.
        var fresh = repo.CreateBranch(branchName);
        Commands.Checkout(repo, fresh);
    }

    private static Commit? ResolveCommit(Repository repo, string refName)
    {
        var local = repo.Branches[refName];
        if (local is not null) return local.Tip;
        var remote = repo.Branches[$"origin/{refName}"];
        if (remote is not null) return remote.Tip;
        // Fall back to direct lookup (sha or tag).
        return repo.Lookup<Commit>(refName);
    }

    private Signature BuildSignature(string? name, string? email)
    {
        var who = string.IsNullOrWhiteSpace(name)
            ? (string.IsNullOrWhiteSpace(_caller.Username) ? _defaultAuthorName : _caller.Username!)
            : name!;
        var mail = string.IsNullOrWhiteSpace(email) ? _defaultAuthorEmail : email!;
        return new Signature(who, mail, DateTimeOffset.UtcNow);
    }

    private string ComputeLocalPath(GitRepoModel row) =>
        Path.Combine(_gitRoot, row.GitRepositoryId.ToString("N"));

    // Best-effort fetch — used by branch listing / checkout to pick up
    // remote changes silently. Failures are logged but never thrown:
    // the caller already has a usable local view.
    /// <summary>
    /// Pushes a local branch, publishing it and recording its upstream the first
    /// time. Returns true when the branch had no upstream and now has one.
    /// </summary>
    /// <remarks>
    /// <c>Network.Push(Branch, …)</c> requires the branch to ALREADY track a
    /// remote one and otherwise throws
    /// <c>"The branch 'x' that you are trying to push does not track an upstream
    /// branch."</c> — so a branch created here could be checked out, written to
    /// and committed, and then never published. Writes to an already-published
    /// branch worked throughout, which is what made pushing look like it worked.
    ///
    /// Pushing by refspec is what <c>git push -u origin &lt;branch&gt;</c> does:
    /// send <c>refs/heads/x:refs/heads/x</c>, then record the upstream so the
    /// next push, the ahead/behind counts and a pull all resolve against it.
    ///
    /// HTTPS only. The SSH path shells out to the git CLI, which sets the
    /// upstream itself.
    /// </remarks>
    internal static bool PushBranch(Repository repo, Branch localBranch, PushOptions options)
    {
        if (localBranch.IsTracking)
        {
            repo.Network.Push(localBranch, options);
            return false;
        }

        var remote = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault()
            ?? throw new BadHttpRequestException(
                "this repository has no remote configured, so a branch cannot be published");

        repo.Network.Push(remote, $"{localBranch.CanonicalName}:{localBranch.CanonicalName}", options);

        // Recorded only after the push succeeds. Writing the upstream first would
        // leave a branch claiming to track something the remote does not have.
        repo.Branches.Update(localBranch,
            b => b.Remote = remote.Name,
            b => b.UpstreamBranch = localBranch.CanonicalName);
        return true;
    }

    private async Task TryFetchAsync(string repoPath, GitAuthBundle auth, CancellationToken ct)
    {
        if (auth.IsSsh)
        {
            if (string.IsNullOrEmpty(auth.PrivateKeyPem)) return;
            var res = await SshGitCli.FetchAsync(repoPath, auth.PrivateKeyPem, auth.Passphrase, _logger, ct);
            if (!res.Ok)
                _logger.LogDebug("git.fetch.ssh.skipped reason={Reason}", res.Message);
            return;
        }
        try
        {
            using var repo = new Repository(repoPath);
            Commands.Fetch(repo, "origin", Array.Empty<string>(),
                new FetchOptions { CredentialsProvider = auth.CredentialsHandler }, null);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogDebug(ex, "git.fetch.https.skipped");
        }
    }

    // Push the current HEAD branch using the right transport. Returns
    // (false, error message) on failure so callers can include it in
    // their user-facing GitOpResult.
    private async Task<SshGitResult> PushCurrentBranchAsync(
        string repoPath, GitAuthBundle auth, CancellationToken ct)
    {
        string branchName;
        using (var repo = new Repository(repoPath))
        {
            branchName = repo.Head?.FriendlyName ?? string.Empty;
            if (string.IsNullOrEmpty(branchName))
                return new SshGitResult(false, "no current branch");

            if (!auth.IsSsh)
            {
                try
                {
                    var localBranch = repo.Branches[branchName] ?? repo.Head;
                    // Same rule as the push operation: a branch created here has no
                    // upstream until its first push, and this is the write-and-push
                    // path.
                    var published = PushBranch(
                        repo, localBranch, new PushOptions { CredentialsProvider = auth.CredentialsHandler });
                    return new SshGitResult(true, published ? "pushed and set upstream" : "pushed");
                }
                catch (LibGit2SharpException ex)
                {
                    return new SshGitResult(false, ex.Message);
                }
            }
        }
        if (string.IsNullOrEmpty(auth.PrivateKeyPem))
            return new SshGitResult(false, "SSH URL needs a key credential");
        return await SshGitCli.PushAsync(repoPath, branchName, auth.PrivateKeyPem, auth.Passphrase, _logger, ct);
    }

    // internal (InternalsVisibleTo) so the B-06 path-traversal guard can be
    // asserted directly. Rejects `..`/`.` segments; normalizes separators and
    // strips leading/trailing slashes.
    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Replace('\\', '/').Trim('/');
        // Reject absolute and traversal fragments outright.
        if (p.Split('/').Any(seg => seg == ".." || seg == "."))
            return string.Empty;
        return p;
    }

    private static bool IsAllowedUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        // HTTPS only — http:// would send the PAT in cleartext as
        // basic-auth on every fetch/push and gives no transport
        // protection against MITM on the clone-from path. SSH is
        // accepted as the alternate authenticated transport.
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || IsSshUrl(url);
    }

    // Matches both forms commonly accepted by git:
    //   • OpenSSH-style scp:    git@github.com:org/repo.git
    //   • Explicit ssh URI:     ssh://git@github.com/org/repo.git
    // Anything else (file://, git://, smart-http on a non-standard scheme)
    // is rejected by IsAllowedUrl above.
    internal static bool IsSshUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
        // scp-style: <user>@<host>:<path>. Must have an "@" before the
        // first "/" or ":" path separator, and a ":" with a non-empty
        // path after it. Reject https:// (the "://" would otherwise
        // hit the @ parser when URLs include credentials inline).
        if (url.Contains("://", StringComparison.Ordinal)) return false;
        var at = url.IndexOf('@');
        if (at <= 0) return false;
        var colon = url.IndexOf(':', at + 1);
        return colon > at && colon < url.Length - 1;
    }

    private static GitRepositoryResponse ToResponse(GitRepoModel r) => new()
    {
        GitRepositoryId = r.GitRepositoryId,
        Name = r.Name,
        Url = r.Url,
        DefaultBranch = r.DefaultBranch,
        AuthCredentialId = r.AuthCredentialId,
        Description = r.Description,
        LocalPath = r.LocalPath,
        LastFetchedAt = r.LastFetchedAt,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static ActionResult<T> Err<T>(int status, string error) =>
        new ObjectResult(new { error }) { StatusCode = status };
}
