namespace flow_weaver_backend.Services.Ai.Tools;

// Scoped carrier of "which conversation and message triggered the tool
// being dispatched right now". The AiChatController hydrates it once per
// turn before calling ToolDispatcher; tool handlers that need origin
// context (generate_report being the first consumer) inject it and read
// the populated values.
//
// Why not thread this through IToolHandler.ExecuteAsync? Because the
// contract is shared with tools that don't care — forcing every handler
// to ignore a new parameter would ripple through dozens of files and
// invite bugs. A scoped side-channel keeps the common case clean.
public interface IToolExecutionContext
{
    Guid? ConversationId { get; }
    Guid? AgentRunId { get; }
    string? AgentName { get; }
    string? UserMessage { get; }

    void Hydrate(Guid? conversationId, Guid? agentRunId, string? agentName, string? userMessage);

    // Integrations whose skills are loaded in this conversation: what came in from
    // the conversation's Context plus whatever this turn loaded (a mention, an API
    // call, the load_skill tool). The runner persists the union at the end.
    IReadOnlyCollection<Guid> LoadedSkillIntegrationIds { get; }

    // True when this call is what loaded it — false when it was already in.
    bool MarkSkillLoaded(Guid integrationId);
}

public sealed class ToolExecutionContext : IToolExecutionContext
{
    private readonly HashSet<Guid> _loadedSkills = new();

    public IReadOnlyCollection<Guid> LoadedSkillIntegrationIds => _loadedSkills;

    public bool MarkSkillLoaded(Guid integrationId) => _loadedSkills.Add(integrationId);

    public Guid? ConversationId { get; private set; }
    public Guid? AgentRunId { get; private set; }
    public string? AgentName { get; private set; }
    public string? UserMessage { get; private set; }

    public void Hydrate(Guid? conversationId, Guid? agentRunId, string? agentName, string? userMessage)
    {
        ConversationId = conversationId;
        AgentRunId = agentRunId;
        AgentName = agentName;
        UserMessage = userMessage;
    }
}
