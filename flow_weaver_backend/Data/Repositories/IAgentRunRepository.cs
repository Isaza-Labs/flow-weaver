using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// AgentRun persistence for the agent runner: a run header is staged at the
// start of a turn and mutated to its terminal state at the end. The generic
// contract (Add + SaveChangesAsync over the shared scope) covers that; the
// named interface keeps the injection point explicit and gives future admin
// read paths (latency / cost metrics) a home.
public interface IAgentRunRepository : IRepository<AgentRun>
{
}
