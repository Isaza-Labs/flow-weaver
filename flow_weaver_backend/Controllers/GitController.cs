using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// CRUD over GitRepository registrations + file/branch operations on
// the underlying working copy. Read endpoints are open to viewers so
// network engineers can inspect committed configs without admin
// rights; mutation endpoints (write/commit/push/checkout/pull) all
// require Admin.
[ApiController]
[Route("api/[controller]")]
[HasPermission("git.read")]
public class GitController : ControllerBase
{
    private readonly IGitService _service;

    public GitController(IGitService service)
    {
        _service = service;
    }

    [HttpGet("repositories")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<GitRepositoryResponse>>> List(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
        => _service.ListAsync(limit, offset, ct);

    [HttpGet("repositories/{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<GitRepositoryResponse>> Get(Guid id, CancellationToken ct)
        => _service.GetAsync(id, ct);

    [HttpPost("repositories")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitRepositoryResponse>> Create(
        [FromBody] CreateGitRepository dto, CancellationToken ct)
        => _service.CreateAsync(dto, ct);

    [HttpPut("repositories/{id:guid}")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitRepositoryResponse>> Update(
        Guid id, [FromBody] UpdateGitRepository dto, CancellationToken ct)
        => _service.UpdateAsync(id, dto, ct);

    [HttpDelete("repositories/{id:guid}")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitRepositoryResponse>> Delete(Guid id, CancellationToken ct)
        => _service.DeleteAsync(id, ct);

    [HttpPost("repositories/{id:guid}/pull")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitOpResult>> Pull(
        Guid id, [FromQuery] string? branch = null, CancellationToken ct = default)
        => _service.PullAsync(id, branch, ct);

    [HttpPost("repositories/{id:guid}/push")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitOpResult>> Push(
        Guid id, [FromQuery] string? branch = null, CancellationToken ct = default)
        => _service.PushAsync(id, branch, ct);

    [HttpGet("repositories/{id:guid}/branches")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<GitBranchesResponse>> Branches(Guid id, CancellationToken ct)
        => _service.ListBranchesAsync(id, ct);

    [HttpPost("repositories/{id:guid}/checkout")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitOpResult>> Checkout(
        Guid id, [FromQuery] string branch, CancellationToken ct)
        => _service.CheckoutAsync(id, branch, ct);

    [HttpGet("repositories/{id:guid}/files")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<GitListFilesResponse>> Files(
        Guid id, [FromQuery] string? path = null, [FromQuery(Name = "ref")] string? gitRef = null,
        CancellationToken ct = default)
        => _service.ListFilesAsync(id, path, gitRef, ct);

    [HttpGet("repositories/{id:guid}/file")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<GitReadFileResponse>> ReadFile(
        Guid id, [FromQuery] string path, [FromQuery(Name = "ref")] string? gitRef = null,
        CancellationToken ct = default)
        => _service.ReadFileAsync(id, path, gitRef, ct);

    [HttpPut("repositories/{id:guid}/file")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitOpResult>> WriteFile(
        Guid id, [FromBody] GitWriteFileRequest req, CancellationToken ct)
        => _service.WriteFileAsync(id, req, ct);

    [HttpPost("repositories/{id:guid}/commit")]
    [HasPermission("git.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitOpResult>> Commit(
        Guid id, [FromBody] GitCommitRequest req, CancellationToken ct)
        => _service.CommitAsync(id, req, ct);

    [HttpGet("repositories/{id:guid}/diff")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<GitDiffResponse>> Diff(
        Guid id, [FromQuery(Name = "from")] string? @from = null,
        [FromQuery] string? to = null, [FromQuery] string? path = null,
        CancellationToken ct = default)
        => _service.DiffAsync(id, @from, to, path, ct);
}
