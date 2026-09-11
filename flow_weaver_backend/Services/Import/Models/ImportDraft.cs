using System.Text.Json;
using System.Threading.Channels;

namespace flow_weaver_backend.Services.Import.Models;

// Lifecycle of one user-initiated workflow import. The draft lives in
// memory (ImportDraftCache) keyed by token; the user uploads → analyze
// runs async → result lands here → user commits or abandons.
public enum ImportDraftStatus
{
    Pending,    // accepted, queued but not started
    Analyzing,  // pipeline is running
    Ready,      // analysis complete; user can commit
    Failed,     // analysis blew up; .Error has detail
    Committed,  // commit succeeded; draft kept briefly for traceability
}

public sealed class ImportDraft : IDisposable
{
    public Guid Token { get; }
    public Guid UserId { get; }
    public string FormatHint { get; }
    public byte[] RawBody { get; }
    public DateTime CreatedAt { get; }

    public ImportDraftStatus Status { get; private set; } = ImportDraftStatus.Pending;
    public AnalysisReport? Report { get; private set; }
    public string? Error { get; private set; }
    public string? ProgressMessage { get; private set; }

    // SSE stream — one channel per draft. Events fire on every transition
    // and on intermediate progress notes from the pipeline. The reader
    // side is the controller; the writer side is the pipeline.
    private readonly Channel<DraftEvent> _events = Channel.CreateUnbounded<DraftEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    public ChannelReader<DraftEvent> Events => _events.Reader;

    // Per-draft cancellation source. The controller hands `Token` to the
    // background pipeline; calling `Cancel()` from DELETE / SweepExpired
    // tears down the analyze run (LLM calls included) instead of leaving
    // it to keep burning tokens after the user abandoned the wizard.
    private readonly CancellationTokenSource _cts = new();
    public CancellationToken CancellationToken => _cts.Token;
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }
    }
    public void Dispose() => _cts.Dispose();

    public ImportDraft(Guid userId, string formatHint, byte[] rawBody)
    {
        Token = Guid.NewGuid();
        UserId = userId;
        FormatHint = formatHint;
        RawBody = rawBody;
        CreatedAt = DateTime.UtcNow;
    }

    public void MarkAnalyzing(string? message = null)
    {
        Status = ImportDraftStatus.Analyzing;
        ProgressMessage = message;
        EmitEvent("status_changed");
    }

    public void MarkProgress(string message)
    {
        ProgressMessage = message;
        EmitEvent("progress");
    }

    public void MarkReady(AnalysisReport report)
    {
        Status = ImportDraftStatus.Ready;
        Report = report;
        ProgressMessage = null;
        EmitEvent("report_ready");
        _events.Writer.TryComplete();
    }

    public void MarkFailed(string error)
    {
        Status = ImportDraftStatus.Failed;
        Error = error;
        EmitEvent("error");
        _events.Writer.TryComplete();
    }

    public void MarkCommitted()
    {
        Status = ImportDraftStatus.Committed;
        EmitEvent("status_changed");
        _events.Writer.TryComplete();
    }

    // Lets the pipeline patch the proposed workflow (e.g. user-generated
    // snippets get stitched into the proposed_workflow on commit time).
    public void UpdateReport(AnalysisReport report) => Report = report;

    private void EmitEvent(string type)
    {
        _events.Writer.TryWrite(new DraftEvent(type, Status, ProgressMessage, Error));
    }
}

public sealed record DraftEvent(string Type, ImportDraftStatus Status, string? Message, string? Error);
