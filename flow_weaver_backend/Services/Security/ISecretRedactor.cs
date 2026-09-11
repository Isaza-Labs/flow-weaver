namespace flow_weaver_backend.Services.Security;

// Best-effort removal of common secret shapes from user-controlled text
// before we persist it (agent prompts, log messages, etc). The goal is
// belt-and-suspenders — if an operator pastes an API key into the chat,
// we redact it before it lands in report_artifacts instead of relying on
// encryption-at-rest alone.
//
// Implementations are stateless + thread-safe; singleton DI.
public interface ISecretRedactor
{
    /// <summary>
    /// Returns the input with known secret-like tokens replaced by a
    /// marker like `[REDACTED:apikey]`. `matched` is true when at least
    /// one replacement happened — callers flip the row's `redacted` flag
    /// so admins viewing the record know the stored text was edited.
    /// </summary>
    (string redacted, bool matched) Redact(string? input);
}
