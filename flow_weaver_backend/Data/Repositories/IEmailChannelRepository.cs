using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// SMTP channel persistence. CRUD comes from the base methods; the extras serve
// the worker (email_send resolves a channel by id or falls back to the default)
// and the service's single-default invariant.
public interface IEmailChannelRepository : IRepository<EmailChannel>
{
    // The channel an email_send step uses when it names none. Read-only.
    Task<EmailChannel?> FindDefaultAsync(CancellationToken ct = default);

    // Active + enabled channel by id — the worker refuses to send through a
    // disabled channel. Read-only.
    Task<EmailChannel?> FindEnabledByIdAsync(Guid channelId, CancellationToken ct = default);

    // Active + enabled channel whose Name matches exactly, case-insensitively,
    // after trimming. Backs the portable `channel` key of an email_send step
    // (snippets/SPEC.md `email_send`), which names a relay rather than carrying
    // a per-instance id. Read-only.
    Task<EmailChannel?> FindEnabledByNameAsync(string name, CancellationToken ct = default);

    // Every active row currently flagged default, tracked, so the service can
    // demote them before promoting a new one.
    Task<IReadOnlyList<EmailChannel>> ListDefaultsTrackedAsync(CancellationToken ct = default);

    // Name uniqueness check (excluding one id on update).
    Task<bool> NameExistsAsync(string name, Guid? exceptId = null, CancellationToken ct = default);

    // Stamps LastSendAt/LastSendStatus after a real send. Called by the
    // email_send worker handler — before this only the manual /email test
    // endpoint wrote them, so channels used exclusively by workflow runs
    // showed a permanently-null "last send" in the UI.
    Task MarkSendOutcomeAsync(Guid channelId, bool ok, CancellationToken ct = default);
}
