using System.Data;
using System.Text.Json;
using Dapper;
using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using JobModel = flow_weaver_backend.Models.Job;

namespace flow_weaver_backend.Services.Engine;

// Raw-SQL implementation of the job queue. Uses the AppDbContext's
// underlying Npgsql connection so we inherit the configured connection
// string + data-source without duplicating wiring.
//
// Column identifiers are quoted PascalCase because the EF Core schema
// (see AppDbContext) maps properties to columns 1:1 without renaming.
// Unquoted `status` / `tag` would be folded to lowercase by Postgres and
// fail to match "Status" / "Tag".
public class QueueRepository : IQueueRepository
{
    // Lease length matches the longest reasonable step timeout. Pick high
    // enough that live workers never lose claims they are actually running,
    // low enough that a crashed worker's jobs come back within a minute or
    // two. Kept here so Claim + Reclaim agree on the number.
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly ILogger<QueueRepository> _logger;

    public QueueRepository(AppDbContext db, ILogger<QueueRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // The critical section of the worker loop. A single UPDATE both picks
    // the next job (inner SELECT with FOR UPDATE SKIP LOCKED) and marks it
    // claimed, so two workers racing on the same row will never both see
    // it — the second one skips that row and moves on to the next eligible
    // job.
    //
    // Ordering: priority DESC first (higher-priority jobs jump the queue),
    // then CreatedAt ASC (FIFO within a priority band).
    private const string ClaimSql = """
        UPDATE jobs
        SET "Status" = 'claimed',
            "ClaimedBy" = @workerId,
            "ClaimedAt" = NOW(),
            "LeaseExpiresAt" = NOW() + (@leaseSeconds::text || ' seconds')::interval,
            "UpdatedAt" = NOW()
        WHERE "JobId" = (
            SELECT "JobId" FROM jobs
            WHERE "Status" = 'pending'
              AND "IsActive" = TRUE
              AND "Tag" = ANY(@tags)
            ORDER BY "Priority" DESC, "CreatedAt" ASC
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        RETURNING
            "JobId", "Type", "Payload", "Tag", "Priority", "Status",
            "ClaimedBy", "ClaimedAt", "CompletedAt", "LeaseExpiresAt",
            "IsActive", "CreatedAt", "UpdatedAt";
        """;

    // Returns every claim that outlived its lease back to 'pending' so
    // another worker can pick them up. Guarded by Status = 'claimed' so a
    // race with a legitimate worker that just completed does not resurrect
    // a terminal job.
    private const string ReclaimSql = """
        UPDATE jobs
        SET "Status" = 'pending',
            "ClaimedBy" = NULL,
            "ClaimedAt" = NULL,
            "LeaseExpiresAt" = NULL,
            "UpdatedAt" = NOW()
        WHERE "Status" = 'claimed'
          AND "LeaseExpiresAt" IS NOT NULL
          AND "LeaseExpiresAt" < NOW();
        """;

    // Bumps LeaseExpiresAt forward only if the claim still belongs to the
    // calling worker. The ClaimedBy guard prevents a stale heartbeat (e.g.
    // from a paused worker that briefly resumed) from clobbering a fresh
    // claim that another worker already took over after reclaim ran.
    private const string RenewLeaseSql = """
        UPDATE jobs
        SET "LeaseExpiresAt" = NOW() + (@leaseSeconds::text || ' seconds')::interval,
            "UpdatedAt" = NOW()
        WHERE "JobId" = @jobId
          AND "Status" = 'claimed'
          AND "ClaimedBy" = @workerId;
        """;

    private const string CompleteSql = """
        UPDATE jobs
        SET "Status" = 'completed',
            "CompletedAt" = NOW(),
            "UpdatedAt" = NOW()
        WHERE "JobId" = @jobId
          AND "Status" = 'claimed';
        """;

    private const string FailSql = """
        UPDATE jobs
        SET "Status" = 'failed',
            "CompletedAt" = NOW(),
            "UpdatedAt" = NOW()
        WHERE "JobId" = @jobId
          AND "Status" = 'claimed';
        """;

    // Two UPDATEs because the orchestrator job carries the run id directly
    // in its payload while per-step jobs carry only step_run_id and have to
    // be joined back through step_runs. Status -> 'failed' (not a new
    // 'cancelled' value) so existing reclaim/sweeper logic that filters on
    // terminal statuses keeps working without a schema change.
    private const string CancelOrchestratorJobsSql = """
        UPDATE jobs
        SET "Status" = 'failed',
            "CompletedAt" = NOW(),
            "UpdatedAt" = NOW()
        WHERE "Status" = 'pending'
          AND "Type" = 'workflow_run'
          AND ("Payload"->>'workflow_run_id')::uuid = @runId;
        """;

    private const string CancelStepJobsSql = """
        UPDATE jobs
        SET "Status" = 'failed',
            "CompletedAt" = NOW(),
            "UpdatedAt" = NOW()
        WHERE "Status" = 'pending'
          AND "Type" = 'step'
          AND ("Payload"->>'step_run_id')::uuid IN (
              SELECT "StepRunId" FROM step_runs WHERE "WorkflowRunId" = @runId
          );
        """;

    // ::jsonb cast: the parameter travels as text (see JsonElementTypeHandler)
    // and Postgres converts it to jsonb at insert time. Without the cast the
    // server rejects the value with "column is of type jsonb but expression
    // is of type text".
    private const string EnqueueSql = """
        INSERT INTO jobs (
            "JobId", "Type", "Payload", "Tag", "Priority", "Status",
            "IsActive", "CreatedAt", "UpdatedAt"
        ) VALUES (
            @jobId, @type, @payload::jsonb, @tag, @priority, 'pending',
            TRUE, NOW(), NOW()
        )
        RETURNING "JobId";
        """;

    public async Task<JobModel?> ClaimAsync(string[] tags, string workerId, CancellationToken ct)
    {
        try
        {
            var conn = await OpenAsync(ct);
            var leaseSeconds = (int)DefaultLease.TotalSeconds;
            var command = new CommandDefinition(
                ClaimSql, new { tags, workerId, leaseSeconds }, cancellationToken: ct);
            var job = await conn.QuerySingleOrDefaultAsync<JobModel>(command);
            if (job is null)
            {
                _logger.LogDebug("engine.queue.empty worker_id={WorkerId} tag_count={TagCount}",
                    workerId, tags.Length);
                return null;
            }

            _logger.LogDebug(
                "engine.queue.claim.ok job_id={JobId} worker_id={WorkerId} tag={Tag} priority={Priority}",
                job.JobId, workerId, job.Tag, job.Priority);
            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "engine.queue.error op=claim worker_id={WorkerId}", workerId);
            throw;
        }
    }

    public async Task<int> CancelPendingByRunAsync(Guid workflowRunId, CancellationToken ct)
    {
        try
        {
            var conn = await OpenAsync(ct);
            var orchCmd = new CommandDefinition(
                CancelOrchestratorJobsSql, new { runId = workflowRunId }, cancellationToken: ct);
            var orchestratorCount = await conn.ExecuteAsync(orchCmd);

            var stepCmd = new CommandDefinition(
                CancelStepJobsSql, new { runId = workflowRunId }, cancellationToken: ct);
            var stepCount = await conn.ExecuteAsync(stepCmd);

            var total = orchestratorCount + stepCount;
            if (total > 0)
                _logger.LogDebug(
                    "engine.queue.dequeue.ok op=cancel_by_run workflow_run_id={WorkflowRunId} orchestrator={Orch} step={Step}",
                    workflowRunId, orchestratorCount, stepCount);
            return total;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "engine.queue.error op=cancel_by_run workflow_run_id={WorkflowRunId}", workflowRunId);
            throw;
        }
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string workerId, int leaseSeconds, CancellationToken ct)
    {
        try
        {
            var conn = await OpenAsync(ct);
            var command = new CommandDefinition(
                RenewLeaseSql,
                new { jobId, workerId, leaseSeconds },
                cancellationToken: ct);
            var rows = await conn.ExecuteAsync(command);
            if (rows == 0)
                _logger.LogWarning(
                    "engine.queue.renew_lease.lost job_id={JobId} worker_id={WorkerId}",
                    jobId, workerId);
            else
                _logger.LogDebug(
                    "engine.queue.renew_lease.ok job_id={JobId} worker_id={WorkerId} lease_seconds={LeaseSeconds}",
                    jobId, workerId, leaseSeconds);
            return rows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "engine.queue.error op=renew_lease job_id={JobId}", jobId);
            throw;
        }
    }

    public async Task<int> ReclaimExpiredAsync(CancellationToken ct)
    {
        try
        {
            var conn = await OpenAsync(ct);
            var command = new CommandDefinition(ReclaimSql, cancellationToken: ct);
            var count = await conn.ExecuteAsync(command);
            if (count > 0)
                _logger.LogDebug("engine.queue.dequeue.ok op=reclaim reclaimed={Reclaimed}", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "engine.queue.error op=reclaim");
            throw;
        }
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            var conn = await OpenAsync(ct);
            var command = new CommandDefinition(CompleteSql, new { jobId }, cancellationToken: ct);
            await conn.ExecuteAsync(command);
            _logger.LogDebug("engine.queue.dequeue.ok op=complete job_id={JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "engine.queue.error op=complete job_id={JobId}", jobId);
            throw;
        }
    }

    public async Task FailAsync(Guid jobId, string error, CancellationToken ct)
    {
        // `error` is accepted for the interface contract but not persisted:
        // the jobs table has no error column, and step-level error detail
        // lives on step_runs.Error where it belongs.
        _ = error;

        try
        {
            var conn = await OpenAsync(ct);
            var command = new CommandDefinition(FailSql, new { jobId }, cancellationToken: ct);
            await conn.ExecuteAsync(command);
            _logger.LogDebug("engine.queue.dequeue.ok op=fail job_id={JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "engine.queue.error op=fail job_id={JobId}", jobId);
            throw;
        }
    }

    public async Task<Guid> EnqueueAsync(
        string type,
        JsonElement payload,
        string tag,
        int priority,
        CancellationToken ct)
    {
        try
        {
            var conn = await OpenAsync(ct);
            var jobId = Guid.NewGuid();
            var command = new CommandDefinition(
                EnqueueSql,
                new { jobId, type, payload, tag, priority },
                cancellationToken: ct);
            var inserted = await conn.ExecuteScalarAsync<Guid>(command);
            _logger.LogDebug(
                "engine.queue.enqueue.ok job_id={JobId} type={Type} tag={Tag} priority={Priority}",
                inserted, type, tag, priority);
            return inserted;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "engine.queue.error op=enqueue type={Type} tag={Tag}",
                type, tag);
            throw;
        }
    }

    private async Task<IDbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await _db.Database.OpenConnectionAsync(ct);
        return conn;
    }
}
