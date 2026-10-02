using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace flow_weaver_backend.Data.Db;

// EF Core context for the flow-weaver PostgreSQL schema.
//
// Conventions:
//   - All entities declare their own <EntityName>Id PK (no shadow keys).
//   - JsonElement maps to PostgreSQL jsonb via a ValueConverter.
//   - List<string> / List<Guid> map to text[] / uuid[] natively (Npgsql).
//   - snake_case table names via ToTable(); columns keep their PascalCase
//     property names (no naming convention is applied).
//   - BaseModel (IsActive, CreatedAt, UpdatedAt) is configured once as a
//     loop over all derived types so we do not duplicate per-entity setup.
public class AppDbContext : DbContext
{
    // Encrypts Integration.AuthConfig at rest. DI always supplies it; contexts
    // built by hand (tests, tooling) without one store the config as-is.
    private readonly IntegrationAuthCipher? _integrationAuthCipher;

    public AppDbContext(DbContextOptions<AppDbContext> options, IntegrationAuthCipher? integrationAuthCipher = null)
        : base(options)
    {
        _integrationAuthCipher = integrationAuthCipher;
    }

    public bool EncryptsIntegrationAuth => _integrationAuthCipher is not null;

    // The model bakes the cipher into a value converter, so a context with a
    // cipher and one without must not share a cached model.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, CipherAwareModelCacheKeyFactory>();
    }

    internal sealed class CipherAwareModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), (context as AppDbContext)?._integrationAuthCipher, designTime);
    }

    // ─── Inventory ──────────────────────────────────────────────────
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DevicePool> DevicePools => Set<DevicePool>();
    public DbSet<InventorySource> InventorySources => Set<InventorySource>();
    public DbSet<Credential> Credentials => Set<Credential>();

    // ─── Workflow authoring ─────────────────────────────────────────
    public DbSet<Snippet> Snippets => Set<Snippet>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowTrigger> WorkflowTriggers => Set<WorkflowTrigger>();
    public DbSet<Skill> Skills => Set<Skill>();

    // ─── Runtime ────────────────────────────────────────────────────
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<StepRun> StepRuns => Set<StepRun>();

    // S16: materialized output of simulate_workflow_run. The PromotionService
    // draft→qa gate and MarkWorkflowReadyHandler read the latest row whose
    // SchemaHash still matches the workflow.
    public DbSet<SimulationResult> SimulationResults => Set<SimulationResult>();

    // ─── Integrations ───────────────────────────────────────────────
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<IntegrationAction> IntegrationActions => Set<IntegrationAction>();

    // ─── MCP servers ────────────────────────────────────────────────
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<McpTool> McpTools => Set<McpTool>();

    // ─── Policies (guardrails) ───────────────────────────────────────
    public DbSet<Policy> Policies => Set<Policy>();

    // Catalog of valid CLI commands per Netmiko device_type.
    // Read by VendorCommandValidator at workflow create/update time and
    // by the validate_ssh_commands AI tool.
    public DbSet<VendorCommand> VendorCommands => Set<VendorCommand>();

    // Git repository registrations. GitService clones
    // these to disk under FlowWeaverConfig:GitRoot and exposes
    // read/write/commit/push operations to controllers and AI tools.
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();

    // Inbound webhook receivers tied to GitRepositories. The receiver
    // controller is public (HMAC-verified) so these rows are looked up
    // by GitWebhookId without any auth context.
    public DbSet<GitWebhook> GitWebhooks => Set<GitWebhook>();
    public DbSet<GitWebhookDelivery> GitWebhookDeliveries => Set<GitWebhookDelivery>();

    // ─── AI ─────────────────────────────────────────────────────────
    public DbSet<AIProvider> AIProviders => Set<AIProvider>();
    public DbSet<AIAgent> AIAgents => Set<AIAgent>();
    public DbSet<AIConversation> AIConversations => Set<AIConversation>();

    // Sprint 7: DB-backed replacement for /Skills/*.md and /Specs/*.yaml.
    public DbSet<AiPromptSkill> AiPromptSkills => Set<AiPromptSkill>();
    public DbSet<AiApiSpec> AiApiSpecs => Set<AiApiSpec>();

    // ─── Messaging channels (Slack/Teams/WhatsApp/Telegram) ─────────
    public DbSet<MessagingChannel> MessagingChannels => Set<MessagingChannel>();
    public DbSet<MessagingIdentityLink> MessagingIdentityLinks => Set<MessagingIdentityLink>();
    public DbSet<MessagingInboundEvent> MessagingInboundEvents => Set<MessagingInboundEvent>();
    public DbSet<MessagingDelivery> MessagingDeliveries => Set<MessagingDelivery>();
    public DbSet<MessagingLinkToken> MessagingLinkTokens => Set<MessagingLinkToken>();

    // ─── Outbound email (SMTP relays) ───────────────────────────────
    public DbSet<EmailChannel> EmailChannels => Set<EmailChannel>();

    // ─── Python snippet sandbox ─────────────────────────────────────
    // Admin-managed allow-list of extra importable modules.
    public DbSet<AllowedPythonModule> AllowedPythonModules => Set<AllowedPythonModule>();

    // ─── Appearance ─────────────────────────────────────────────────
    // User-authored colour themes offered next to the built-in fw-* ones.
    public DbSet<Theme> Themes => Set<Theme>();

    // ─── Governance ─────────────────────────────────────────────────
    public DbSet<WorkflowPlan> WorkflowPlans => Set<WorkflowPlan>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    // S16: feature-by-feature progress for the agent constructor and the
    // workflow import wizard.
    public DbSet<PlanFeature> PlanFeatures => Set<PlanFeature>();

    // S16: scratchpad rows the agent reuses across chat turns.
    public DbSet<AgentScratch> AgentScratches => Set<AgentScratch>();

    // S16: assertions that gate "ready to promote". The runner reads
    // Inputs + Assertions, executes the workflow against QA Lab, and
    // records the outcome.
    public DbSet<WorkflowAcceptanceTest> WorkflowAcceptanceTests => Set<WorkflowAcceptanceTest>();

    // Application-wide settings singleton (see AppSetting.SingletonId).
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthEvent> AuthEvents => Set<AuthEvent>();

    // Sprint 8: secret store consumed by the agent's
    // ExecuteOperation tool and REST spec executor via ${secret:...} refs.
    public DbSet<Secret> Secrets => Set<Secret>();

    // Sprint 9: append-only action trace. ITraceLogger writes here from
    // every critical path; a BackgroundService prunes rows older than
    // Tracing:RetentionDays.
    public DbSet<TraceEvent> TraceEvents => Set<TraceEvent>();

    // Sprint 5.7: Domain audit trail (distinct from auth-specific AuthEvents).
    public DbSet<AuditEvent> AuditLogs => Set<AuditEvent>();

    // Sprint 11: generated report files persisted for audit. Body lives
    // as bytea (gzipped for html/csv/pdf); ReportRetentionHostedService
    // purges expired rows.
    public DbSet<ReportArtifact> ReportArtifacts => Set<ReportArtifact>();

    // Per-resource RBAC. Grants a subject one of
    // owner/editor/runner/viewer on a single Workflow or Integration.
    // The global Admin/Operator/Viewer tier still applies as a floor.
    public DbSet<ResourcePermission> ResourcePermissions => Set<ResourcePermission>();

    // RBAC-granular refactor: capability-based grants
    // binding users to CapabilityCatalog keys under optional ABAC conditions.
    // Supersedes the coarse 3-tier model; the two "builtin.*" grants reproduce
    // the legacy operator/viewer bundles.
    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Round-trips JsonElement ↔ jsonb via raw JSON text. Works uniformly
        // across Npgsql versions and avoids the default "as string" behavior
        // that would escape nested JSON.
        var jsonElementConverter = new ValueConverter<JsonElement, string>(
            v => v.ValueKind == JsonValueKind.Undefined ? "null" : v.GetRawText(),
            v => JsonDocument.Parse(string.IsNullOrEmpty(v) ? "null" : v, default).RootElement);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(JsonElement))
                {
                    property.SetValueConverter(jsonElementConverter);
                    property.SetColumnType("jsonb");
                }
            }
        }

        if (_integrationAuthCipher is { } cipher)
        {
            modelBuilder.Entity<Integration>()
                .Property(x => x.AuthConfig)
                .HasConversion(new ValueConverter<JsonElement, string>(
                    v => cipher.Protect(v),
                    v => cipher.Unprotect(v)));
        }

        // ─── Tables + PKs ───────────────────────────────────────────

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => new { x.SourceId, x.ExternalId })
                .IsUnique()
                .HasFilter("\"SourceId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");
        });

        modelBuilder.Entity<DevicePool>(e =>
        {
            e.ToTable("device_pools");
            e.HasKey(x => x.DevicePoolId);
        });

        modelBuilder.Entity<InventorySource>(e =>
        {
            e.ToTable("inventory_sources");
            e.HasKey(x => x.InventorySourceId);
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.ToTable("credentials");
            e.HasKey(x => x.CredentialId);
        });

        modelBuilder.Entity<Snippet>(e =>
        {
            // Idempotency must be one of the three known values
            // or null (= fall back to the handler default). Enforced by
            // a CHECK constraint so a typo at the API layer fails loud.
            e.ToTable("snippets", t =>
            {
                t.HasCheckConstraint(
                    "CK_snippets_Idempotency",
                    "\"Idempotency\" IS NULL OR \"Idempotency\" IN ('idempotent', 'requires_compensation', 'non_reversible')");
            });
            e.HasKey(x => x.SnippetId);
            e.HasIndex(x => x.Type);
            // Cross-instance identity — see Snippet.Slug. Unique so an
            // imported bundle can never bind to an ambiguous row.
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<Workflow>(e =>
        {
            e.ToTable("workflows");
            e.HasKey(x => x.WorkflowId);
            e.HasIndex(x => x.Environment);
        });

        modelBuilder.Entity<WorkflowVersion>(e =>
        {
            e.ToTable("workflow_versions");
            e.HasKey(x => x.WorkflowVersionId);
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<WorkflowTrigger>(e =>
        {
            e.ToTable("workflow_triggers");
            e.HasKey(x => x.WorkflowTriggerId);
            e.HasIndex(x => x.WorkflowId);
            e.HasIndex(x => new { x.Enabled, x.NextRunAt });
        });

        modelBuilder.Entity<Skill>(e =>
        {
            e.ToTable("skills");
            e.HasKey(x => x.SkillId);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.JobId);
            // Critical index: the queue claim query filters by status + tag
            // and orders by priority + created_at. Required for SKIP LOCKED
            // to scale past a few workers.
            e.HasIndex(x => new { x.Status, x.Tag, x.Priority, x.CreatedAt });
            // Reclaim sweeper filter: WHERE Status='claimed' AND LeaseExpiresAt<NOW().
            e.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
        });

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.ToTable("workflow_runs");
            e.HasKey(x => x.WorkflowRunId);
            e.HasIndex(x => x.WorkflowId);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<SimulationResult>(e =>
        {
            e.ToTable("simulation_results");
            e.HasKey(x => x.SimulationResultId);
            // Hot-path lookup: PromotionService and mark_workflow_ready
            // pull the latest row for a workflow, filtered by SchemaHash.
            e.HasIndex(x => new { x.WorkflowId, x.SchemaHash, x.SimulatedAt })
                .IsDescending(false, false, true);
        });

        modelBuilder.Entity<StepRun>(e =>
        {
            e.ToTable("step_runs");
            e.HasKey(x => x.StepRunId);
            e.HasIndex(x => x.WorkflowRunId);
            // Backs GetCompletedStatsBySnippetAsync, which the snippet list runs
            // on every load of the workflow editor palette. Without it that is a
            // sequential scan of the whole step history on the hottest page.
            e.HasIndex(x => x.SnippetId);
        });

        modelBuilder.Entity<Integration>(e =>
        {
            e.ToTable("integrations");
            e.HasKey(x => x.IntegrationId);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<IntegrationAction>(e =>
        {
            e.ToTable("integration_actions");
            e.HasKey(x => x.IntegrationActionId);
            e.HasIndex(x => x.IntegrationId);
        });

        modelBuilder.Entity<McpServer>(e =>
        {
            e.ToTable("mcp_servers");
            e.HasKey(x => x.McpServerId);
            e.HasIndex(x => x.Enabled);
        });

        modelBuilder.Entity<McpTool>(e =>
        {
            e.ToTable("mcp_tools");
            e.HasKey(x => x.McpToolId);
            e.HasIndex(x => x.McpServerId);
            // A server exposes each tool name once; the tools/list upsert relies
            // on this to detect existing rows.
            e.HasIndex(x => new { x.McpServerId, x.Name })
                .IsUnique()
                .HasFilter("\"IsActive\" = true");
        });

        modelBuilder.Entity<Policy>(e =>
        {
            e.ToTable("policies");
            e.HasKey(x => x.PolicyId);
            e.HasIndex(x => x.Enabled);
        });

        modelBuilder.Entity<VendorCommand>(e =>
        {
            e.ToTable("vendor_commands");
            e.HasKey(x => x.VendorCommandId);
            // Hot-path lookup: validator scans by (device_type, kind).
            e.HasIndex(x => new { x.DeviceType, x.Kind });
            // Avoid duplicate (device_type, kind, value) entries — the seed
            // must be idempotent and the UI must reject doubles.
            e.HasIndex(x => new { x.DeviceType, x.Kind, x.Value })
                .IsUnique()
                .HasFilter("\"IsActive\" = true");
        });

        modelBuilder.Entity<GitRepository>(e =>
        {
            e.ToTable("git_repositories");
            e.HasKey(x => x.GitRepositoryId);
            e.HasIndex(x => x.Name).IsUnique()
                .HasFilter("\"IsActive\" = true");
        });

        modelBuilder.Entity<GitWebhook>(e =>
        {
            e.ToTable("git_webhooks");
            e.HasKey(x => x.GitWebhookId);
            e.HasIndex(x => x.GitRepositoryId);
        });

        modelBuilder.Entity<GitWebhookDelivery>(e =>
        {
            e.ToTable("git_webhook_deliveries");
            e.HasKey(x => x.GitWebhookDeliveryId);
            e.HasIndex(x => new { x.GitWebhookId, x.At }).IsDescending(false, true);
        });

        modelBuilder.Entity<AIProvider>(e =>
        {
            e.ToTable("ai_providers");
            e.HasKey(x => x.AIProviderId);
        });

        modelBuilder.Entity<AIAgent>(e =>
        {
            e.ToTable("ai_agents");
            e.HasKey(x => x.AIAgentId);
        });

        modelBuilder.Entity<AIConversation>(e =>
        {
            e.ToTable("ai_conversations");
            e.HasKey(x => x.AIConversationId);
            e.HasIndex(x => x.AgentId);
            // Resolve an inbound external thread back to its conversation each turn.
            e.HasIndex(x => new { x.MessagingChannelId, x.ExternalThreadId });
        });

        // ─── Python snippet sandbox ─────────────────────────────────────
        modelBuilder.Entity<AllowedPythonModule>(e =>
        {
            e.ToTable("allowed_python_modules");
            e.HasKey(x => x.AllowedPythonModuleId);
            // One row per import name. The unique index counts
            // inactive rows too, so the service hard-deletes on removal.
            e.HasIndex(x => x.ImportName).IsUnique();
        });

        // ─── Appearance ─────────────────────────────────────────────────
        modelBuilder.Entity<Theme>(e =>
        {
            e.ToTable("themes");
            e.HasKey(x => x.ThemeId);
            // The picker's query is "everything shared, plus mine" — one
            // index serves both halves of that OR.
            e.HasIndex(x => x.IsShared);
            e.HasIndex(x => x.OwnerUserId);
        });

        // ─── Messaging channels ─────────────────────────────────────────
        modelBuilder.Entity<MessagingChannel>(e =>
        {
            e.ToTable("messaging_channels");
            e.HasKey(x => x.MessagingChannelId);
            e.HasIndex(x => x.Provider);
        });

        modelBuilder.Entity<MessagingIdentityLink>(e =>
        {
            e.ToTable("messaging_identity_links");
            e.HasKey(x => x.MessagingIdentityLinkId);
            // One internal user per external identity within a channel. The
            // service coalesces a null workspace to "" so the unique index bites
            // for providers without a workspace concept (Telegram).
            e.HasIndex(x => new { x.MessagingChannelId, x.ExternalWorkspaceId, x.ExternalUserId })
                .IsUnique();
            e.HasIndex(x => x.LinkedUserId);
        });

        modelBuilder.Entity<MessagingInboundEvent>(e =>
        {
            e.ToTable("messaging_inbound_events");
            e.HasKey(x => x.MessagingInboundEventId);
            // Idempotency: a provider re-delivery with the same event id is dropped.
            e.HasIndex(x => new { x.MessagingChannelId, x.ProviderEventId }).IsUnique();
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<MessagingDelivery>(e =>
        {
            e.ToTable("messaging_deliveries");
            e.HasKey(x => x.MessagingDeliveryId);
            e.HasIndex(x => new { x.MessagingChannelId, x.At });
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<MessagingLinkToken>(e =>
        {
            e.ToTable("messaging_link_tokens");
            e.HasKey(x => x.MessagingLinkTokenId);
            e.HasIndex(x => x.TokenHash);
            // Retention sweeper deletes expired tokens (F7).
            e.HasIndex(x => x.ExpiresAt);
        });

        // ─── Outbound email (SMTP relays) ───────────────────────────────
        modelBuilder.Entity<EmailChannel>(e =>
        {
            e.ToTable("email_channels");
            e.HasKey(x => x.EmailChannelId);
            // The worker resolves the fallback channel on every email_send
            // step that doesn't name one.
            e.HasIndex(x => new { x.IsDefault, x.Enabled });
            e.HasIndex(x => x.Provider);
        });

        // Sprint 7: DB-backed replacement for /Skills/*.md and /Specs/*.yaml.
        modelBuilder.Entity<AiPromptSkill>(e =>
        {
            e.ToTable("ai_prompt_skills");
            e.HasKey(x => x.AiPromptSkillId);
            // Name is the lookup key (e.g. "base.md") and is globally unique.
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.IsActive, x.SortOrder, x.Name });
            // Catalog pages filter by integration (global vs scoped); the
            // agent joins through this key when deciding which credentials
            // to use for a spec's operations.
            e.HasIndex(x => x.IntegrationId);
            e.Property(x => x.Content).HasColumnType("text");
        });

        modelBuilder.Entity<AiApiSpec>(e =>
        {
            e.ToTable("ai_api_specs");
            e.HasKey(x => x.AiApiSpecId);
            e.HasIndex(x => x.Api).IsUnique();
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.IntegrationId);
            e.Property(x => x.Content).HasColumnType("text");
        });

        modelBuilder.Entity<WorkflowPlan>(e =>
        {
            e.ToTable("workflow_plans");
            e.HasKey(x => x.WorkflowPlanId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ConversationId);
        });

        modelBuilder.Entity<AgentRun>(e =>
        {
            e.ToTable("agent_runs");
            e.HasKey(x => x.AgentRunId);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.TraceId);
        });

        modelBuilder.Entity<PlanFeature>(e =>
        {
            e.ToTable("plan_features", t =>
            {
                t.HasCheckConstraint(
                    "CK_plan_features_Status",
                    "\"Status\" IN ('pending', 'in_progress', 'verified', 'rejected', 'skipped')");
            });
            e.HasKey(x => x.PlanFeatureId);
            // Common lookup: rebuild the checklist for a plan / import token.
            e.HasIndex(x => new { x.WorkflowPlanId, x.Ordinal });
            e.HasIndex(x => new { x.ImportToken, x.Ordinal });
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<AgentScratch>(e =>
        {
            e.ToTable("agent_scratches");
            e.HasKey(x => x.AgentScratchId);
            // Upsert key: (ConversationId, Key) is unique.
            e.HasIndex(x => new { x.ConversationId, x.Key }).IsUnique()
                .HasFilter("\"IsActive\" = true");
            e.HasIndex(x => x.WorkflowPlanId);
        });

        modelBuilder.Entity<WorkflowAcceptanceTest>(e =>
        {
            e.ToTable("workflow_acceptance_tests", t =>
            {
                t.HasCheckConstraint(
                    "CK_workflow_acceptance_tests_LastStatus",
                    "\"LastStatus\" IS NULL OR \"LastStatus\" IN ('passed', 'failed', 'error', 'skipped', 'running')");
            });
            e.HasKey(x => x.WorkflowAcceptanceTestId);
            e.HasIndex(x => new { x.WorkflowId, x.IsActive });
            e.HasIndex(x => x.LastStatus);
        });

        // ─── Auth ───────────────────────────────────────────────────


        modelBuilder.Entity<AppSetting>(e =>
        {
            e.ToTable("app_settings");
            // Fixed key: the application never generates one, it always writes
            // AppSetting.SingletonId, so a single row is enforced by the PK.
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RbacMode).IsRequired();
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.UserId);
            // Username is the login handle and is globally unique.
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.RefreshTokenId);
            // TokenHash is the lookup key for refresh calls. Globally unique
            // (SHA256 collision = cryptographic failure, not data model).
            e.HasIndex(x => x.TokenHash).IsUnique();
            // Chain cleanup: find every active token for a user quickly.
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
        });

        modelBuilder.Entity<AuthEvent>(e =>
        {
            e.ToTable("auth_events");
            e.HasKey(x => x.AuthEventId);
            // Admin queries filter by time window, optionally by user.
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserId, x.At });
            // The retention sweeper deletes by age alone, which the
            // composite index above can't serve.
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<Secret>(e =>
        {
            e.ToTable("secrets");
            e.HasKey(x => x.SecretId);
            // Name is the lookup key used by ${secret:secret:<name>:value};
            // must be unique so templates resolve deterministically.
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<TraceEvent>(e =>
        {
            e.ToTable("trace_events");
            e.HasKey(x => x.TraceEventId);
            // Default timeline. `At DESC` matches the admin list UI.
            e.HasIndex(x => x.At).IsDescending(true);
            // Filter-by-action and filter-by-user patterns both need an index
            // on (discriminator, time) so pagination stays fast as the table grows.
            e.HasIndex(x => new { x.Action, x.At }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserId, x.At }).IsDescending(false, true);
            // Correlation join key — rarely filtered alone, but when we do
            // it's for a single request so equality lookup is cheap.
            e.HasIndex(x => x.RequestId);
        });

        // Sprint 5.7: Domain audit log for entity mutations.
        //
        // Every index below is shaped after a filter /api/audit/events
        // actually offers. A bare EntityType index would be useless —
        // ~20 distinct values across the table, so the planner ignores it
        // and the entity_type filter degrades into a scan as the table
        // grows — hence every filter is paired with At.
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.AuditEventId);
            // Default listing: full timeline, newest first.
            e.HasIndex(x => x.At).IsDescending(true);
            // ?entity_type=… — paired with At so the low-cardinality column
            // is only ever probed inside a time slice.
            e.HasIndex(x => new { x.EntityType, x.At }).IsDescending(false, true);
            // ?entity_id=… — "everything that happened to workflow X".
            e.HasIndex(x => new { x.EntityId, x.At }).IsDescending(false, true);
            // ?user_id=…
            e.HasIndex(x => new { x.UserId, x.At }).IsDescending(false, true);
            // Correlation join against trace_events / the logs, which index
            // RequestId the same way.
            e.HasIndex(x => x.RequestId);
        });

        // Sprint 11: persisted report files. Indexed for the admin list
        // view's common filters (per user, per format, per source) and
        // for the retention sweeper which scans by ExpiresAt on active rows.
        modelBuilder.Entity<ReportArtifact>(e =>
        {
            e.ToTable("report_artifacts");
            e.HasKey(x => x.ReportArtifactId);
            e.HasIndex(x => x.CreatedAt).IsDescending(true);
            e.HasIndex(x => new { x.UserId, x.CreatedAt }).IsDescending(false, true);
            e.HasIndex(x => x.Format);
            e.HasIndex(x => x.Source);
            e.HasIndex(x => x.AgentConversationId);
            e.HasIndex(x => x.WorkflowRunId);
            // Retention sweeper — partial index keeps it lean.
            e.HasIndex(x => x.ExpiresAt)
                .HasFilter("\"IsActive\" = true");
        });

        modelBuilder.Entity<ResourcePermission>(e =>
        {
            e.ToTable("resource_permissions", t =>
            {
                // Constrain ResourceType to the values the service knows
                // about so a typo in a future call site can't slip an
                // unknown value into the table.
                t.HasCheckConstraint(
                    "CK_resource_permissions_ResourceType",
                    "\"ResourceType\" IN ('workflow', 'integration')");
                // Same idea for Role: the analyzer ranks by string match,
                // and an unknown value silently degrades to "no privilege".
                t.HasCheckConstraint(
                    "CK_resource_permissions_Role",
                    "\"Role\" IN ('owner', 'editor', 'runner', 'viewer')");
            });
            e.HasKey(x => x.ResourcePermissionId);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasIndex(x => new { x.ResourceType, x.ResourceId });
            e.HasIndex(x => new { x.SubjectType, x.SubjectId });
            // Avoid duplicate grants for the same (resource, subject, role).
            // Explicit name keeps the identifier under Postgres' 63-char
            // limit; auto-generated names get truncated and are harder
            // to reference in DBA tooling.
            e.HasIndex(x => new { x.ResourceType, x.ResourceId, x.SubjectType, x.SubjectId, x.Role })
                .IsUnique()
                .HasFilter("\"IsActive\" = true")
                .HasDatabaseName("UX_resource_permissions_unique_grant");
        });

        modelBuilder.Entity<PermissionGrant>(e =>
        {
            e.ToTable("permission_grants");
            e.HasKey(x => x.PermissionGrantId);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.Enabled).HasDefaultValue(true);
            // Resolver hot-path: load the enabled grants.
            e.HasIndex(x => x.Enabled);
            // Seeder/sync look built-in bundles up by (Name).
            e.HasIndex(x => x.Name);
        });

    }
}
