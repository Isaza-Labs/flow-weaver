using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class SimulationResultRepository : RepositoryBase<SimulationResult>, ISimulationResultRepository
{
    public SimulationResultRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<SimulationResult?> FindActiveByIdAsync(
        Guid simulationResultId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(s => s.SimulationResultId == simulationResultId, ct);
}
