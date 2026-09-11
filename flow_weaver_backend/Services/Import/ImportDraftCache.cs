using System.Collections.Concurrent;
using flow_weaver_backend.Services.Import.Models;

namespace flow_weaver_backend.Services.Import;

// In-memory store of in-flight import drafts. Per-instance: a multi-API
// deployment requires sticky session affinity OR a swap to a distributed
// cache. For the current single-instance backend, this is enough.
//
// TTL: drafts expire 30 minutes after creation. A background sweep
// triggered on access keeps the cache from growing without bound; we
// don't run a hosted service for it because the cache is naturally
// small (one entry per active wizard session).
public sealed class ImportDraftCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, ImportDraft> _drafts = new();

    public ImportDraft Create(Guid userId, string formatHint, byte[] rawBody)
    {
        SweepExpired();
        var draft = new ImportDraft(userId, formatHint, rawBody);
        _drafts[draft.Token] = draft;
        return draft;
    }

    // Returns null when the token is unknown OR the draft belongs to a
    // different user (we don't surface a 403 differently from a 404 to
    // avoid leaking that "this token exists, just not yours").
    public ImportDraft? Get(Guid token, Guid userId)
    {
        SweepExpired();
        if (!_drafts.TryGetValue(token, out var draft)) return null;
        if (draft.UserId != userId) return null;
        return draft;
    }

    public void Delete(Guid token)
    {
        if (_drafts.TryGetValue(token, out var draft))
        {
            if (_drafts.TryRemove(token, out var removed))
            {
                // Cancel before disposing — the pipeline observes the
                // token and bails out of pending LLM calls, validators,
                // and DB writes.
                removed.Cancel();
                removed.Dispose();
            }
        }
    }

    private void SweepExpired()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var (token, draft) in _drafts)
        {
            if (draft.CreatedAt < cutoff)
            {
                if (_drafts.TryRemove(token, out var removed))
                {
                    removed.Cancel();
                    removed.Dispose();
                }
            }
        }
    }
}
