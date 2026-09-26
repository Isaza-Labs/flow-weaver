namespace flow_weaver_backend.Services.Engine;

// Bind to "Workflow" section of appsettings. All values have sane defaults
// so the section can be omitted entirely without breaking boot.
public class WorkflowExecutorOptions
{
    public const string SectionName = "Workflow";

    // How many ExecuteRunAsync orchestrations can be active at the same time
    // on this process. Each one holds a polling loop for the run's duration,
    // consuming a thread-pool thread. 20 is generous for a single worker;
    // scale horizontally rather than bumping this past ~50.
    public int MaxConcurrentWorkflows { get; set; } = 20;

    // Per-step timeout. If a step's step_runs row stays non-terminal for
    // longer than this, the orchestrator marks it failed and continues.
    public int StepTimeoutSeconds { get; set; } = 1800; // 30 min

    // How often the orchestrator polls step_runs for progress. 500ms is a
    // reasonable balance: fast enough for interactive feedback, low enough
    // DB cost at ~2 QPS per active run.
    public int PollIntervalMs { get; set; } = 500;

    // Hard ceiling on orchestration wall-clock. If a run hasn't completed
    // by this time, it's force-failed. Guards against infinite-wait bugs
    // where a step never transitions to terminal.
    public int OrchestrationTimeoutSeconds { get; set; } = 7200; // 2 h

    // Sprint 3.4: which environment this process is allowed to run
    // workflows for. A ladder: "dev-sandbox" accepts drafts only, "qa-lab"
    // accepts drafts + qa, "production" accepts drafts + qa + production.
    // Default allows all drafts to run in dev without extra config.
    public string? WorkerEnvironment { get; set; } = "dev-sandbox";

    // Frontend origin used to render `{{ run.url }}` as an absolute link
    // (`<base>/runs/<run id>`) inside failure notifications. Empty => the
    // template still resolves, but to the relative path `/runs/<id>`, which
    // is useless in an email. Set it to whatever URL operators actually
    // type in their browser (env: Workflow__PublicBaseUrl).
    public string? PublicBaseUrl { get; set; }
}
