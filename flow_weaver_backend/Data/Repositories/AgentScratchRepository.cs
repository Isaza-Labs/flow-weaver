using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AgentScratchRepository : RepositoryBase<AgentScratch>, IAgentScratchRepository
{
    public AgentScratchRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<JsonElement?> ReadValueAsync(
        Guid conversationId, string key, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(s => s.ConversationId == conversationId && s.Key == key)
            .Select(s => (JsonElement?)s.Value)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AgentScratch>> ListActiveAsync(
        Guid conversationId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(s => s.ConversationId == conversationId)
            .OrderBy(s => s.Key)
            .ToListAsync(ct);

    public async Task<(bool Raced, Exception? Error)> UpsertAsync(
        Guid conversationId, string key, JsonElement value,
        Guid? workflowPlanId, string? note, DateTime now, CancellationToken ct = default)
    {
        var existing = await Query(activeOnly: true, tracking: true)
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId && s.Key == key, ct);

        if (existing is not null)
        {
            existing.Value = value;
            existing.WorkflowPlanId = workflowPlanId ?? existing.WorkflowPlanId;
            existing.Note = note ?? existing.Note;
            existing.UpdatedAt = now;
        }
        else
        {
            Set.Add(new AgentScratch
            {
                AgentScratchId = Guid.NewGuid(),
                ConversationId = conversationId,
                WorkflowPlanId = workflowPlanId,
                Key = key,
                Value = value,
                Note = note,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        try
        {
            await Db.SaveChangesAsync(ct);
            return (false, null);
        }
        catch (DbUpdateException ex)
        {
            // Concurrent upsert raced on the unique index. Drop our tracked
            // entity and retry as UPDATE on the row that won.
            foreach (var entry in Db.ChangeTracker.Entries<AgentScratch>().ToList())
                entry.State = EntityState.Detached;

            var winner = await Query(activeOnly: true, tracking: true)
                .FirstOrDefaultAsync(s => s.ConversationId == conversationId && s.Key == key, ct);
            if (winner is null) throw;
            winner.Value = value;
            winner.Note = note ?? winner.Note;
            winner.UpdatedAt = now;
            await Db.SaveChangesAsync(ct);
            return (true, ex);
        }
    }
}
