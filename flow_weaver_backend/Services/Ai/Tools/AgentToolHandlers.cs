using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

namespace flow_weaver_backend.Services.Ai.Tools;

// Single source of truth for the agent tool handlers registered into the
// ToolRegistry at startup (see Program.cs). Kept here — not inline in
// Program.cs — so the classification-coverage test iterates the EXACT set that
// gets registered: adding a handler to this list is what makes the tool
// callable, and the test (ToolClassificationCoverageTests)
// then forces an explicit PermissionClassifier.Matrix + ToolCapabilityMap entry
// for it. Order is irrelevant.
public static class AgentToolHandlers
{
    public static readonly IReadOnlyList<Type> All = new[]
    {
        typeof(ListWorkflowsHandler),
        typeof(ListSnippetsHandler),
        // In-process snippet creation so the chatting user's admin role
        // propagates natively (network_enabled = admin-only). See the handler
        // header — this replaces the fragile execute_operation self-call path.
        typeof(CreateSnippetHandler),
        // App configuration (users / policies / vendor commands) — gated by
        // the dispatcher's per-request role check.
        typeof(CreateUserHandler),
        typeof(ListUsersHandler),
        typeof(SetUserRoleHandler),
        typeof(GrantResourcePermissionHandler),
        typeof(CreatePolicyHandler),
        typeof(ListPoliciesHandler),
        typeof(CreateVendorCommandHandler),
        typeof(UpdateVendorCommandHandler),
        typeof(DeleteVendorCommandHandler),
        typeof(ListCredentialsHandler),
        typeof(QueryDevicesHandler),
        typeof(CreateWorkflowPlanHandler),
        typeof(EvaluatePromptSufficiencyHandler),
        typeof(ValidateSshCommandsHandler),
        typeof(ListVendorCommandsHandler),
        typeof(FindCommandHandler),
        typeof(SimulateWorkflowRunHandler),
        typeof(MarkWorkflowReadyHandler),
        typeof(ListPlanFeaturesHandler),
        typeof(RunAcceptanceTestsHandler),
        typeof(ListApisHandler),
        typeof(DiscoverOperationsHandler),
        typeof(OperationDetailHandler),
        typeof(ExecuteOperationHandler),
        typeof(LoadSkillHandler),
        typeof(ListMcpServersHandler),
        typeof(DiscoverMcpToolsHandler),
        typeof(CallMcpToolHandler),
        typeof(GetRunDetailsHandler),
        typeof(GetStepLogsHandler),
        typeof(GetWorkflowDetailsHandler),
        typeof(UpdateWorkflowNodeConfigHandler),
        typeof(GenerateReportHandler),
        typeof(GitListRepositoriesHandler),
        typeof(GitListFilesHandler),
        typeof(GitReadFileHandler),
        typeof(GitWriteFileHandler),
        typeof(GitCommitPushHandler),
        typeof(GitPullHandler),
        typeof(GitDiffHandler),
        typeof(GitListWebhooksHandler),
        typeof(GitCreateWebhookHandler),
        typeof(GitCreateRemoteRepositoryHandler),
        // S15: drafts a Snippet body for a missing reference detected during
        // workflow import (wizard's "generate with AI" action).
        typeof(GenerateSnippetForImportHandler),
        // FU-3: chat-tool that translates a foreign workflow definition
        // to v1 + lists missing dependencies. Read-only; the chat user
        // is redirected to /workflows/import to commit.
        typeof(AnalyzeForeignWorkflowHandler),
        // Reads a file into structured data. Its byte source
        // is a stored report artifact rather than a conversation attachment,
        // because this product has no attachments and does have artifacts.
        typeof(ParseFileHandler),
    };

    // The tool NAMES behind `All`. Derived, never listed: PermissionClassifier's
    // header used to enumerate by hand which entries had no handler yet, and that
    // list was wrong (`create_snippet` was on it after its handler shipped). Anything
    // that needs to know which classifications are backed by a tool reads this.
    //
    // The handler is uninitialised rather than constructed: `Name` is a constant
    // getter on every handler, and constructing one would need the whole DI graph.
    // ToolClassificationCoverageTests reads the names the same way.
    public static readonly IReadOnlySet<string> RegisteredToolNames =
        All.Select(t => ((IToolHandler)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(t)).Name)
           .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
