using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AppSettingsRepository : IAppSettingsRepository
{
    private readonly AppDbContext _db;

    public AppSettingsRepository(AppDbContext db) => _db = db;

    public async Task<AppSetting?> GetAsync(bool tracking = false, CancellationToken ct = default)
    {
        IQueryable<AppSetting> q = tracking ? _db.AppSettings : _db.AppSettings.AsNoTracking();
        return await q.FirstOrDefaultAsync(s => s.Id == AppSetting.SingletonId, ct);
    }

    public void Add(AppSetting row) => _db.AppSettings.Add(row);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
