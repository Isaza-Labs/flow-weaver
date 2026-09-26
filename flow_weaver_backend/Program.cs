using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.SystemConsole.Themes;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.IntegrationAction;
using flow_weaver_backend.Services.Ai.Skills;
using flow_weaver_backend.Services.Ai.Specs;
using flow_weaver_backend.Services.AIAgent;
using flow_weaver_backend.Services.AIProvider;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Credential;
using flow_weaver_backend.Services.Device;
using flow_weaver_backend.Services.DevicePool;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.InventorySource;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Utils.Report;
using flow_weaver_backend.Utils.Report.Exporters;
using flow_weaver_backend.Services.Snippet;
using flow_weaver_backend.Services.Skill;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Job;
using flow_weaver_backend.Services.StepRun;
using flow_weaver_backend.Services.Workflow;
using flow_weaver_backend.Services.WorkflowPlan;
using flow_weaver_backend.Services.WorkflowRun;
using flow_weaver_backend.Services.WorkflowTrigger;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.WorkflowVersion;

var builder = WebApplication.CreateBuilder(args);

// Serilog — console sink format depends on LOG_FORMAT env var:
//   pretty → colored, human-readable lines (default in Development)
//   json   → CLEF one-line-per-event (default in Production; ideal for
//            aggregators like Loki/Elastic that parse structured fields)
// Enrichers hang request metadata off every line via LogContext;
// CorrelationMiddleware pushes those properties per request.
//
// Verbosity knobs (all env vars, parsed as Serilog LogEventLevel —
// values: Verbose, Debug, Information, Warning, Error, Fatal):
//
//   LOG_EF_SQL       → verbosity of Microsoft.EntityFrameworkCore.Database.Command.
//                      Default Warning (silent in steady state). Set to
//                      Information to see every SQL query with timing;
//                      useful for debugging N+1, timeouts, or row counts.
//                      Use sparingly — a single API call produces dozens
//                      of SELECT lines at Information.
//
//   LOG_ACTIONS      → verbosity of the application services tree
//                      flow_weaver_backend.Services.* as a whole.
//                      Covers: Engine, Worker, Workflow, WorkflowRun,
//                      StepRun, Job, WorkflowPlan, Snippet, Device,
//                      DevicePool, Credential, Integration, IntegrationAction,
//                      InventorySource, Skill, AIAgent, AIProvider,
//                      WorkflowTrigger, WorkflowVersion, Auth, Policy,
//                      Ai.RestExecutor, Ai.Secrets. The "what is the
//                      platform doing right now" logs: workflow starts,
//                      step dispatches, handler begin/end, HTTP calls to
//                      integrations, credential resolves, auth events.
//                      Default Information; set to Debug for deep traces
//                      (iteration counts, per-device loops, secret-hit
//                      bookkeeping, policy decisions), Warning to silence
//                      almost everything except state changes + errors.
//                      Does NOT affect `Services.Ai` as a namespace —
//                      that subtree is hardcoded to Debug for AI chat
//                      visibility and wins by longest-prefix match.
//
//   LOG_FORMAT       → pretty | json (see above).
//   AI_LOG_PAYLOADS  → true to dump raw SSE chunks from the LLM providers.
//                      Very noisy, triage-only.
//
// To parse an env var with a safe default, we use Enum.TryParse with
// ignoreCase=true. An unrecognised value falls back to the default so
// typos don't accidentally silence logs.
builder.Host.UseSerilog((context, services, cfg) =>
{
    var logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT")?.Trim().ToLowerInvariant();
    var pretty = logFormat switch
    {
        "pretty" or "text" or "console" => true,
        "json" or "clef" or "compact" => false,
        _ => context.HostingEnvironment.IsDevelopment(),
    };

    // EF SQL commands — loud at Information, near-silent at Warning.
    // Default Warning because the steady-state noise drowns the action
    // logs the user actually cares about (see the log sample that
    // triggered this knob). Flip to Information when hunting query bugs.
    var efSqlLevel = ParseLogLevel(
        Environment.GetEnvironmentVariable("LOG_EF_SQL"), LogEventLevel.Warning);

    // Application action subtree — default Information gives you the
    // "workflow X started", "step Y dispatched", "handler Z finished"
    // timeline. Bumping to Debug adds per-iteration / per-device lines
    // from the orchestrator + worker; useful when a step misbehaves.
    var actionsLevel = ParseLogLevel(
        Environment.GetEnvironmentVariable("LOG_ACTIONS"), LogEventLevel.Information);

    cfg
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", efSqlLevel)
        .MinimumLevel.Override("flow_weaver_backend", LogEventLevel.Information)
        // LOG_ACTIONS governs the whole application-services tree
        // (workflow/worker/engine/integration/device/credential/auth/policy/…).
        // Serilog resolves overrides by longest prefix match, so the
        // more-specific `.Services.Ai` Debug override below still wins
        // for the AI chat subtree even if LOG_ACTIONS is set to Warning.
        .MinimumLevel.Override("flow_weaver_backend.Services", actionsLevel)
        .MinimumLevel.Override("flow_weaver_backend.Services.Ai", LogEventLevel.Debug)
        .MinimumLevel.Override("flow_weaver_backend.Controllers.AiChatController", LogEventLevel.Debug)
        .MinimumLevel.Override("flow_weaver_backend.BackgroundServices", LogEventLevel.Debug)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("service", "flow_weaver_backend")
        .Enrich.WithProperty("env", context.HostingEnvironment.EnvironmentName);

    if (pretty)
    {
        // {SourceContext} shows the emitting class; short_ctx is the last
        // segment only so long namespaces don't drown the line. See the
        // Destructure.ByTransforming call below for how it's produced.
        const string template =
            "[{Timestamp:HH:mm:ss} {Level:u3}] {ShortContext}{CallerTag} {Message:lj}{NewLine}{Exception}";
        cfg.Enrich.With<ShortContextEnricher>()
           .Enrich.With<CallerTagEnricher>()
           .WriteTo.Console(
                outputTemplate: template,
                theme: AnsiConsoleTheme.Code,
                applyThemeToRedirectedOutput: true);
    }
    else
    {
        cfg.WriteTo.Console(new CompactJsonFormatter());
    }
});

// Parse a Serilog LogEventLevel from an env var with a safe default.
// Case-insensitive; empty / whitespace / unrecognised value falls
// through to `fallback` (never throws). Declared as a local function
// so it lives next to the UseSerilog block that consumes it.
static LogEventLevel ParseLogLevel(string? raw, LogEventLevel fallback)
{
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    return Enum.TryParse<LogEventLevel>(raw.Trim(), ignoreCase: true, out var parsed)
        ? parsed
        : fallback;
}

// Sprint 2.8: worker-only mode. When WORKER_ONLY=true (or --worker CLI
// flag), skip the entire HTTP surface (controllers, OpenAPI, auth
// middleware, CORS, rate limiter). The process only runs the DB +
// engine + worker BackgroundService + handlers. Saves ~40 MB RSS and
// avoids binding port 8080 so the container can coexist with the API
// on the same host.
var workerOnly = builder.Configuration.GetValue<bool>("WorkerOnly")
    || args.Contains("--worker");

// Add services to the container.

if (!workerOnly)
{
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            // Guards against the InvalidOperationException thrown by the
            // default JsonElement writer when a property is left in its
            // uninitialized (Undefined) state — usually produced by
            // `dto.X ?? default` in service mappers. Treat Undefined as
            // JSON null so the HTTP response never 500s after a successful
            // SaveChanges.
            options.JsonSerializerOptions.Converters.Add(
                new flow_weaver_backend.Services.Security.SafeJsonElementConverter());
        });

    // RFC 7807 error contract. Domain failures (ValidationException,
    // NotFoundException, ConflictException, …) thrown anywhere in the
    // request pipeline are caught by DomainExceptionHandler and emitted
    // as problem+json with status/title/detail plus a top-level `error`
    // field for the legacy frontend reader and `code` for new clients.
    builder.Services.AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions["request_id"] =
                ctx.HttpContext.Response.Headers[
                    flow_weaver_backend.Services.Observability.CorrelationMiddleware.RequestIdHeader]
                .ToString();
        };
    });
    builder.Services.AddExceptionHandler<flow_weaver_backend.Services.Errors.DomainExceptionHandler>();
}

// OpenAPI document + metadata (not needed in worker-only mode).
if (!workerOnly)
{
    builder.Services.AddOpenApi("v1", options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new Microsoft.OpenApi.OpenApiInfo
            {
                Title = "Flow Weaver Backend API",
                Version = "v1",
                Description = """
                    REST API for the Flow Weaver network-automation platform.
                    Covers device/credential inventory, workflows, triggers,
                    governance plans, integrations, AI providers/agents, and the
                    file-backed skills + specs catalog under /api/ai/catalog.
                    """,
                Contact = new Microsoft.OpenApi.OpenApiContact
                {
                    Name = "Flow Weaver",
                    Email = "info@networkingdev.com",
                },
            };
            return Task.CompletedTask;
        });
    });
}

// Reads from ConnectionStrings:DefaultConnection in appsettings*.json, or from the
// ConnectionStrings__DefaultConnection env var (set by docker-compose).
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Restores the client address from X-Forwarded-For for proxies the operator
// declared trusted. Without it every browser request looks like it came from
// the frontend container. See ForwardedHeadersConfiguration.
builder.Services.AddForwardedHeadersFromConfig(builder.Configuration);

// Sprint 5.5: Health checks — /health/live (liveness, no checks) and
// /health/ready (readiness, verifies DB connectivity).
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database");

var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"]
                  ?? Path.Combine(builder.Environment.ContentRootPath, "keyring");
Directory.CreateDirectory(keyRingPath);

// S13.4: register the active KMS wrapper. Default "filesystem" is a
// passthrough (keys persist plaintext on disk — pre-S13.4 behaviour).
// "aws-kms" wraps every persisted key with AWS KMS Encrypt; the KMS
// key id is required via DataProtection:Aws:KeyId.
var kmsProvider = (builder.Configuration["DataProtection:KmsProvider"] ?? "filesystem")
    .Trim().ToLowerInvariant();
switch (kmsProvider)
{
    case "aws-kms":
        builder.Services.AddSingleton<flow_weaver_backend.Services.Security.Kms.IKmsKeyWrapper,
            flow_weaver_backend.Services.Security.Kms.AwsKmsKeyWrapper>();
        break;
    case "filesystem":
    case "":
    case "none":
        builder.Services.AddSingleton<flow_weaver_backend.Services.Security.Kms.IKmsKeyWrapper,
            flow_weaver_backend.Services.Security.Kms.NoOpKmsKeyWrapper>();
        break;
    default:
        throw new InvalidOperationException(
            $"DataProtection:KmsProvider '{kmsProvider}' is not supported. " +
            "Allowed: filesystem (default), aws-kms.");
}

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("flow-weaver-backend");

// Wire the KMS-aware encryptor only when a real provider is configured.
// The no-op path leaves keys plaintext so existing keyring files stay
// readable without an upgrade dance.
if (kmsProvider != "filesystem" && kmsProvider != "none" && kmsProvider != "")
{
    builder.Services.AddSingleton<flow_weaver_backend.Services.Security.Kms.KmsXmlEncryptor>();
    builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
        .Configure<flow_weaver_backend.Services.Security.Kms.KmsXmlEncryptor>(
            (opts, encryptor) => opts.XmlEncryptor = encryptor);
}

builder.Services.AddScoped<ICredentialEncryptionService, CredentialEncryptionService>();

// ─── Repositories (data-access layer; keeps AppDbContext out of services) ────
// Open-generic fallback for entities with no special persistence logic, plus
// entity-specific repositories where queries or error-translation are
// non-trivial. Services depend on these interfaces instead of AppDbContext so
// the business logic can move to a library without dragging EF Core along.
builder.Services.AddScoped(
    typeof(flow_weaver_backend.Data.Repositories.IRepository<>),
    typeof(flow_weaver_backend.Data.Repositories.RepositoryBase<>));
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IDeviceRepository,
    flow_weaver_backend.Data.Repositories.DeviceRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IWorkflowRepository,
    flow_weaver_backend.Data.Repositories.WorkflowRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IWorkflowVersionRepository,
    flow_weaver_backend.Data.Repositories.WorkflowVersionRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IWorkflowTriggerRepository,
    flow_weaver_backend.Data.Repositories.WorkflowTriggerRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IWorkflowRunRepository,
    flow_weaver_backend.Data.Repositories.WorkflowRunRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IStepRunRepository,
    flow_weaver_backend.Data.Repositories.StepRunRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IJobRepository,
    flow_weaver_backend.Data.Repositories.JobRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.ISnippetRepository,
    flow_weaver_backend.Data.Repositories.SnippetRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IVendorCommandRepository,
    flow_weaver_backend.Data.Repositories.VendorCommandRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAiPromptSkillRepository,
    flow_weaver_backend.Data.Repositories.AiPromptSkillRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAiApiSpecRepository,
    flow_weaver_backend.Data.Repositories.AiApiSpecRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAppSettingsRepository,
    flow_weaver_backend.Data.Repositories.AppSettingsRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IPolicyEvaluatorRepository,
    flow_weaver_backend.Data.Repositories.PolicyEvaluatorRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.ISloRepository,
    flow_weaver_backend.Data.Repositories.SloRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IResourcePermissionRepository,
    flow_weaver_backend.Data.Repositories.ResourcePermissionRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IUserRepository,
    flow_weaver_backend.Data.Repositories.UserRepository>();
// The three observability writers are Singleton, not Scoped: they depend only
// on IServiceScopeFactory and open their own short-lived AppDbContext per
// write. Registering them as Scoped would compile fine but invites the
// regression they exist to prevent — someone injecting the ambient
// AppDbContext back in and re-coupling audit writes to the caller's
// transaction. The lifetime makes that impossible.
builder.Services.AddSingleton<flow_weaver_backend.Data.Repositories.IAuditEventRepository,
    flow_weaver_backend.Data.Repositories.AuditEventRepository>();
builder.Services.AddSingleton<flow_weaver_backend.Data.Repositories.IAuthEventRepository,
    flow_weaver_backend.Data.Repositories.AuthEventRepository>();
builder.Services.AddSingleton<flow_weaver_backend.Data.Repositories.ITraceEventRepository,
    flow_weaver_backend.Data.Repositories.TraceEventRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IRefreshTokenRepository,
    flow_weaver_backend.Data.Repositories.RefreshTokenRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IIntegrationRepository,
    flow_weaver_backend.Data.Repositories.IntegrationRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IIntegrationActionRepository,
    flow_weaver_backend.Data.Repositories.IntegrationActionRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IMcpServerRepository,
    flow_weaver_backend.Data.Repositories.McpServerRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IMcpToolRepository,
    flow_weaver_backend.Data.Repositories.McpToolRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IPlanFeatureRepository,
    flow_weaver_backend.Data.Repositories.PlanFeatureRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.ISimulationResultRepository,
    flow_weaver_backend.Data.Repositories.SimulationResultRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IWorkflowAcceptanceTestRepository,
    flow_weaver_backend.Data.Repositories.WorkflowAcceptanceTestRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.ISecretRepository,
    flow_weaver_backend.Data.Repositories.SecretRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.ICredentialRepository,
    flow_weaver_backend.Data.Repositories.CredentialRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAgentScratchRepository,
    flow_weaver_backend.Data.Repositories.AgentScratchRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAiProviderRepository,
    flow_weaver_backend.Data.Repositories.AiProviderRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IGitRepositoryRepository,
    flow_weaver_backend.Data.Repositories.GitRepositoryRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IGitWebhookRepository,
    flow_weaver_backend.Data.Repositories.GitWebhookRepository>();
// AI conversation + agent-run persistence for the extracted agent runner
// (AgentConversationRunner), shared by the web chat and the messaging worker.
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAIConversationRepository,
    flow_weaver_backend.Data.Repositories.AIConversationRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAgentRunRepository,
    flow_weaver_backend.Data.Repositories.AgentRunRepository>();
// Messaging channels (Slack/Teams/WhatsApp/Telegram).
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IMessagingChannelRepository,
    flow_weaver_backend.Data.Repositories.MessagingChannelRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IMessagingIdentityLinkRepository,
    flow_weaver_backend.Data.Repositories.MessagingIdentityLinkRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IMessagingDeliveryRepository,
    flow_weaver_backend.Data.Repositories.MessagingDeliveryRepository>();
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IMessagingLinkTokenRepository,
    flow_weaver_backend.Data.Repositories.MessagingLinkTokenRepository>();
// Outbound email (SMTP relays) — read by the admin CRUD and by email_send.
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IEmailChannelRepository,
    flow_weaver_backend.Data.Repositories.EmailChannelRepository>();
// python_snippet import allow-list (admin-managed).
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IAllowedPythonModuleRepository,
    flow_weaver_backend.Data.Repositories.AllowedPythonModuleRepository>();
// User-authored colour themes (shared + private).
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IThemeRepository,
    flow_weaver_backend.Data.Repositories.ThemeRepository>();
// Unit of work for services that commit more than one aggregate per request
// (e.g. WorkflowPlan.Build creates snippets + a workflow in one transaction).
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IUnitOfWork,
    flow_weaver_backend.Data.Repositories.UnitOfWork>();

builder.Services.AddScoped<IDevice, DeviceService>();
builder.Services.AddScoped<ICredential, CredentialService>();
builder.Services.AddScoped<IInventorySource, InventorySourceService>();
builder.Services.AddScoped<IDevicePool, DevicePoolService>();
builder.Services.AddScoped<ISnippet, SnippetService>();
builder.Services.AddScoped<IWorkflow, WorkflowService>();
// YAML/JSON/python/ansible export + import parsing for /workflow/{id}/export
// and /workflow/import. Throws ValidationException / NotFoundException;
// the global handler maps both to problem+json.
builder.Services.AddScoped<
    flow_weaver_backend.Services.Workflow.IWorkflowExportService,
    flow_weaver_backend.Services.Workflow.WorkflowExportService>();
// Portable workflow bundle (export v3, read v2+v3, deterministic
// cross-instance import). Separate from the Services/Import translation
// pipeline on purpose: a bundle from another FlowWeaver — or from Nashira,
// which shares the format — resolves by identity or fails with a list, it is
// never fuzzy-matched like a foreign n8n/Itential definition.
builder.Services.AddScoped<
    flow_weaver_backend.Services.Workflow.IWorkflowBundleService,
    flow_weaver_backend.Services.Workflow.WorkflowBundleService>();
// The write side of a bundle import: snippets + sub-workflows + workflow +
// triggers, one transaction, or nothing.
builder.Services.AddScoped<
    flow_weaver_backend.Services.Workflow.IWorkflowBundleImporter,
    flow_weaver_backend.Services.Workflow.WorkflowBundleImporter>();
builder.Services.AddScoped<ISkill, SkillService>();
builder.Services.AddScoped<IIntegration, IntegrationService>();
builder.Services.AddScoped<IIntegrationAction, IntegrationActionService>();
// Bundle endpoint: integration + skills + specs + actions, one tx.
// Throws DomainException on validation/conflict; the controller is
// a thin adapter.
builder.Services.AddScoped<
    flow_weaver_backend.Services.Integration.IIntegrationBundleService,
    flow_weaver_backend.Services.Integration.IntegrationBundleService>();
// Post-creation management of an integration's scoped skills/specs. Attaching
// a spec here re-materializes the integration's actions (what the edit flow
// was missing vs. bundle-create).
builder.Services.AddScoped<
    flow_weaver_backend.Services.Integration.IIntegrationCatalogService,
    flow_weaver_backend.Services.Integration.IntegrationCatalogService>();
// MCP server management (CRUD + encrypt auth + test/sync tool cache).
builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpServerService,
    flow_weaver_backend.Services.Mcp.McpServerService>();
// FR-023: corporate guardrails. Service handles CRUD; the evaluator is
// what both WorkflowService and WorkflowExecutor consult to gate writes
// and runs. Both are scoped because they touch AppDbContext.
builder.Services.AddScoped<IPolicy, flow_weaver_backend.Services.Policy.PolicyService>();
builder.Services.AddScoped<flow_weaver_backend.Services.Policy.IPolicyEvaluator,
    flow_weaver_backend.Services.Policy.PolicyEvaluator>();
// S13.5: per-resource RBAC (workflow/integration grants).
builder.Services.AddScoped<flow_weaver_backend.Services.Permission.IResourcePermissionService,
    flow_weaver_backend.Services.Permission.ResourcePermissionService>();

// RBAC-granular refactor (plan_rbac_granular.md) phase 1: keeps the built-in
// operator/viewer permission grants in sync with each user's legacy role.
builder.Services.AddScoped<flow_weaver_backend.Services.Permission.IBuiltinGrantSync,
    flow_weaver_backend.Services.Permission.BuiltinGrantSync>();

// Phase 2: capability resolution. IPermissionGrantReader loads a user's grants;
// IEffectivePermissions turns them into HasAsync/CapabilitiesAsync decisions
// with admin-bypass. Not consulted for enforcement yet (phases 4+).
builder.Services.AddScoped<flow_weaver_backend.Data.Repositories.IPermissionGrantReader,
    flow_weaver_backend.Data.Repositories.PermissionGrantReader>();
builder.Services.AddScoped<flow_weaver_backend.Services.Permission.IEffectivePermissions,
    flow_weaver_backend.Services.Permission.EffectivePermissions>();

// Phase 3: management API for permission grants (CRUD + subject assignment).
builder.Services.AddScoped<flow_weaver_backend.Services.Interfaces.IPermissionGrant,
    flow_weaver_backend.Services.Permission.PermissionGrantService>();

// Application-wide feature flags. Singleton because the 60s IMemoryCache is
// shared across requests; the service uses IServiceScopeFactory internally to
// grab a scoped repository on miss.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<flow_weaver_backend.Services.Settings.IAppSettingsService,
    flow_weaver_backend.Services.Settings.AppSettingsService>();

// S13.6: rollback risk analyzer used by PromotionService and the editor.
builder.Services.AddScoped<flow_weaver_backend.Services.Promotion.WorkflowRollbackAnalyzer>();

// S15: cross-system workflow import pipeline (analyze → commit) with
// in-memory draft cache, format detectors, deterministic translators
// for n8n / Itential / v1, and the agent translator as a fallback.
builder.Services.AddSingleton<flow_weaver_backend.Services.Import.ImportDraftCache>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.WorkflowImportPipeline>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.DependencyResolver>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.ConflictDetector>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.SnippetStubBuilder>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Detectors.IDslDetector,
    flow_weaver_backend.Services.Import.Detectors.FlowWeaverV1Detector>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Detectors.IDslDetector,
    flow_weaver_backend.Services.Import.Detectors.N8nDetector>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Detectors.IDslDetector,
    flow_weaver_backend.Services.Import.Detectors.ItentialDetector>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Detectors.IDslDetector,
    flow_weaver_backend.Services.Import.Detectors.GenericDagDetector>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Translators.IDslTranslator,
    flow_weaver_backend.Services.Import.Translators.FlowWeaverV1Translator>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Translators.IDslTranslator,
    flow_weaver_backend.Services.Import.Translators.N8nTranslator>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Translators.IDslTranslator,
    flow_weaver_backend.Services.Import.Translators.ItentialTranslator>();
builder.Services.AddScoped<flow_weaver_backend.Services.Import.Translators.IDslTranslator,
    flow_weaver_backend.Services.Import.Translators.AgentTranslator>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.GenerateSnippetForImportHandler>();
// FU-3: chat-tool front for the import pipeline.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.AnalyzeForeignWorkflowHandler>();

// S14.2: SLO compute service shared by the dashboard endpoint and the
// daily breach watcher. Scoped because it touches AppDbContext.
builder.Services.AddScoped<flow_weaver_backend.Services.Slo.SloComputeService>();
// Concrete service also registered so IntegrationBundleService can reuse
// its BuildEntity helper without duplicating the shape.
builder.Services.AddScoped<IntegrationService>();
builder.Services.AddScoped<IAIProvider, AIProviderService>();
builder.Services.AddScoped<IAIAgent, AIAgentService>();
builder.Services.AddScoped<IWorkflowTrigger, WorkflowTriggerService>();

// Sprint 7: prompt skills + API specs CRUD. These wrap
// the catalog tables used by the agent; mutations invalidate the
// respective singleton caches (ISkillPromptLoader / IApiSpecIndex).
builder.Services.AddScoped<IAiPromptSkill,
    flow_weaver_backend.Services.AiPromptSkill.AiPromptSkillService>();
builder.Services.AddScoped<IAiApiSpec,
    flow_weaver_backend.Services.AiApiSpec.AiApiSpecService>();
builder.Services.AddScoped<flow_weaver_backend.Services.Promotion.IPromotionService,
    flow_weaver_backend.Services.Promotion.PromotionService>();
// Git integration: registry of remote repositories +
// LibGit2Sharp wrapper. The service is stateful on disk (clones live
// under Git:Root) but holds no in-memory state beyond a per-repo lock,
// so a scoped lifetime is correct.
builder.Services.AddScoped<flow_weaver_backend.Services.Git.IGitService,
    flow_weaver_backend.Services.Git.GitService>();
builder.Services.AddScoped<flow_weaver_backend.Services.Git.IGitWebhookService,
    flow_weaver_backend.Services.Git.GitWebhookService>();
// Webhook receiver runs out-of-band (anonymous endpoint, builds its
// own scope). Singleton so the IServiceScopeFactory it captures is
// the root one — child scopes get a fresh DbContext per call.
builder.Services.AddSingleton<flow_weaver_backend.Services.Git.GitWebhookReceiver>();
// Same rationale for the workflow-trigger webhook receiver (FR-027 / TC-FW-061).
builder.Services.AddSingleton<flow_weaver_backend.Services.WorkflowTrigger.WorkflowWebhookReceiver>();
builder.Services.AddSingleton<flow_weaver_backend.Services.Compiler.IWorkflowYamlCompiler,
    flow_weaver_backend.Services.Compiler.WorkflowYamlCompiler>();
// Pluggable workflow exporters (Sprint 6 / Phase 6 follow-up). Each
// implementation declares its own `format` key; the controller picks
// one by query string. Add new ones here and they become available
// without touching the controller switch.
builder.Services.AddSingleton<flow_weaver_backend.Services.Compiler.IWorkflowExporter,
    flow_weaver_backend.Services.Compiler.PythonWorkflowExporter>();
builder.Services.AddSingleton<flow_weaver_backend.Services.Compiler.IWorkflowExporter,
    flow_weaver_backend.Services.Compiler.AnsibleWorkflowExporter>();
builder.Services.AddScoped<IWorkflowPlan, WorkflowPlanService>();

// Sprint 4: AI Constructor — LLM providers, tool system, chat.
builder.Services.AddHttpClient("llm", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Providers.LlmProviderFactory>();

// Role / tier gate for tool dispatch. Singleton — Matrix is immutable
// static data; only the logger is per-process. The injected checker is
// consumed by ToolDispatcher.
builder.Services.AddSingleton<flow_weaver_backend.Services.Ai.Permissions.PermissionClassifier>();

// Heuristic prompt-injection observability. Singleton — compiled Regex
// + logger share one instance across the process.
builder.Services.AddSingleton<flow_weaver_backend.Services.Ai.Security.PromptSafetyChecker>();

// Stream deadline + warn-threshold knobs. Bound from the "AiChat"
// section; env var override is `AiChat__StreamDeadlineSeconds`.
builder.Services.Configure<flow_weaver_backend.Services.Ai.AiChatOptions>(
    builder.Configuration.GetSection(flow_weaver_backend.Services.Ai.AiChatOptions.SectionName));

// Tool registry (singleton) + dispatcher (scoped per-request).
builder.Services.AddSingleton<flow_weaver_backend.Services.Ai.Tools.ToolRegistry>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.ToolDispatcher>();

// Agent turn orchestrator (scoped). Holds the tool-calling loop extracted from
// AiChatController so the web chat (SSE sink), the non-streaming endpoint, and
// the messaging worker (F2) all run the same path.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Conversation.IAgentConversationRunner,
    flow_weaver_backend.Services.Ai.Conversation.AgentConversationRunner>();

// Messaging channels (Slack/Teams/WhatsApp/Telegram). Provider resolver is a
// singleton over every registered IMessagingProvider (concrete providers are
// added per phase, starting with Telegram in F2). Outbound calls use a named
// HttpClient.
builder.Services.Configure<flow_weaver_backend.Services.Messaging.MessagingOptions>(
    builder.Configuration.GetSection(flow_weaver_backend.Services.Messaging.MessagingOptions.SectionName));
builder.Services.AddSingleton<flow_weaver_backend.Services.Messaging.IMessagingProviderResolver,
    flow_weaver_backend.Services.Messaging.MessagingProviderResolver>();
builder.Services.AddHttpClient("messaging", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<flow_weaver_backend.Services.Messaging.IMessagingChannelService,
    flow_weaver_backend.Services.Messaging.MessagingChannelService>();
builder.Services.AddScoped<flow_weaver_backend.Services.Messaging.IMessagingLinkService,
    flow_weaver_backend.Services.Messaging.MessagingLinkService>();
// Admin CRUD for the python_snippet import allow-list.
builder.Services.AddScoped<flow_weaver_backend.Services.PythonModules.IAllowedPythonModuleService,
    flow_weaver_backend.Services.PythonModules.AllowedPythonModuleService>();
// Colour themes. Any user may keep private ones; publishing is admin-gated
// inside the service, not by the controller.
builder.Services.AddScoped<flow_weaver_backend.Services.Themes.IThemeService,
    flow_weaver_backend.Services.Themes.ThemeService>();
// Outbound email. The sender is shared by the channel test-send (API process)
// and the email_send handler (worker process), so it registers unconditionally;
// the admin CRUD service only matters where controllers run.
builder.Services.AddScoped<flow_weaver_backend.Services.Email.IEmailSender,
    flow_weaver_backend.Services.Email.EmailSender>();
builder.Services.AddScoped<flow_weaver_backend.Services.Email.IEmailChannelService,
    flow_weaver_backend.Services.Email.EmailChannelService>();
// Concrete providers (one IMessagingProvider per platform; resolver indexes them).
builder.Services.AddSingleton<flow_weaver_backend.Services.Messaging.IMessagingProvider,
    flow_weaver_backend.Services.Messaging.Providers.TelegramProvider>();
builder.Services.AddSingleton<flow_weaver_backend.Services.Messaging.IMessagingProvider,
    flow_weaver_backend.Services.Messaging.Providers.SlackProvider>();
builder.Services.AddSingleton<flow_weaver_backend.Services.Messaging.IMessagingProvider,
    flow_weaver_backend.Services.Messaging.Providers.WhatsAppProvider>();
builder.Services.AddSingleton<flow_weaver_backend.Services.Messaging.IMessagingProvider,
    flow_weaver_backend.Services.Messaging.Providers.TeamsProvider>();
// Inbound ingest (creates its own scope per delivery) + the worker
// that runs queued agent turns and outbound sends.
builder.Services.AddScoped<flow_weaver_backend.Services.Messaging.IMessagingIngestService,
    flow_weaver_backend.Services.Messaging.MessagingIngestService>();
builder.Services.AddScoped<flow_weaver_backend.Services.Messaging.IMessagingJobProcessor,
    flow_weaver_backend.Services.Messaging.MessagingJobProcessor>();
// The messaging worker runs the AGENT for inbound turns, which needs the
// populated ToolRegistry + the full AI/tool stack. That registry is only
// populated in the API (non-worker) process (see the `if (!workerOnly)` block
// that calls reg.Register(handler) at startup). So restrict the messaging
// worker to the API process — the shared `jobs` queue means a Slack event the
// worker-only container's socket receives still gets processed here. Without
// this gate, a turn picked up by the worker-only container sees an EMPTY tool
// registry → the agent answers "no tools enabled".
if (!workerOnly)
    builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.MessagingWorkerHostedService>();
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.MessagingRetentionHostedService>();
// Slack Socket Mode: outbound WebSocket connector (no public ingress). Active
// only for Slack channels that have an App-Level Token configured.
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.SlackSocketModeHostedService>();
// Teams over Azure Relay: the same "no public ingress" shape for a platform
// that has no Socket Mode of its own — an outbound control channel the Relay
// forwards the Bot Framework's POSTs over. Active only for Teams channels with
// a Relay connection string configured.
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.TeamsRelayHostedService>();

// Frontend page links handed to the agent inside tool results. Singleton —
// it only reads Workflow:PublicBaseUrl once.
builder.Services.AddSingleton<flow_weaver_backend.Services.Ai.Tools.IAppLinks,
                              flow_weaver_backend.Services.Ai.Tools.AppLinks>();

// Tool handlers (scoped — they inject DbContext).
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListWorkflowsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListSnippetsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.CreateSnippetHandler>();
// Configuration tools (users / policies / vendor commands). Per-request
// role gating is enforced by the ToolDispatcher, so it's safe to register them
// for every agent — a non-admin caller is refused before the handler runs.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.CreateUserHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListUsersHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.SetUserRoleHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.GrantResourcePermissionHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.CreatePolicyHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListPoliciesHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.CreateVendorCommandHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.UpdateVendorCommandHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.DeleteVendorCommandHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListCredentialsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.QueryDevicesHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.CreateWorkflowPlanHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.EvaluatePromptSufficiencyHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ValidateSshCommandsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListVendorCommandsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.FindCommandHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.SimulateWorkflowRunHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.MarkWorkflowReadyHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListPlanFeaturesHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.RunAcceptanceTestsHandler>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Ai.Scratch.IAgentScratchService,
    flow_weaver_backend.Services.Ai.Scratch.AgentScratchService>();

// Sprint 8 dynamic API tools (netora-style).
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListApisHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.DiscoverOperationsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.OperationDetailHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.LoadSkillHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Skills.ScopedSkillCatalog>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ExecuteOperationHandler>();

// MCP tools: discover + call tools on registered external MCP servers.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ListMcpServersHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.DiscoverMcpToolsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.CallMcpToolHandler>();

// Sprint 8 domain debug/edit tools used by "Fix with AI" / "Edit with AI".
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.GetRunDetailsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.GetStepLogsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.GetWorkflowDetailsHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.UpdateWorkflowNodeConfigHandler>();

// Git tool handlers — let the agent inspect, edit, and push files in
// the registered repositories. Scoped because they wrap IGitService
// which is itself scoped.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitListRepositoriesHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitListFilesHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitReadFileHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitWriteFileHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitCommitPushHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitPullHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitDiffHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitListWebhooksHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitCreateWebhookHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.Git.GitCreateRemoteRepositoryHandler>();

// Sprint 11: report generation. The tool handler needs
// IToolExecutionContext (scoped — hydrated by AiChatController per turn)
// and IReportService (scoped — wraps the four exporters).
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.IToolExecutionContext,
    flow_weaver_backend.Services.Ai.Tools.ToolExecutionContext>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.GenerateReportHandler>();
builder.Services.Configure<ReportOptions>(builder.Configuration.GetSection(ReportOptions.SectionName));
builder.Services.AddSingleton<ISecretRedactor, SecretRedactor>();
// Exporters are stateless — singleton. ReportService is scoped because
// it writes through AppDbContext + ICurrentUser.
builder.Services.AddSingleton<IReportExporter, HtmlReportExporter>();
builder.Services.AddSingleton<IReportExporter, CsvReportExporter>();
builder.Services.AddSingleton<IReportExporter, ExcelReportExporter>();
builder.Services.AddSingleton<IReportExporter, PdfReportExporter>();
builder.Services.AddSingleton<IReportExporter, WordReportExporter>();
builder.Services.AddSingleton<IReportExporter, JsonReportExporter>();

// File parsing (Services/Files). Stateless and thread-safe — the parsers hold no
// state between calls — so a singleton, like the exporters above.
builder.Services.AddSingleton<flow_weaver_backend.Services.Files.IFileParsingService,
    flow_weaver_backend.Services.Files.FileParsingService>();
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Tools.Handlers.ParseFileHandler>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.ReportRetentionHostedService>();

// QuestPDF community license — required globally; calling it per-export
// would duplicate work and surprise tests.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Constructor.IPromptAnalyzer,
    flow_weaver_backend.Services.Ai.Constructor.PromptAnalyzer>();

// Sprint 8: ${secret:source:id:field} resolver used by the REST executor
// and any tool that needs stored credentials without the admin
// pasting them into the agent prompt.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Secrets.ISecretResolver,
    flow_weaver_backend.Services.Ai.Secrets.SecretResolver>();

// Expands ${report:<id>} attachment references into base64 so the agent can attach a generated report to an email/integration by id
// instead of copying the blob. Used by the REST executor alongside secrets.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Reports.IReportReferenceResolver,
    flow_weaver_backend.Services.Ai.Reports.ReportReferenceResolver>();

// Runs one OpenAPI operation against the live API with secret substitution
// and SSRF guard. Backs the agent's `execute_operation` dynamic tool.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.RestExecutor.IRestOperationExecutor,
    flow_weaver_backend.Services.Ai.RestExecutor.RestOperationExecutor>();

// Sprint 2.1: runtime entities (read-only from the HTTP surface). The
// engine/workers added in 2.2–2.4 are the only writers.
builder.Services.AddScoped<IWorkflowRun, WorkflowRunService>();
builder.Services.AddScoped<IStepRun, StepRunService>();
builder.Services.AddScoped<IWorkflowVersion, WorkflowVersionService>();
builder.Services.AddScoped<IJob, JobService>();

// Sprint 2.2: Dapper-backed queue repository. Scoped so it shares the
// per-request AppDbContext connection (keeps transactions, connection
// reuse, and the configured data source in one place).
builder.Services.AddScoped<IQueueRepository, QueueRepository>();
// Install the JsonElement ↔ string mapping so Dapper can round-trip jsonb
// columns on every query in the project — matches the EF Core converter.
Dapper.SqlMapper.AddTypeHandler(new JsonElementTypeHandler());

// Sprint 2.3: DAG parsing + variable templating. Both are stateless after
// construction, so Singleton keeps allocation out of the per-request path.
builder.Services.AddSingleton<DagParser>();
builder.Services.AddSingleton<IVariableResolver, VariableResolver>();

// Sprint 2.7: Condition evaluator for conditional edges in the DAG.
builder.Services.AddSingleton<IConditionEvaluator, ConditionEvaluator>();

// Sprint 2.4: Workflow executor — the orchestration brain. Singleton
// because it holds the concurrency SemaphoreSlim and creates scopes
// internally (one per run). RetryPolicyExecutor is stateless; the worker
// uses it to run a step's retry_policy attempts.
builder.Services.Configure<WorkflowExecutorOptions>(
    builder.Configuration.GetSection(WorkflowExecutorOptions.SectionName));
builder.Services.AddSingleton<RetryPolicyExecutor>();
builder.Services.AddSingleton<IWorkflowExecutor, WorkflowExecutor>();

// Sprint 2.5: Worker — BackgroundService that polls the job queue and
// dispatches to handlers. Options bind to the "Worker" section.
// Individual ISnippetHandler implementations are registered by Sprint
// 2.6; the worker resolves them from the DI scope at dispatch time.
builder.Services.Configure<flow_weaver_backend.Services.Worker.WorkerOptions>(
    builder.Configuration.GetSection(flow_weaver_backend.Services.Worker.WorkerOptions.SectionName));
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.WorkerHostedService>();
// Installs admin-approved pip packages for python_snippet into the shared
// package dir the sandbox binds. Gated to the worker so there is a SINGLE
// writer to the shared package volume — which is also the only container
// that runs sandboxed python steps (they're routed to the `sandbox` queue
// tag the API container doesn't claim; see WorkerOptions.Tags). The atomic
// per-row claim still guards against multiple worker replicas.
if (workerOnly)
    builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.PythonPackageProvisionerHostedService>();

// Sweeper that returns expired job claims to the pending pool. Runs in
// both API and worker-only modes so a crashed worker is always eventually
// recovered even if only one deployment tier is alive.
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.JobReclaimHostedService>();

// Sprint 9 observability: application-level trace trail. Scoped service so
// each request sees its own ICurrentUser + IHttpContextAccessor. The
// retention sweeper runs independently and purges rows older than
// Tracing:RetentionDays (default 365; env Tracing__RetentionDays).
builder.Services.AddScoped<flow_weaver_backend.Services.Observability.ITraceLogger,
    flow_weaver_backend.Services.Observability.TraceLogger>();
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.TraceRetentionHostedService>();

// The other two security logs. auth_events is capped at Auth:RetentionDays
// (default 180) because token refreshes make it session-volume; audit_logs is
// kept forever unless Audit:RetentionDays is set, because it is the compliance
// record. See SecurityLogRetentionHostedService.
builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.SecurityLogRetentionHostedService>();

// Periodic reminder: emits a daily AuditEvent snapshotting which
// integrations have AllowPrivateNetwork=true. Compliance uses /admin/audit
// (filter category=allow-private-network) to recheck the active opt-outs.
//
// Registered ONLY in API mode. The compose deploy runs both backend and
// worker-only instances; both would otherwise emit duplicate audit rows
// every 24h. The reminder's only purpose is to surface state to admins via
// the API surface, so the backend instance is the natural owner.
if (!workerOnly)
{
    builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.AllowPrivateNetworkReminderService>();

    // S14.2: daily SLO breach sweep. Same gating rationale as the
    // AllowPrivateNetwork reminder — it writes audit rows, so only the
    // backend container should own it to avoid duplicates.
    builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.SloBreachWatcherService>();

    // Schedules: fires cron/interval WorkflowTriggers when NextRunAt comes due
    // and enqueues a run via IWorkflowExecutor. Single-owner (API container)
    // like the sweepers above so the backend+worker deploy fires each schedule
    // exactly once. Supports 6-field (seconds) cron for sub-minute cadence.
    builder.Services.AddHostedService<flow_weaver_backend.BackgroundServices.SchedulerHostedService>();
}

// Sprint 10: reseed-from-disk for skills + specs so deployed updates to
// bundled /Skills/*.md and /Specs/*.yaml can overwrite existing rows on
// demand from the admin UI.
builder.Services.AddScoped<flow_weaver_backend.Services.Ai.Seed.CatalogReseedService>();

// Sprint 2.6: Snippet handlers. Each ISnippetHandler maps to a
// Snippet.Type value. Scoped so they can inject a fresh DbContext.
//
// Guarded client: IUrlGuard runs on the initial URL AND on every redirect
// hop (AllowAutoRedirect is forced off). Without that, an allowed external
// host could 302 the request straight at 169.254.169.254 and the guard would
// never see it. See SsrfGuardingRedirectHandler.
builder.Services.AddGuardedHttpClient("rest_call", c => c.Timeout = TimeSpan.FromSeconds(60));

// Load-test device simulation (NFR-004 / TC-FW-065). When Simulation:Enabled,
// the device-touching leaf handlers (ping/ssh/ansible) are swapped for fakes
// that do NO real network I/O — so a staging box can scale to thousands of
// synthetic devices to measure orchestrator/queue/DB capacity. Boot is refused
// on a production-tier box (SimulationGuard). OFF by default.
builder.Services.Configure<flow_weaver_backend.Services.Worker.Simulation.SimulationOptions>(
    builder.Configuration.GetSection(flow_weaver_backend.Services.Worker.Simulation.SimulationOptions.SectionName));
var simulationEnabled = builder.Configuration.GetValue(
    $"{flow_weaver_backend.Services.Worker.Simulation.SimulationOptions.SectionName}:Enabled", false);
flow_weaver_backend.Services.Worker.Simulation.SimulationGuard.ThrowIfUnsafe(
    simulationEnabled, builder.Configuration["Workflow:WorkerEnvironment"]);
if (simulationEnabled)
{
    builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
        flow_weaver_backend.Services.Worker.Simulation.SimPingHandler>();
    builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
        flow_weaver_backend.Services.Worker.Simulation.SimSshHandler>();
    builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
        flow_weaver_backend.Services.Worker.Simulation.SimAnsibleHandler>();
}
else
{
    builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
        flow_weaver_backend.Services.Worker.Handlers.PingHandler>();
    builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
        flow_weaver_backend.Services.Worker.Handlers.SshHandler>();
    builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
        flow_weaver_backend.Services.Worker.Handlers.AnsibleHandler>();
}
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.RestCallHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.TransformHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.IntegrationActionHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.McpCallHandler>();
// (ssh handler registered above — real or simulated depending on Simulation:Enabled)
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.NetconfHandler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.SnmpV3Handler>();
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.PythonHandler>();
// (ansible handler registered above — real or simulated depending on Simulation:Enabled)
// Sprint 11: report snippet — lets workflow steps produce the same
// artifacts the agent's generate_report tool produces, composed with
// integration_action to email/upload them.
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.ReportHandler>();
// Native Slack snippet (type "slack_message"). The bot token is a deployment
// secret (Slack:BotToken / env SLACK_BOT_TOKEN), unlike the integration_action
// route which keeps it in an encrypted Integration.
builder.Services.AddHttpClient(
    flow_weaver_backend.Services.Worker.Handlers.SlackHandler.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.SlackHandler>();
// Native SMTP snippet (type "email_send"). Credentials come from an encrypted
// EmailChannel row, not from deployment config, so one deployment can hold
// several relays and a step picks one (or falls back to the default channel).
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.EmailSendHandler>();
// Git ops handler — workflow steps can read/write/commit/push files in
// registered repositories. Delegates to IGitService so the same code
// path serves the agent tools, the REST controller, and the runner.
builder.Services.AddScoped<flow_weaver_backend.Services.Worker.ISnippetHandler,
    flow_weaver_backend.Services.Worker.Handlers.GitOpsHandler>();

// Sprint 5.1: SSRF guard — blocks requests to private/loopback addresses.
builder.Services.AddSingleton<IUrlGuard, UrlGuard>();

// Skills (/Skills/*.md) and API specs (/Specs/*.yaml) are file-backed caches
// shared across requests — singletons so the index survives between calls.
builder.Services.AddSingleton<ISkillPromptLoader, SkillPromptLoader>();
builder.Services.AddSingleton<IApiSpecIndex, YamlSpecIndex>();

// Workflow schema validator loads the JSON Schema once from an embedded
// resource; Singleton because it's stateless after construction.
builder.Services.AddSingleton<flow_weaver_backend.Services.Validation.IWorkflowSchemaValidator,
    flow_weaver_backend.Services.Validation.WorkflowSchemaValidator>();

// Second-pass validator: confirms integration_action nodes reference
// real Integration / IntegrationAction rows. Scoped
// because it injects AppDbContext.
builder.Services.AddScoped<flow_weaver_backend.Services.Validation.IWorkflowReferenceValidator,
    flow_weaver_backend.Services.Validation.WorkflowReferenceValidator>();

// Third-pass validator: catalog of valid CLI commands per
// device_type. Singleton registry caches the catalog; scoped validator
// reads it + AppDbContext. Mutations on /api/vendor-command call
// IVendorCommandRegistry.Invalidate so admins see edits immediately.
builder.Services.AddSingleton<flow_weaver_backend.Services.Validation.IVendorCommandRegistry,
    flow_weaver_backend.Services.Validation.VendorCommandRegistry>();
builder.Services.AddScoped<flow_weaver_backend.Services.Validation.IVendorCommandValidator,
    flow_weaver_backend.Services.Validation.VendorCommandValidator>();
builder.Services.AddScoped<flow_weaver_backend.Services.Interfaces.IVendorCommand,
    flow_weaver_backend.Services.VendorCommand.VendorCommandService>();

// ─── Integration health check ───────────────────────────────────────
// Two named HttpClients so the handler lifetime is managed by the factory
// (warm connection pool, periodic DNS refresh) and TLS-skip is a compile-time
// choice of client name rather than a per-request handler rebuild.
// Both are guarded: redirects are followed by SsrfGuardingRedirectHandler with
// IUrlGuard re-run per hop and Authorization stripped cross-origin, so a
// redirect can neither reach an internal address nor leak the integration's
// bearer to a third-party host.
builder.Services.AddGuardedHttpClient(
    flow_weaver_backend.Services.Integration.IntegrationHealthChecker.ClientName,
    client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddGuardedHttpClient(
    flow_weaver_backend.Services.Integration.IntegrationHealthChecker.InsecureClientName,
    client => client.Timeout = TimeSpan.FromSeconds(10),
    primaryHandler: () => new HttpClientHandler
    {
        // Used only for integrations with TLSSkipVerify=true (lab / dev).
        // Never enable globally.
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

builder.Services.AddSingleton<flow_weaver_backend.Services.Integration.IntegrationAuthBuilder>();
// OAuth2 client-credentials for integrations: token grant + in-memory cache,
// and the async applier that layers the Bearer token over the sync builder.
builder.Services.AddSingleton<flow_weaver_backend.Services.Integration.IIntegrationOAuthTokenService,
    flow_weaver_backend.Services.Integration.IntegrationOAuthTokenService>();
builder.Services.AddSingleton<flow_weaver_backend.Services.Integration.IIntegrationAuthApplier,
    flow_weaver_backend.Services.Integration.IntegrationAuthApplier>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Integration.IIntegrationHealthChecker,
    flow_weaver_backend.Services.Integration.IntegrationHealthChecker>();

// ─── MCP (Model Context Protocol) client ─────────────────────────────
// Named HTTP clients for connecting to external MCP servers. Timeout is
// infinite so Streamable-HTTP/SSE streams aren't cut short — the McpClient's
// per-operation linked CTS is the real budget. Insecure variant is TLS-skip.
builder.Services.AddGuardedHttpClient(
    flow_weaver_backend.Services.Mcp.McpHttpClients.Secure,
    client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);

builder.Services.AddGuardedHttpClient(
    flow_weaver_backend.Services.Mcp.McpHttpClients.Insecure,
    client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    primaryHandler: () => new HttpClientHandler
    {
        // Used only for MCP servers with TLSSkipVerify=true (lab / dev).
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpConnectionFactory,
    flow_weaver_backend.Services.Mcp.McpConnectionFactory>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpClient,
    flow_weaver_backend.Services.Mcp.McpClient>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpToolExecutor,
    flow_weaver_backend.Services.Mcp.McpToolExecutor>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpOAuthService,
    flow_weaver_backend.Services.Mcp.McpOAuthService>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpTokenService,
    flow_weaver_backend.Services.Mcp.McpTokenService>();
builder.Services.AddScoped<
    flow_weaver_backend.Services.Mcp.IMcpOAuthFlowService,
    flow_weaver_backend.Services.Mcp.McpOAuthFlowService>();

// Security baseline — only needed when serving HTTP.
if (!workerOnly)
{
    builder.Services.AddFlowWeaverRateLimiter();
    builder.Services.AddFlowWeaverCors(builder.Configuration);
}

// Caller context is required by scoped services used in both API and
// worker-only mode. Outside HTTP requests, MutableCurrentUser lets
// background flows (scheduler, webhook receiver) explicitly Bind() an
// identity before resolving downstream services in the same scope.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<MutableCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<MutableCurrentUser>());

// ─── Auth: options, JWT bearer, policies ────────────────────────────
// Auth services are needed for the HTTP surface AND for the DevSeedService
// (password hashing). Register core auth always, but JWT bearer + policies
// only when serving HTTP.

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IPasswordPolicy, PasswordPolicy>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<IAuthAuditLogger, AuthAuditLogger>();
builder.Services.AddScoped<IAuthService, AuthService>();

// Sprint 5.7: Domain audit logger — captures entity mutations (create,
// update, delete) for compliance. Scoped so it shares the request's
// DbContext, ICurrentUser, and IHttpContextAccessor.
builder.Services.AddScoped<flow_weaver_backend.Services.Audit.IAuditLogger,
    flow_weaver_backend.Services.Audit.AuditLogger>();

// Validate Jwt:Key in BOTH API and worker boots. Workers do not issue
// tokens but they share the same secret store and a misconfigured deploy
// (placeholder key shipped to prod) is the same security incident
// regardless of which role boots first. The bound options are reused by
// the bearer registration below to avoid re-binding the section twice.
// JwtOptions.ValidateForBoot is the single source of truth for this rule
// — covered by JwtBootValidationTests. After Validate returns, the
// options are guaranteed non-null with a 32+ char key.
var jwtBound = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();
JwtOptions.ValidateForBoot(jwtBound, builder.Environment.EnvironmentName);
var jwtOptions = jwtBound!;

if (!workerOnly)
{
    var jwt = jwtOptions;
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Admin", p => p.RequireRole("admin"));
        options.AddPolicy("Operator", p => p.RequireRole("admin", "operator"));
        options.AddPolicy("Viewer", p => p.RequireAuthenticatedUser());
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    // RBAC-granular refactor (plan_rbac_granular.md §6.1): resolve
    // [HasPermission("cap")] gates. The provider materialises "perm:*"
    // policies on demand; the scoped handler decides them (admin-bypass →
    // granular grants → legacy-tier fallback per the configured RbacMode).
    builder.Services.AddSingleton<IAuthorizationPolicyProvider,
        flow_weaver_backend.Services.Security.Authorization.PermissionPolicyProvider>();
    builder.Services.AddScoped<IAuthorizationHandler,
        flow_weaver_backend.Services.Security.Authorization.PermissionAuthorizationHandler>();
}

var app = builder.Build();

if (simulationEnabled)
    app.Logger.LogWarning(
        "SIMULATION MODE ON (Simulation:Enabled=true) — ping/ssh/ansible steps are FAKED, no real "
        + "device I/O. For load testing only. worker_environment={WorkerEnvironment}",
        builder.Configuration["Workflow:WorkerEnvironment"] ?? "dev-sandbox");

// Populate the tool registry with handler metadata only when serving HTTP.
// In worker-only mode the handlers are unused, and resolving them at
// startup would require request-scoped caller context that does not exist.
if (!workerOnly)
{
    using var initScope = app.Services.CreateScope();
    var reg = app.Services.GetRequiredService<flow_weaver_backend.Services.Ai.Tools.ToolRegistry>();
    // Single source of truth (also iterated by ToolClassificationCoverageTests
    // so a new tool can't ship unclassified — FR-037 / TC-FW-063).
    foreach (var ht in flow_weaver_backend.Services.Ai.Tools.AgentToolHandlers.All)
    {
        var handler = (flow_weaver_backend.Services.Ai.Tools.IToolHandler)initScope.ServiceProvider.GetRequiredService(ht);
        reg.Register(handler);
    }
}

// Apply pending migrations on startup. Retries for ~60s to survive the race
// where the backend container starts before Postgres finishes booting, even
// when compose declares depends_on: service_healthy.
await ApplyMigrationsWithRetryAsync(app, TimeSpan.FromSeconds(60));

// Dev-only seed: creates the admin/admin user so a fresh
// database is immediately usable from the frontend login page. Production
// deployments go through /api/auth/bootstrap + /api/users instead.
if (app.Environment.IsDevelopment())
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var seedLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Auth.DevSeedService.SeedAsync(scopeFactory, seedLogger);
}

// Cross-instance identity backfill. Integrations and snippets created before
// the Slug column existed get one here so a workflow bundle exported from this
// instance can name them portably. Idempotent; only null slugs are touched.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var slugLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Common.SlugBackfillService.BackfillAsync(scopeFactory, slugLogger);
}

// Always-on seed: ensure the default qa→production promotion gate
// exists. Replaces the previous hardcoded 48h check; admins can
// edit or disable the seeded policy via /policies. Idempotent.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var policyLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Policy.PolicyDefaultsSeeder.SeedAsync(scopeFactory, policyLogger);
}

// RBAC-granular refactor (plan_rbac_granular.md) phase 1: ensure the built-in
// operator/viewer permission grants exist and backfill existing
// users into them by their legacy role. Idempotent; no behaviour change (the
// bundles reproduce the 3-tier model until later phases enforce grants).
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var grantsLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Permission.PermissionGrantSeeder.SeedAsync(scopeFactory, grantsLogger);
}

// Sprint 7 (revised): boot-time UPSERT of /Skills and /Specs into the
// catalog rows. Shipped entries that are still the shipped version get
// refreshed without admins having to click "Reseed from disk"; rows an
// admin edited or deactivated are kept as they are (see
// ShippedCatalog). Custom rows (filenames not present on disk) are left
// alone. The runtime reseed buttons stay for taking the shipped version
// explicitly and for hot-edits inside the running container.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var catalogLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Ai.Seed.CatalogBootReseedService.ReseedAllAsync(
        scopeFactory, app.Environment, catalogLogger);
}

// Sprint 8: when an OpenAI provider is enabled, ensure a default
// assistant agent is wired to it. Keeps /ai/chat working without
// requiring admins to manually author an agent before first use.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var agentSeedLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Ai.Seed.DefaultAgentSeedService.SeedAsync(
        scopeFactory, agentSeedLogger);
}

// Sprint 10: seed baseline Snippets (ping / rest_call / transform).
// Idempotent by type — the agent has these as building blocks from day
// one instead of minting them before every new workflow.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var snippetsSeedLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Ai.Seed.DefaultSnippetsSeedService.SeedAsync(
        scopeFactory, snippetsSeedLogger);

    // Must run AFTER the seed above: it rewrites the editor marker that leaked
    // into saved workflows to the seeded integration_action snippet's real id,
    // so there is nothing to rewrite to until that snippet exists.
    await flow_weaver_backend.Services.Common.LegacyNodeMarkerBackfill.RunAsync(
        scopeFactory, snippetsSeedLogger);
}

// Seed the ssh command catalog. Idempotent by
// (device_type, kind, value): existing rows (active or
// admin-deleted) are left alone; missing rows are inserted. User-
// authored rows survive every re-seed.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var vendorCommandsLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Ai.Seed.DefaultVendorCommandsSeedService.SeedAsync(
        scopeFactory, vendorCommandsLogger);

    // S14.3: gap-fill from Skills/vendors/*.yaml. Runs after the in-code
    // catalogue so YAML scaffolds (Arista, Fortinet, Palo Alto, F5, …)
    // only insert what the curated catalogue did not cover.
    await flow_weaver_backend.Services.Ai.Seed.VendorCommandYamlSeedService.SeedAsync(
        scopeFactory, app.Environment, vendorCommandsLogger);
}

// FR-023 sample policies. Idempotent by name; ships disabled.
{
    var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
    var policiesSeedLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await flow_weaver_backend.Services.Ai.Seed.DefaultPoliciesSeedService.SeedAsync(
        scopeFactory, policiesSeedLogger);
}

// Warm the spec index so the first
// /api/ai/catalog/operations request does not pay the parse cost.
{
    using var warmScope = app.Services.CreateScope();
    var specIndex = app.Services.GetRequiredService<IApiSpecIndex>();
    await specIndex.ReloadAsync();
}

// System boot trace — one row per process start. Lets /admin/traces filter
// by category=system to see restarts and seed activity at a glance. Opens
// a scope manually because ITraceLogger is scoped (it injects ICurrentUser
// which is itself scoped and request-bound, but at boot there is no
// request).
try
{
    using var bootScope = app.Services.CreateScope();
    var bootTrace = bootScope.ServiceProvider
        .GetRequiredService<flow_weaver_backend.Services.Observability.ITraceLogger>();
    await bootTrace.EventAsync("system.boot", "system", "completed",
        metadata: new
        {
            environment = app.Environment.EnvironmentName,
            dotnet = Environment.Version.ToString(),
            machine = Environment.MachineName,
            process_id = Environment.ProcessId,
        });
}
catch
{
    // boot-trace failure must never block serving traffic
}

static async Task ApplyMigrationsWithRetryAsync(WebApplication app, TimeSpan totalTimeout)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

    var deadline = DateTime.UtcNow + totalTimeout;
    var attempt = 0;

    while (true)
    {
        attempt++;
        try
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied (attempt {Attempt})", attempt);
            return;
        }
        catch (Exception ex) when (DateTime.UtcNow < deadline)
        {
            logger.LogWarning(ex, "Migration attempt {Attempt} failed, retrying in 3s", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

// ─── HTTP pipeline (skipped in worker-only mode) ��───────────────────
if (!workerOnly)
{
    // FIRST in the pipeline: everything downstream that reads the caller's
    // address — the login rate limiter, CorrelationMiddleware's remote_ip,
    // AuditEvent.Ip, AuthEvent.Ip — must see the rewritten value, not the
    // proxy's. Only rewrites for trusted proxies; see ForwardedHeadersConfiguration.
    app.UseForwardedHeaders();
    app.Logger.LogInformation(
        "forwarded-headers trusted_proxies={TrustedProxies}",
        ForwardedHeadersConfiguration.Describe(builder.Configuration));

    app.UseSecurityHeaders();
    app.UseCors(CorsConfiguration.PolicyName);

    // Global error pipeline — DomainException → problem+json. Must run
    // before MVC binds so unhandled domain failures from controllers and
    // services are translated uniformly. UseStatusCodePages turns native
    // 404/405 (route miss, wrong verb) into the same shape, so clients
    // never see HTML error pages.
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options
            .WithTitle("Flow Weaver API")
            .WithTheme(ScalarTheme.Default)
            .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl);
    });

    app.UseAuthentication();
    app.UseAuthorization();

    // Correlation must come after auth so the middleware can read claim
    // values for user_id / username / role from HttpContext.User. It
    // must come before controller mapping so every handler log line
    // carries the enriched scope.
    app.UseMiddleware<CorrelationMiddleware>();

    // Serilog's own request logging — one structured line per HTTP
    // request with method, path, status, elapsed_ms. Complements the
    // finer-grained trace events we emit inside handlers.
    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} in {Elapsed:0.00}ms";
        o.GetLevel = (httpCtx, elapsed, ex) =>
        {
            if (ex is not null) return LogEventLevel.Error;
            if (httpCtx.Response.StatusCode >= 500) return LogEventLevel.Error;
            if (httpCtx.Response.StatusCode >= 400) return LogEventLevel.Warning;
            // Health + OpenAPI polling would drown the log at Info level.
            var path = httpCtx.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/health", StringComparison.Ordinal)) return LogEventLevel.Debug;
            if (path.StartsWith("/openapi", StringComparison.Ordinal)) return LogEventLevel.Debug;
            return LogEventLevel.Information;
        };
    });

    app.UseRateLimiter();
    app.MapControllers();

    // Sprint 5.5: Health check endpoints (anonymous).
    // /health/live  — always 200, no checks (liveness probe).
    // /health/ready — runs the "database" check (readiness probe).
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
        .AllowAnonymous();
    app.MapHealthChecks("/health/ready")
        .AllowAnonymous();
}

app.Run();
