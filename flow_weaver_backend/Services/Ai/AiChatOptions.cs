namespace flow_weaver_backend.Services.Ai;

// Bind to "AiChat" section of appsettings. All values have sane defaults
// so the section can be omitted without breaking boot.
//
// The stream deadline used to be a hardcoded 25s const, which was fine
// for simple ping-style turns but starved out multi-integration
// workflow construction (NetBox + email + Infoblox would spend most of
// the budget on discover_operations/operation_detail round-trips). 120s
// gives the deeper, more-recursive turns (raised MaxIterations) room to
// finish while still protecting against runaway loops. Override via env
// var `AiChat__StreamDeadlineSeconds` per environment (e.g. prod=90).
public sealed class AiChatOptions
{
    public const string SectionName = "AiChat";

    // Hard wall-clock on Stream(). Exceeding it emits a timeout SSE
    // event with the partial tail and closes the turn. The rescue
    // persist in the finally block still saves the user message +
    // whatever assistant text accumulated.
    //
    // 240, up from 120: every tool round re-sends the whole prompt (tens of
    // thousands of tokens of skills), each REST call may take up to 60s, and a
    // real answer takes the model a while to write; at 120s such turns ended as
    // `timeout` with half an answer on screen. The "llm" HttpClient backstop is
    // 5 minutes (Program.cs) — keep at least a minute under it, or the transport
    // ends the turn instead of this deadline. A reverse proxy in front needs its
    // read timeout above this number too, or the cut moves there, silently.
    public int StreamDeadlineSeconds { get; set; } = 240;

    // Percentage of the deadline (0-100) at which AiChatController
    // emits a warning log with elapsed + tool-call count. Helps catch
    // turns that are about to bust the deadline before they actually
    // do, so you can tune the deadline or the prompt without waiting
    // for an incident.
    public int DeadlineWarnPercent { get; set; } = 75;

    // Hard cap on the characters of a single tool result echoed back to
    // the model as the `tool` message. A handler returning a huge payload
    // (e.g. get_step_logs with a multi-device config dump) used to push
    // the request past the model's context window, and the provider
    // rejected the whole turn with 400. Beyond this the result is
    // truncated with a marker; the model still gets the head and can
    // narrow the call. Override via env var `AiChat__MaxToolResultChars`.
    public int MaxToolResultChars { get; set; } = 100_000;

    // Cap on the characters of conversation history replayed to the model each
    // turn. The newest messages that fit are sent, cut on turn boundaries; the
    // model is told how many earlier messages were left out. 0 or less disables
    // the window and replays everything, which is what happened before.
    //
    // Every turn (and every tool round inside it) re-sends the whole thread, so an
    // unbounded history grows until the provider rejects the request or a gateway
    // trims it from the top — where the system prompt is. 60_000 chars is about
    // 15k tokens: a dozen ordinary exchanges, or two or three heavy ones with
    // tables in them. Override via env var `AiChat__HistoryCharBudget`.
    public int HistoryCharBudget { get; set; } = 60_000;
}
