using System.Net;
using System.Text.Json;
using flow_weaver_backend.BackgroundServices;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// A snippet's retry_policy (execution/SPEC.md §3) used to be stored, translated and exported,
// and then never applied: RetryPolicyExecutor was injected into the orchestrator and never
// called, so a step with `max_retries: 3` failed on its first transient error. These pin the
// behaviour end to end — the gate (handler-marked `retryable`), the loop, the handlers that
// mark failures, and the worker that runs the attempts.
public class StepRetryTests
{
    private static RetryPolicyExecutor Retry() => new(NullLogger<RetryPolicyExecutor>.Instance);

    private static RetryPolicy Policy(int maxRetries, double initial = 5, string backoff = "exponential") =>
        new() { MaxRetries = maxRetries, InitialDelay = initial, Backoff = backoff, MaxDelay = 300 };

    private static SnippetResult Ok() => new() { Success = true, Change = StepChange.Unchanged };

    private static SnippetResult Transient(string error = "connect failed") =>
        new() { Success = false, Change = StepChange.Unchanged, Error = error, Retryable = true };

    private static SnippetResult Final(string error = "HTTP 404") =>
        new() { Success = false, Change = StepChange.Unchanged, Error = error };

    // Replays `results` in order, one per attempt, and records every wait it is asked for.
    private sealed class Script
    {
        private readonly Queue<SnippetResult> _results;
        public int Calls { get; private set; }
        public List<TimeSpan> Waits { get; } = new();
        public bool Continue { get; set; } = true;

        public Script(params SnippetResult[] results) => _results = new(results);

        public Task<SnippetResult> Attempt(CancellationToken _)
        {
            Calls++;
            return Task.FromResult(_results.Dequeue());
        }

        public Task<bool> BeforeRetry(TimeSpan delay, CancellationToken _)
        {
            Waits.Add(delay);
            return Task.FromResult(Continue);
        }
    }

    private static Task<RetryOutcome> Run(RetryPolicy policy, Script s) =>
        Retry().RunAsync(policy, s.Attempt, s.BeforeRetry, CancellationToken.None);

    // ─── RetryPolicyExecutor.RunAsync ───────────────────────────────────

    [Fact]
    public async Task A_retryable_failure_is_retried_with_the_policy_backoff_until_it_succeeds()
    {
        var s = new Script(Transient(), Transient(), Ok());

        var outcome = await Run(Policy(maxRetries: 3), s);

        Assert.True(outcome.Result.Success);
        Assert.Equal(3, outcome.Attempts);
        Assert.False(outcome.Abandoned);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, s.Waits);
        Assert.Contains("attempt 1 failed: connect failed", outcome.RetryLog);
        Assert.Contains("attempt 2 failed", outcome.RetryLog);
    }

    [Fact]
    public async Task Retries_stop_when_the_policy_is_spent_and_the_last_failure_stands()
    {
        var s = new Script(Transient("t1"), Transient("t2"), Transient("t3"));

        var outcome = await Run(Policy(maxRetries: 2), s);

        Assert.False(outcome.Result.Success);
        Assert.Equal("t3", outcome.Result.Error);
        Assert.Equal(3, outcome.Attempts); // the first attempt plus max_retries
        Assert.Equal(2, s.Waits.Count);
    }

    [Fact]
    public async Task A_failure_the_handler_did_not_mark_retryable_is_never_retried()
    {
        var s = new Script(Final());

        var outcome = await Run(Policy(maxRetries: 5), s);

        Assert.Equal(1, outcome.Attempts);
        Assert.Empty(s.Waits);
        Assert.Equal(string.Empty, outcome.RetryLog);
    }

    [Fact]
    public async Task An_attempt_that_changed_something_is_not_repeated_even_if_marked_retryable()
    {
        var changed = new SnippetResult
        {
            Success = false, Change = StepChange.Changed, Error = "half applied", Retryable = true,
        };
        var s = new Script(changed);

        var outcome = await Run(Policy(maxRetries: 5), s);

        Assert.Equal(1, outcome.Attempts);
        Assert.Empty(s.Waits);
    }

    [Fact]
    public async Task A_snippet_without_a_policy_runs_once()
    {
        var s = new Script(Transient());

        var outcome = await Run(new RetryPolicy(), s);

        Assert.Equal(1, outcome.Attempts);
        Assert.Empty(s.Waits);
    }

    [Fact]
    public async Task When_the_step_is_no_longer_wanted_the_retry_is_abandoned()
    {
        var s = new Script(Transient(), Ok()) { Continue = false };

        var outcome = await Run(Policy(maxRetries: 3), s);

        Assert.True(outcome.Abandoned);
        Assert.Equal(1, s.Calls);
    }

    [Fact]
    public void A_negative_delay_waits_zero_instead_of_crashing()
    {
        Assert.Equal(TimeSpan.Zero, Retry().GetDelay(Policy(maxRetries: 1, initial: -10), 1));
    }

    // ─── HttpVerbs: when is an HTTP failure safe to send again ──────────

    [Theory]
    [InlineData("GET", 503, true)]
    [InlineData("GET", 500, true)]
    [InlineData("GET", 504, true)]
    [InlineData("GET", 408, true)]
    [InlineData("GET", 404, false)]
    [InlineData("GET", 401, false)]
    [InlineData("POST", 503, true)]  // refused before processing
    [InlineData("POST", 429, true)]
    [InlineData("POST", 500, false)] // may have been applied
    [InlineData("POST", 502, false)]
    [InlineData("DELETE", 504, false)]
    public void Http_status_is_retryable_only_when_resending_is_safe(string method, int status, bool expected)
    {
        Assert.Equal(expected, HttpVerbs.RetryableStatus(method, status));
    }

    [Fact]
    public void A_write_is_retried_on_transport_failure_only_when_it_never_reached_the_server()
    {
        Assert.True(HttpVerbs.RetryableTransportFailure("POST",
            new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        Assert.True(HttpVerbs.RetryableTransportFailure("POST",
            new HttpRequestException(HttpRequestError.NameResolutionError, "no such host")));
        Assert.False(HttpVerbs.RetryableTransportFailure("POST",
            new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset mid-response")));
        Assert.True(HttpVerbs.RetryableTransportFailure("GET",
            new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset mid-response")));
    }

    // ─── Handlers mark their failures ───────────────────────────────────

    private static IConfiguration AllowInternalUrls => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:AllowInternalUrls"] = "true" })
        .Build();

    private static RestCallHandler Rest(FakeHttpMessageHandler http) => new(
        new FakeHttpClientFactory(http),
        new UrlGuard(AllowInternalUrls, NullLogger<UrlGuard>.Instance),
        new PassThroughSecrets(),
        NullLogger<RestCallHandler>.Instance);

    private static SnippetRequest RestReq(string method) => new()
    {
        StepRunId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        NodeId = "n1",
        SnippetId = null,
        DeviceId = null,
        SnippetType = "rest_call",
        InputPayload = JsonSerializer.SerializeToElement(new { url = "http://api.test/items", method }),
    };

    [Theory]
    [InlineData("GET", HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("POST", HttpStatusCode.BadGateway, false)]
    [InlineData("GET", HttpStatusCode.NotFound, false)]
    public async Task RestCall_marks_an_http_failure_retryable_by_verb_and_status(
        string method, HttpStatusCode status, bool expected)
    {
        var result = await Rest(new FakeHttpMessageHandler(status)).ExecuteAsync(RestReq(method), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Retryable);
    }

    [Fact]
    public async Task RestCall_success_is_never_marked_retryable()
    {
        var result = await Rest(new FakeHttpMessageHandler(HttpStatusCode.OK)).ExecuteAsync(RestReq("GET"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task RestCall_a_refused_connection_is_retryable_even_for_a_write()
    {
        var http = new FakeHttpMessageHandler(_ =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        var result = await Rest(http).ExecuteAsync(RestReq("POST"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Retryable);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)] // the write may have landed after the client gave up
    public async Task RestCall_a_timeout_is_retryable_only_for_a_read(string method, bool expected)
    {
        var http = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("timeout"));

        var result = await Rest(http).ExecuteAsync(RestReq(method), CancellationToken.None);

        Assert.Equal("request timed out", result.Error);
        Assert.Equal(expected, result.Retryable);
    }

    [Theory]
    [InlineData(3, true)]   // connect timeout
    [InlineData(5, true)]   // connect failure
    [InlineData(1, false)]  // parse
    [InlineData(2, false)]  // authentication will not fix itself
    [InlineData(4, false)]  // host key mismatch must never be retried past
    [InlineData(99, false)] // unhandled: commands may have run
    public void Ssh_only_connect_failures_are_retryable(int exitCode, bool expected)
    {
        Assert.Equal(expected, SshHandler.IsRetryableRunnerExit(exitCode));
    }

    // ─── The worker runs the attempts ───────────────────────────────────

    // Hands out exactly one job, then an empty queue; records how the job ended.
    private sealed class OneJobQueue : IQueueRepository
    {
        private Job? _job;
        public TaskCompletionSource<string> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LeaseRenewals { get; private set; }

        public OneJobQueue(Job job) => _job = job;

        public Task<Job?> ClaimAsync(string[] tags, string workerId, CancellationToken ct)
        {
            var job = _job;
            _job = null;
            return Task.FromResult(job);
        }
        public Task CompleteAsync(Guid jobId, CancellationToken ct) { Ended.TrySetResult("completed"); return Task.CompletedTask; }
        public Task FailAsync(Guid jobId, string error, CancellationToken ct) { Ended.TrySetResult("failed"); return Task.CompletedTask; }
        public Task<Guid> EnqueueAsync(string type, JsonElement payload, string tag, int priority, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task<int> ReclaimExpiredAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, int leaseSeconds, CancellationToken ct)
        {
            LeaseRenewals++;
            return Task.FromResult(true);
        }
        public Task<int> CancelPendingByRunAsync(Guid workflowRunId, CancellationToken ct) => Task.FromResult(0);
    }

    // A handler whose attempts are scripted; `onAttempt` runs inside each attempt.
    private sealed class ScriptedHandler : ISnippetHandler
    {
        private readonly Queue<SnippetResult> _results;
        private readonly Func<int, Task>? _onAttempt;
        public int Calls;

        public ScriptedHandler(IEnumerable<SnippetResult> results, Func<int, Task>? onAttempt = null)
        {
            _results = new(results);
            _onAttempt = onAttempt;
        }

        public string Type => "scripted";

        public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            if (_onAttempt is not null) await _onAttempt(n);
            return _results.Dequeue();
        }
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly CancellationTokenSource _stop = new();
        private WorkerHostedService? _worker;
        public ServiceProvider Sp { get; }
        public OneJobQueue Queue { get; }
        public Guid StepRunId { get; } = Guid.NewGuid();
        public Guid RunId { get; } = Guid.NewGuid();

        public WorkerFixture(ScriptedHandler handler, string? retryPolicyJson)
        {
            Queue = new OneJobQueue(new Job
            {
                JobId = Guid.NewGuid(),
                Type = "step",
                Payload = JsonSerializer.SerializeToElement(new { step_run_id = StepRunId }),
            });

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(_db)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<IQueueRepository>(Queue);
            services.AddSingleton<ISnippetHandler>(handler);
            services.AddLogging();
            Sp = services.BuildServiceProvider();

            using var db = NewDb();
            var snippetId = Guid.NewGuid();
            db.Snippets.Add(new Snippet
            {
                SnippetId = snippetId,
                Name = "flaky",
                Type = "scripted",
                TargetMode = "once",
                RetryPolicy = retryPolicyJson is null ? default : JsonDocument.Parse(retryPolicyJson).RootElement.Clone(),
            });
            db.WorkflowRuns.Add(new WorkflowRun { WorkflowRunId = RunId, Status = RunStatus.Running });
            db.StepRuns.Add(new StepRun
            {
                StepRunId = StepRunId,
                WorkflowRunId = RunId,
                NodeId = "n1",
                SnippetId = snippetId,
                Status = StepStatus.Pending,
                InputPayload = JsonSerializer.SerializeToElement(new { }),
                IsActive = true,
            });
            db.SaveChanges();
        }

        public AppDbContext NewDb() => Sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public async Task<(string JobEnd, StepRun Step)> RunAsync()
        {
            _worker = new WorkerHostedService(
                Sp.GetRequiredService<IServiceScopeFactory>(),
                new NoopExecutor(),
                new RetryPolicyExecutor(NullLogger<RetryPolicyExecutor>.Instance),
                Options.Create(new WorkerOptions { PollDelayMs = 10 }),
                NullLogger<WorkerHostedService>.Instance);
            await _worker.StartAsync(_stop.Token);

            var end = await Queue.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var db = NewDb();
            return (end, await db.StepRuns.AsNoTracking().SingleAsync(s => s.StepRunId == StepRunId));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_worker is not null) await _worker.StopAsync(CancellationToken.None);
            await Sp.DisposeAsync();
        }
    }

    private sealed class NoopExecutor : IWorkflowExecutor
    {
        public Task<Guid> EnqueueRunAsync(Guid userId, Guid workflowId, RunWorkflowRequest request, CancellationToken ct, string trigger = "manual")
            => throw new NotSupportedException();
        public Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotSupportedException();
    }

    private const string ThreeRetriesNoWait =
        """{"max_retries":3,"initial_delay_seconds":0,"backoff":"fixed","max_delay_seconds":0}""";

    [Fact]
    public async Task Worker_retries_a_transient_failure_and_records_one_completed_step()
    {
        var handler = new ScriptedHandler([Transient("connection refused"), Ok()]);
        await using var f = new WorkerFixture(handler, ThreeRetriesNoWait);

        var (jobEnd, step) = await f.RunAsync();

        Assert.Equal("completed", jobEnd);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Contains("attempt 1 failed: connection refused", step.Logs);
        Assert.Equal(1, f.Queue.LeaseRenewals); // the claim is extended past each wait
    }

    [Fact]
    public async Task Worker_without_a_policy_fails_the_step_on_the_first_transient_error()
    {
        var handler = new ScriptedHandler([Transient("connection refused"), Ok()]);
        await using var f = new WorkerFixture(handler, retryPolicyJson: null);

        var (jobEnd, step) = await f.RunAsync();

        Assert.Equal("failed", jobEnd);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Equal("connection refused", step.Error);
    }

    [Fact]
    public async Task Worker_does_not_overwrite_a_step_the_orchestrator_already_timed_out()
    {
        WorkerFixture? fixture = null;
        var handler = new ScriptedHandler([Transient(), Ok()], onAttempt: async n =>
        {
            if (n != 1) return;
            // While the first attempt runs, the orchestrator's step timeout fires.
            using var db = fixture!.NewDb();
            var row = await db.StepRuns.SingleAsync(s => s.StepRunId == fixture.StepRunId);
            row.Status = StepStatus.Failed;
            row.Error = "step timed out";
            await db.SaveChangesAsync();
        });
        await using var f = fixture = new WorkerFixture(handler, ThreeRetriesNoWait);

        var (jobEnd, step) = await f.RunAsync();

        Assert.Equal("failed", jobEnd);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("step timed out", step.Error);
    }
}
