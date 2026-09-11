using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AiPromptSkillRepository : RepositoryBase<AiPromptSkill>, IAiPromptSkillRepository
{
    public AiPromptSkillRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Dictionary<string, AiPromptSkill>> GetByNamesAsync(
        IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0) return new Dictionary<string, AiPromptSkill>();
        return await Query(activeOnly: false, tracking: true)
            .Where(s => names.Contains(s.Name))
            .ToDictionaryAsync(s => s.Name, ct);
    }

    public async Task<IReadOnlyList<AiPromptSkill>> ListOrderedAsync(
        int limit, int offset, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

    public async Task<AiPromptSkill?> FindByNameAsync(string name, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: true)
            .FirstOrDefaultAsync(s => s.Name == name, ct);

    public async Task<bool> NameExistsForOtherAsync(
        string name, Guid excludeId, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: false)
            .AnyAsync(s => s.Name == name && s.AiPromptSkillId != excludeId, ct);

    public async Task<DateTime?> MaxActiveUpdatedAtAsync(CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .MaxAsync(s => (DateTime?)s.UpdatedAt, ct);

    public async Task<IReadOnlyList<string>> ListActiveContentsOrderedAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => s.Content)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListActiveGlobalContentsOrderedAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(s => s.IntegrationId == null)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => s.Content)
            .ToListAsync(ct);
}
