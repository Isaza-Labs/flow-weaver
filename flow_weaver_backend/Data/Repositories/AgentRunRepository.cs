using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

public class AgentRunRepository : RepositoryBase<AgentRun>, IAgentRunRepository
{
    public AgentRunRepository(AppDbContext db) : base(db)
    {
    }
}
