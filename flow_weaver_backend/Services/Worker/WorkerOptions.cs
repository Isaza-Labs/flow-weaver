namespace flow_weaver_backend.Services.Worker;

// Bind to "Worker" section of appsettings. Overridable per-container
// via env vars (Worker__Tags, Worker__MaxConcurrency, etc.).
public class WorkerOptions
{
    public const string SectionName = "Worker";

    // How many jobs can be processed at the same time on this process.
    // Step jobs are typically short (seconds), but workflow_run jobs hold
    // a slot for the entire orchestration duration (minutes–hours).
    // Split tags across containers to avoid starvation.
    public int MaxConcurrency { get; set; } = 10;

    // Which queue tags this worker claims: `default` (step jobs),
    // `orchestrator` (workflow_run orchestration jobs) and `sandbox`
    // (python_snippet steps, which need the bwrap sandbox).
    //
    // Deployments where only SOME containers can run bwrap (the compose
    // deploy: the worker service carries seccomp/apparmor=unconfined, the
    // API service keeps Docker's default hardening) MUST override the
    // hardened container's tags to EXCLUDE `sandbox` — otherwise it
    // intermittently claims python steps it cannot sandbox and they fail
    // with "No permissions to create new namespace". See the Worker__Tags
    // overrides in deploy/docker-compose.yml.
    //
    // This bound property MUST default to EMPTY. .NET's ConfigurationBinder
    // can only ADD to a pre-initialized array (existing elements are kept
    // and config items appended), so a non-empty initializer here could
    // never be shrunk by an override: with `["default","orchestrator",
    // "sandbox"]` as the default, the compose backend's Worker__Tags__0/1
    // override still ended up CONTAINING `sandbox` and the hardened
    // container kept claiming python steps. Consumers read EffectiveTags,
    // which falls back to all three tags when nothing is configured
    // (single-process dev setup).
    public string[] Tags { get; set; } = [];

    public static readonly string[] DefaultTags = ["default", "orchestrator", "sandbox"];

    // The claim list the worker actually uses: the configured tags
    // (deduplicated — appsettings + env can both contribute) or, when
    // nothing is configured, every tag.
    private string[]? _effectiveTags;
    public string[] EffectiveTags =>
        _effectiveTags ??= Tags is { Length: > 0 } ? Tags.Distinct().ToArray() : DefaultTags;

    // How long the worker sleeps when the queue is empty before polling
    // again. 500ms is a reasonable latency/cost balance — step latency is
    // ~500ms worst-case, DB cost is ~2 QPS per idle worker.
    public int PollDelayMs { get; set; } = 500;
}
