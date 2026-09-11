using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Ai.Skills;
using flow_weaver_backend.Services.Ai.Tools;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
// `AIAgent` clashes with the flow_weaver_backend.Dtos.AIAgent namespace (DTO
// folder); alias to the model like LlmProviderFactory does.
using AiAgentModel = flow_weaver_backend.Models.AIAgent;

namespace flow_weaver_backend.Services.Ai.Conversation;

// The agent's tool-calling loop, lifted out of AiChatController so it can run
// under any transport: the web chat wraps an SseAgentEventSink around it, the
// messaging worker (F2) passes NullAgentEventSink and uses the returned text.
//
// Identity comes from the scoped ICurrentUser — the caller binds it (JWT or
// MutableCurrentUser.Bind) before resolving this runner, and the same value
// reaches ToolDispatcher for RBAC, so a turn can never act above the bound
// user's role regardless of channel.
public sealed class AgentConversationRunner : IAgentConversationRunner
{
    // jsonb tool-call log uses camelCase keys to match the shape AiChatController
    // wrote before the extraction (admin reads agent_runs.ToolCalls).
    private static readonly JsonSerializerOptions ToolLogJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppDbContext _db; // AIAgent/AIProvider resolution only
    private readonly IAIConversationRepository _conversations;
    private readonly IAgentRunRepository _agentRuns;
    private readonly LlmProviderFactory _providerFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolDispatcher _dispatcher;
    private readonly ISkillPromptLoader _skillLoader;
    private readonly ScopedSkillCatalog _scopedSkills;
    private readonly ICurrentUser _caller;
    private readonly ITraceLogger _trace;
    private readonly IToolExecutionContext _toolContext;
    private readonly AiChatOptions _chatOptions;
    private readonly ILogger<AgentConversationRunner> _logger;

    public AgentConversationRunner(
        AppDbContext db,
        IAIConversationRepository conversations,
        IAgentRunRepository agentRuns,
        LlmProviderFactory providerFactory,
        ToolRegistry toolRegistry,
        ToolDispatcher dispatcher,
        ISkillPromptLoader skillLoader,
        ScopedSkillCatalog scopedSkills,
        ICurrentUser caller,
        ITraceLogger trace,
        IToolExecutionContext toolContext,
        IOptions<AiChatOptions> chatOptions,
        ILogger<AgentConversationRunner> logger)
    {
        _db = db;
        _scopedSkills = scopedSkills;
        _conversations = conversations;
        _agentRuns = agentRuns;
        _providerFactory = providerFactory;
        _toolRegistry = toolRegistry;
        _dispatcher = dispatcher;
        _skillLoader = skillLoader;
        _caller = caller;
        _trace = trace;
        _toolContext = toolContext;
        _chatOptions = chatOptions.Value;
        _logger = logger;
    }

    public async Task<AgentTurnResult> RunAsync(
        AgentTurnRequest request, IAgentEventSink sink, CancellationToken ct)
    {
        string userMessage = request.Message;
        var deadlineSeconds = request.DeadlineSecondsOverride
            ?? (_chatOptions.StreamDeadlineSeconds > 0 ? _chatOptions.StreamDeadlineSeconds : 60);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));
        var streamCt = deadlineCts.Token;

        var warnThresholdMs = (long)(deadlineSeconds * 1000
            * Math.Clamp(_chatOptions.DeadlineWarnPercent, 1, 100) / 100.0);
        var warnAlreadyFired = false;
        var streamStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Resolve agent + provider up front. On failure, report through the sink
        // and return a structured error instead of throwing — an SSE caller has
        // already committed to a reader and can't re-parse an exception body.
        AiAgentModel? agent;
        IStreamingToolCallingLlmProvider llm;
        string model;
        string systemPrompt;
        try
        {
            (llm, model, agent, systemPrompt) = await ResolveAgentAsync(request.AgentId, streamCt);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No AI provider"))
        {
            await sink.ErrorAsync(ex.Message, "no_provider", ct);
            return ErrorResult(request.ConversationId, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No AI agent"))
        {
            await sink.ErrorAsync(ex.Message, "no_agent", ct);
            return ErrorResult(request.ConversationId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.chat.resolve_failed agent={AgentId}", request.AgentId);
            await sink.ErrorAsync(ex.Message, "resolve_failed", ct);
            return ErrorResult(request.ConversationId, ex.Message);
        }

        var maxIter = agent?.MaxIterations ?? 20;
        var temperature = agent?.Temperature ?? 0.2;

        // Integration skills: the index always, the text when the integration is in
        // play. "In play" at the start of a turn means loaded earlier in this
        // conversation (kept in its Context) or named in this message; API calls
        // during the turn and the load_skill tool add to the set as it goes.
        var scopedSkills = await _scopedSkills.ListAsync(streamCt);
        var previouslyLoaded = scopedSkills.Count == 0
            ? []
            : await LoadLoadedSkillsAsync(request.ConversationId, streamCt);
        foreach (var id in previouslyLoaded) _toolContext.MarkSkillLoaded(id);
        foreach (var id in ScopedSkillCatalog.MatchMentions(userMessage, scopedSkills)) _toolContext.MarkSkillLoaded(id);
        systemPrompt = ScopedSkillCatalog.ComposePrompt(
            systemPrompt, scopedSkills, new HashSet<Guid>(_toolContext.LoadedSkillIntegrationIds));

        var messages = await BuildInitialMessages(systemPrompt, userMessage, request.ConversationId, streamCt);
        var tools = _toolRegistry.ToDefinitions(agent?.Tools);
        // load_skill is how the model reaches a skill it has not been handed; an
        // agent whose tool list predates it must still be able to call it.
        if (scopedSkills.Count > 0 && !tools.Any(t => t.Name.Equals("load_skill", StringComparison.OrdinalIgnoreCase)))
            tools.AddRange(_toolRegistry.ToDefinitions(new[] { "load_skill" }));

        // Pre-assign a conversation id (new or existing) and echo it back right
        // away. Without this a client that sent null can't learn the
        // server-assigned id and every follow-up lands in a brand new thread.
        var conversationId = request.ConversationId ?? Guid.NewGuid();
        var isNewConversation = !request.ConversationId.HasValue;
        await sink.ConversationAsync(conversationId, isNewConversation, ct);

        // Hydrate the scoped tool context so audit-writing handlers
        // (generate_report) can attribute the call to this conversation + user
        // message. Re-hydrated below once the AgentRunId exists.
        _toolContext.Hydrate(conversationId, agentRunId: null, agent?.Name, userMessage);

        _logger.LogInformation(
            "ai.chat.begin agent={AgentName} model={Model} tools={ToolCount} messages={MessageCount} conversation={ConversationId} deadline_s={DeadlineSeconds} prompt_chars={PromptChars} scoped_skills={ScopedSkills} skills_loaded={SkillsLoaded}",
            agent?.Name ?? "(fallback)", model, tools.Count, messages.Count,
            conversationId, deadlineSeconds, systemPrompt?.Length ?? 0,
            scopedSkills.Count, _toolContext.LoadedSkillIntegrationIds.Count);

        var traceId = await _trace.StartAsync("ai.chat.run", "ai", new
        {
            agent_id = agent?.AIAgentId,
            agent_name = agent?.Name,
            model,
            conversation_id = conversationId,
            tool_count = tools.Count,
            message_chars = userMessage?.Length ?? 0,
        }, ct);

        var agentRun = new AgentRun
        {
            AgentRunId = Guid.NewGuid(),
            ConversationId = conversationId,
            TraceId = Guid.NewGuid().ToString("N"),
            AgentName = agent?.Name ?? "(fallback)",
            UserId = _caller.UserId.ToString(),
            ToolCalls = JsonDocument.Parse("[]").RootElement,
            Status = AgentRunStatus.Running,
            IsActive = true,
            StartedAt = DateTime.UtcNow,
        };
        _agentRuns.Add(agentRun);
        await _agentRuns.SaveChangesAsync(ct);

        _toolContext.Hydrate(conversationId, agentRun.AgentRunId, agent?.Name, userMessage);

        var toolCalls = new List<AgentToolCall>();
        var totalIn = 0;
        var totalOut = 0;
        var iterationsCompleted = 0;
        var tailText = new StringBuilder();
        var timedOut = false;
        var outputCutOff = false;
        var persisted = false;
        string? lastError = null;

        try
        {
            for (var iter = 0; iter < maxIter && !streamCt.IsCancellationRequested; iter++)
            {
                iterationsCompleted = iter + 1;
                var fullContent = new StringBuilder();
                var pendingToolCalls = new List<ToolCallResult>();
                string? stopReason = null;

                try
                {
                    await foreach (var evt in llm.ChatWithToolsStreamAsync(messages, tools, model, temperature, streamCt))
                    {
                        switch (evt.Type)
                        {
                            case "text_delta":
                                fullContent.Append(evt.TextDelta);
                                tailText.Append(evt.TextDelta);
                                await sink.TextAsync(evt.TextDelta ?? string.Empty, ct);
                                break;
                            case "tool_call":
                                if (evt.ToolCall is not null) pendingToolCalls.Add(evt.ToolCall);
                                break;
                            case "done":
                                if (evt.InputTokens is int ti) totalIn += ti;
                                if (evt.OutputTokens is int to) totalOut += to;
                                if (evt.StopReason is not null) stopReason = evt.StopReason;
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
                {
                    _logger.LogWarning("ai.chat.request.timeout iter={Iteration}", iter + 1);
                    timedOut = true;
                    break;
                }

                if (OutputCutOff(stopReason))
                {
                    // The model ran out of output tokens mid-answer. Nothing in the
                    // text says so — a cut-off answer reads like a short one — and any
                    // tool call it was in the middle of is not one it meant to run, so
                    // the turn ends here: the text as streamed plus a notice the user
                    // can read, persisted together so the next turn's model sees it too.
                    _logger.LogWarning(
                        "ai.chat.output_cut_off trace={Trace} iter={Iteration} stop_reason={StopReason} pending_tool_calls={PendingToolCalls}",
                        agentRun.TraceId, iter + 1, stopReason, pendingToolCalls.Count);
                    outputCutOff = true;
                    tailText.Append(CutOffNotice);
                    await sink.TextAsync(CutOffNotice, ct);
                    break;
                }

                if (pendingToolCalls.Count == 0)
                {
                    _logger.LogInformation(
                        "ai.chat.iteration.end.final iter={Iteration} reason=no_tool_calls", iter + 1);
                    break;
                }

                messages.Add(new LlmMessage
                {
                    Role = "assistant",
                    Content = fullContent.ToString(),
                    ToolCalls = pendingToolCalls,
                });

                foreach (var tc in pendingToolCalls)
                {
                    await sink.ToolStartAsync(tc.Name, tc.Arguments, ct);

                    ToolCallOutput output;
                    try
                    {
                        output = await _dispatcher.DispatchAsync(tc.Name, tc.Arguments, streamCt);
                    }
                    catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
                    {
                        _logger.LogWarning("ai.chat.tool.call.timeout name={Tool}", tc.Name);
                        timedOut = true;
                        break;
                    }

                    var resultRaw = output.Result.GetRawText();
                    _logger.LogInformation(
                        "ai.chat.tool.call.end name={Tool} success={Success} result_chars={ResultChars}",
                        tc.Name, output.Success, resultRaw.Length);

                    await sink.ToolResultAsync(tc.Name, output, ct);
                    toolCalls.Add(new AgentToolCall(tc.Name, tc.Arguments, output.Success));

                    messages.Add(new LlmMessage
                    {
                        Role = "tool",
                        Content = CapForLlm(resultRaw, _chatOptions.MaxToolResultChars),
                        ToolCallId = tc.Id,
                    });

                    // A call against an integration's API is the strongest signal that
                    // its skill is needed; the text goes in right behind the result so
                    // the model reads the rules before it interprets the data.
                    if (scopedSkills.Count > 0
                        && await AutoLoadSkillAsync(tc.Name, tc.Arguments, scopedSkills, streamCt) is { } skillMessage)
                        messages.Add(skillMessage);
                }

                if (!warnAlreadyFired && streamStopwatch.ElapsedMilliseconds >= warnThresholdMs)
                {
                    warnAlreadyFired = true;
                    _logger.LogWarning(
                        "ai.chat.deadline_warn iter={Iteration} elapsed_ms={Elapsed} deadline_ms={Deadline} tool_calls={ToolCalls}",
                        iter + 1, streamStopwatch.ElapsedMilliseconds, deadlineSeconds * 1000, toolCalls.Count);
                }

                if (timedOut) break;
            }

            if (timedOut)
            {
                _logger.LogWarning(
                    "ai.chat.timeout trace={Trace} iterations={Iterations} elapsed_ms={Elapsed} tokens_in={TokensIn} tokens_out={TokensOut}",
                    agentRun.TraceId, iterationsCompleted, streamStopwatch.ElapsedMilliseconds, totalIn, totalOut);
                await _trace.TimeoutAsync(traceId, new
                {
                    iterations = iterationsCompleted,
                    elapsed_ms = streamStopwatch.ElapsedMilliseconds,
                    tokens_in = totalIn,
                    tokens_out = totalOut,
                }, ct);

                var tail = tailText.Length > 500
                    ? tailText.ToString(tailText.Length - 500, 500)
                    : tailText.ToString();
                await sink.TimeoutAsync(tail, deadlineSeconds, ct);
            }
            else
            {
                _logger.LogInformation(
                    "ai.chat.done trace={Trace} iterations={Iterations} elapsed_ms={Elapsed} tokens_in={TokensIn} tokens_out={TokensOut} tool_calls={ToolCalls} output_cut_off={OutputCutOff}",
                    agentRun.TraceId, iterationsCompleted, streamStopwatch.ElapsedMilliseconds, totalIn, totalOut, toolCalls.Count, outputCutOff);
                await _trace.CompleteAsync(traceId, new
                {
                    iterations = iterationsCompleted,
                    elapsed_ms = streamStopwatch.ElapsedMilliseconds,
                    tokens_in = totalIn,
                    tokens_out = totalOut,
                    tool_calls = toolCalls.Count,
                    output_cut_off = outputCutOff,
                }, ct);
                await sink.DoneAsync(totalIn, totalOut, iterationsCompleted, ct);
            }

            // Persist the full assistant turn so follow-up messages can read it
            // as conversation history. Attribute the RESOLVED agent, not the
            // request's: the web chat usually omits agent_id and relies on the
            // default assistant, which left every conversation with
            // agent_id=null even though a concrete agent served it.
            await PersistConversationAsync(
                conversationId, userMessage, agent?.AIAgentId ?? request.AgentId, tailText.ToString(),
                totalIn, totalOut, ct,
                NewlyLoadedSkills(previouslyLoaded));
            persisted = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ai.chat.error trace={Trace} iterations={Iterations} error_type={ErrorType}",
                agentRun.TraceId, iterationsCompleted, ex.GetType().Name);
            await _trace.FailAsync(traceId, ex.Message, new
            {
                error_type = ex.GetType().Name,
                iterations = iterationsCompleted,
            }, CancellationToken.None);
            await sink.ErrorAsync(ex.Message, null, ct);
            agentRun.Status = AgentRunStatus.Failed;
            lastError = ex.Message;
        }
        finally
        {
            agentRun.TokensIn = totalIn;
            agentRun.TokensOut = totalOut;
            agentRun.ToolCalls = JsonSerializer.SerializeToElement(toolCalls, ToolLogJson);
            if (agentRun.Status == AgentRunStatus.Running)
                agentRun.Status = timedOut ? AgentRunStatus.Failed : AgentRunStatus.Completed;
            agentRun.CompletedAt = DateTime.UtcNow;
            agentRun.UpdatedAt = DateTime.UtcNow;
            try { await _agentRuns.SaveChangesAsync(CancellationToken.None); } catch { /* best effort */ }

            // Rescue: if the happy-path persist never ran (timeout, stream break,
            // 503), save what we have so the user message + partial assistant
            // text live in history. CancellationToken.None because the caller's
            // ct may already be cancelled here.
            if (!persisted)
            {
                var partial = tailText.ToString();
                var content = lastError is null
                    ? partial
                    : string.IsNullOrWhiteSpace(partial) ? $"[error: {lastError}]" : $"{partial}\n\n[error: {lastError}]";
                try
                {
                    await PersistConversationAsync(
                        conversationId, userMessage, agent?.AIAgentId ?? request.AgentId, content,
                        totalIn, totalOut, CancellationToken.None,
                        NewlyLoadedSkills(previouslyLoaded));
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(persistEx, "ai.chat.persist_rescue_failed trace={Trace}", agentRun.TraceId);
                }
            }
        }

        return new AgentTurnResult
        {
            ConversationId = conversationId,
            IsNewConversation = isNewConversation,
            FinalText = tailText.ToString(),
            ToolCalls = toolCalls,
            TokensIn = totalIn,
            TokensOut = totalOut,
            Iterations = iterationsCompleted,
            TimedOut = timedOut,
            OutputCutOff = outputCutOff,
            Error = lastError,
        };
    }

    // What the user reads when the model stops mid-answer for lack of output
    // tokens. Appended to the answer rather than sent as an error frame: an error
    // would paint the whole turn red and hide the text that did arrive, and going
    // through the answer means it is persisted with it, so on the next turn the
    // model also sees that it was cut off and can pick up instead of restarting.
    public const string CutOffNotice =
        "\n\n[Answer cut off: the model hit its output-token limit before finishing. " +
        "Ask me to continue, or narrow the request.]";

    // OpenAI and Ollama say "length"; Anthropic says "max_tokens"; Gemini says
    // "MAX_TOKENS". Either means "not finished", the only fact the runner needs.
    public static bool OutputCutOff(string? stopReason) =>
        string.Equals(stopReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

    // The newest messages that fit `charBudget`, cut on turn boundaries, oldest
    // first. `Omitted` is how many were left out. The most recent exchange is
    // always sent, budget or no budget: the model has to see what was just said.
    // A budget of zero or less disables the window.
    public static (List<LlmMessage> Kept, int Omitted) WindowHistory(IReadOnlyList<LlmMessage> history, int charBudget)
    {
        if (charBudget <= 0 || history.Count == 0) return (history.ToList(), 0);

        var start = history.Count;
        long used = 0;
        while (start > 0)
        {
            var length = history[start - 1].Content?.Length ?? 0;
            if (used + length > charBudget && history.Count - start >= 2) break;
            used += length;
            start--;
        }
        // Never open on the agent's half of a turn: an answer without its question
        // reads as the model talking to itself.
        while (start < history.Count && history[start].Role == "assistant") start++;

        return (history.Skip(start).ToList(), start);
    }

    // Told to the model when history was windowed, so it asks about what it
    // cannot see instead of assuming it remembers.
    public static string HistoryOmittedNote(int omitted) =>
        $"[Context note: the {omitted} earliest messages of this conversation were left out to fit the "
        + "model's context window. If the user refers to something from them, ask rather than assume.]";

    // The set to persist when this turn loaded something new; null when nothing
    // changed, which PersistConversationAsync reads as "leave Context alone".
    private IReadOnlyCollection<Guid>? NewlyLoadedSkills(IReadOnlyCollection<Guid> previouslyLoaded)
        => _toolContext.LoadedSkillIntegrationIds.Count > previouslyLoaded.Count
            ? _toolContext.LoadedSkillIntegrationIds
            : null;

    // Loads the skills of the integration a tool call is about, once per
    // conversation, as a system message placed right after the tool result. Null
    // when the call names no integration or its skills are already in.
    private async Task<LlmMessage?> AutoLoadSkillAsync(
        string toolName, JsonElement args, IReadOnlyList<ScopedSkill> scopedSkills, CancellationToken ct)
    {
        Guid? integrationId;
        try
        {
            integrationId = await _scopedSkills.IntegrationForToolCallAsync(toolName, args, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not worth a turn: the call already ran; the skill just arrives later.
            _logger.LogWarning(ex, "ai.chat.skill_autoload_failed tool={Tool}", toolName);
            return null;
        }
        if (integrationId is not { } id) return null;

        var skills = scopedSkills.Where(s => !s.AlwaysLoaded && s.IntegrationId == id).ToList();
        if (skills.Count == 0 || !_toolContext.MarkSkillLoaded(id)) return null;

        _logger.LogInformation(
            "ai.chat.skill_autoloaded conversation={ConversationId} tool={Tool} integration={Integration} skills={Skills}",
            _toolContext.ConversationId, toolName, skills[0].IntegrationSlug, string.Join(",", skills.Select(s => s.Name)));

        return new LlmMessage
        {
            Role = "system",
            Content = string.Join("\n\n---\n\n", skills.Select(ScopedSkillCatalog.RenderLoaded)),
        };
    }

    // The integrations whose skills this conversation has already loaded, from its
    // Context. A conversation that does not exist yet has none.
    private async Task<IReadOnlyCollection<Guid>> LoadLoadedSkillsAsync(Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } id) return [];
        try
        {
            var conv = await _conversations.FindForHistoryAsync(id, ct);
            return conv is null ? [] : ScopedSkillCatalog.LoadedFromContext(conv.Context);
        }
        catch (Exception ex)
        {
            // Failing closed means the skill loads again on the next mention or API
            // call, which costs a round-trip and nothing else.
            _logger.LogWarning(ex, "ai.chat.loaded_skills_read_failed conversation={ConversationId}", id);
            return [];
        }
    }

    // Caps a tool result before it goes back to the model as the `tool` message.
    // Keeps the head of the raw text and appends a plain marker — the model
    // reads it as text, so it need not stay valid JSON. Prevents a single
    // oversized result from blowing the context window (→ 400).
    internal static string CapForLlm(string raw, int maxChars)
    {
        if (maxChars <= 0 || raw.Length <= maxChars) return raw;
        return raw.Substring(0, maxChars)
            + $"\n…[truncated {raw.Length - maxChars} chars to fit the model context — narrow the tool call (e.g. one step_run_id) or fetch the raw output another way]";
    }

    private static AgentTurnResult ErrorResult(Guid? conversationId, string error) => new()
    {
        ConversationId = conversationId ?? Guid.Empty,
        IsNewConversation = !conversationId.HasValue,
        Error = error,
    };

    // Resolves (llm, model, agent, system_prompt) for the current turn.
    //   1. explicit agent_id, else
    //   2. the default enabled "assistant" agent, else
    //   3. a clear "no agent configured" error — we deliberately do NOT
    //      synthesize a fallback around the provider default, because that
    //      ran the chat with an unconfigured model/temperature and failed
    //      with an obscure upstream 400 instead of an actionable message.
    private async Task<(IStreamingToolCallingLlmProvider llm, string model, AiAgentModel? agent, string systemPrompt)>
        ResolveAgentAsync(Guid? agentId, CancellationToken ct)
    {
        AiAgentModel? agent = null;
        IStreamingToolCallingLlmProvider llm;
        string model;

        if (agentId.HasValue)
        {
            (llm, model) = await _providerFactory.ResolveForAgentAsync(agentId.Value, ct);
            agent = await _db.AIAgents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.AIAgentId == agentId.Value, ct);
        }
        else
        {
            agent = await _db.AIAgents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Enabled && a.IsActive && a.Role == "assistant", ct);
            if (agent is not null)
            {
                (llm, model) = await _providerFactory.ResolveForAgentAsync(agent.AIAgentId, ct);
            }
            else
            {
                // No explicit agent_id and no default "assistant" agent.
                // Require an agent instead of silently falling back, and say
                // exactly what's missing — the provider first, since an agent
                // can't exist without one.
                _ = await _db.AIProviders.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.IsActive && p.Enabled, ct)
                    ?? throw new InvalidOperationException(
                        "No AI provider configured. Add one in Settings > AI Providers.");
                throw new InvalidOperationException(
                    "No AI agent configured. Create an enabled \"assistant\" agent in Settings > AI Agents.");
            }
        }

        var systemPrompt = await BuildSystemPromptAsync(agent, ct);
        return (llm, model, agent, systemPrompt);
    }

    private async Task<string> BuildSystemPromptAsync(AiAgentModel? agent, CancellationToken ct)
    {
        var tools = _toolRegistry.ToDefinitions(agent?.Tools);
        var toolListText = tools.Count == 0
            ? "(no tools enabled)"
            : string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}"));

        var skillCtx = new SkillTemplateContext
        {
            CurrentDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ToolList = toolListText,
        };

        var skills = await _skillLoader.LoadAsync(skillCtx, ct);
        var agentTail = string.IsNullOrWhiteSpace(agent?.SystemPrompt) ? string.Empty : agent!.SystemPrompt;

        if (string.IsNullOrWhiteSpace(skills) && string.IsNullOrWhiteSpace(agentTail))
            return "You are a helpful network automation assistant.";

        return string.IsNullOrWhiteSpace(agentTail) ? skills : $"{skills}\n\n---\n\n{agentTail}";
    }

    private async Task<List<LlmMessage>> BuildInitialMessages(
        string systemPrompt, string userMessage, Guid? conversationId, CancellationToken ct)
    {
        var msgs = new List<LlmMessage> { new() { Role = "system", Content = systemPrompt } };

        if (conversationId.HasValue)
        {
            var conv = await _conversations.FindForHistoryAsync(conversationId.Value, ct);
            var history = new List<LlmMessage>();
            if (conv is not null && conv.Messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in conv.Messages.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                    var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    if (role != "system") history.Add(new LlmMessage { Role = role, Content = content });
                }
            }

            var (kept, omitted) = WindowHistory(history, _chatOptions.HistoryCharBudget);
            if (omitted > 0)
            {
                _logger.LogInformation(
                    "ai.chat.history_windowed conversation={ConversationId} sent={Sent} omitted={Omitted} budget_chars={Budget}",
                    conversationId, kept.Count, omitted, _chatOptions.HistoryCharBudget);
                msgs.Add(new LlmMessage { Role = "system", Content = HistoryOmittedNote(omitted) });
            }
            msgs.AddRange(kept);
        }

        msgs.Add(new LlmMessage { Role = "user", Content = userMessage });
        return msgs;
    }

    private async Task PersistConversationAsync(
        Guid conversationId, string? userMessage, Guid? agentId, string assistantContent,
        int inputTokens, int outputTokens, CancellationToken ct,
        IReadOnlyCollection<Guid>? loadedSkills = null)
    {
        try
        {
            var now = DateTime.UtcNow;
            var userIdStr = _caller.UserId.ToString();
            var newMessages = new List<object>
            {
                new { role = "user", content = userMessage ?? string.Empty },
                new { role = "assistant", content = assistantContent },
            };

            var conv = await _conversations.FindTrackedAsync(conversationId, ct);
            if (conv is not null)
            {
                // External (messaging) threads are owned by the channel/thread,
                // not a fixed user — the worker already authorized the bound
                // user. After an account re-link the originally-stamped UserId is
                // stale, so only enforce the ownership guard for web-sourced
                // conversations; refresh the owner on external ones so the field
                // tracks the current linked user.
                var isExternal = !string.IsNullOrEmpty(conv.Source)
                    && !string.Equals(conv.Source, "web", StringComparison.OrdinalIgnoreCase);
                if (!isExternal && conv.UserId != userIdStr)
                {
                    _logger.LogWarning(
                        "Conversation {Id} not owned by user {UserId}; skipping persist",
                        conversationId, _caller.UserId);
                    return;
                }
                if (isExternal && conv.UserId != userIdStr) conv.UserId = userIdStr;

                // Heal conversations created before the resolved-agent fix
                // (or whose first turn pre-dated an explicit agent pick):
                // stamp the agent that actually served this turn.
                if (conv.AgentId is null && agentId is not null) conv.AgentId = agentId;

                var existing = conv.Messages.ValueKind == JsonValueKind.Array
                    ? conv.Messages.EnumerateArray().Select(e => (object)e).ToList()
                    : new List<object>();
                existing.AddRange(newMessages);
                conv.Messages = JsonSerializer.SerializeToElement(existing);
                conv.TokenUsage = JsonSerializer.SerializeToElement(new { input = inputTokens, output = outputTokens });
                // Null is "nothing new this turn", never "forget".
                if (loadedSkills is not null)
                    conv.Context = ScopedSkillCatalog.ContextWithLoaded(conv.Context, loadedSkills);
                conv.UpdatedAt = now;
                await _conversations.SaveChangesAsync(ct);
                return;
            }

            // First message of a brand new conversation whose id was minted in
            // this turn — create with the caller-supplied id so the client's
            // view stays consistent with the backend.
            var created = new AIConversation
            {
                AIConversationId = conversationId,
                UserId = userIdStr,
                AgentId = agentId,
                Messages = JsonSerializer.SerializeToElement(newMessages),
                Context = loadedSkills is null
                    ? default
                    : ScopedSkillCatalog.ContextWithLoaded(default, loadedSkills),
                Status = "active",
                // This runner serves the web chat; the messaging worker
                // creates its own rows stamped with the channel provider
                // (MessagingIngestService). Stamping it explicitly means
                // `source` is never null — it used to be, and the isExternal
                // check below already treated null and "web" identically.
                Source = "web",
                TokenUsage = JsonSerializer.SerializeToElement(new { input = inputTokens, output = outputTokens }),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _conversations.Add(created);
            await _conversations.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist conversation {Id}", conversationId);
        }
    }
}
