using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Git;

// Façade over a local working copy of a registered Git repository. The
// service owns the on-disk layout (one folder per repo under
// <GitRoot>/<repoId>) and serializes calls per repository so two
// simultaneous writes don't trash the index.
//
// Authorization (admin vs viewer) is enforced at the controller layer
// — the service trusts its caller.
public interface IGitService
{
    Task<ActionResult<ListResponse<GitRepositoryResponse>>> ListAsync(
        int limit, int offset, CancellationToken ct);

    Task<ActionResult<GitRepositoryResponse>> GetAsync(Guid id, CancellationToken ct);

    Task<ActionResult<GitRepositoryResponse>> CreateAsync(
        CreateGitRepository dto, CancellationToken ct);

    Task<ActionResult<GitRepositoryResponse>> UpdateAsync(
        Guid id, UpdateGitRepository dto, CancellationToken ct);

    Task<ActionResult<GitRepositoryResponse>> DeleteAsync(Guid id, CancellationToken ct);

    Task<ActionResult<GitOpResult>> PullAsync(Guid id, string? branch, CancellationToken ct);

    Task<ActionResult<GitOpResult>> PushAsync(Guid id, string? branch, CancellationToken ct);

    Task<ActionResult<GitBranchesResponse>> ListBranchesAsync(Guid id, CancellationToken ct);

    Task<ActionResult<GitOpResult>> CheckoutAsync(Guid id, string branch, CancellationToken ct);

    Task<ActionResult<GitListFilesResponse>> ListFilesAsync(
        Guid id, string? path, string? @ref, CancellationToken ct);

    Task<ActionResult<GitReadFileResponse>> ReadFileAsync(
        Guid id, string path, string? @ref, CancellationToken ct);

    Task<ActionResult<GitOpResult>> WriteFileAsync(
        Guid id, GitWriteFileRequest req, CancellationToken ct);

    Task<ActionResult<GitOpResult>> CommitAsync(
        Guid id, GitCommitRequest req, CancellationToken ct);

    Task<ActionResult<GitDiffResponse>> DiffAsync(
        Guid id, string? @from, string? to, string? path, CancellationToken ct);
}
