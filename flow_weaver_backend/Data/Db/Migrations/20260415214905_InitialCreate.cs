using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adapter_actions",
                columns: table => new
                {
                    AdapterActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    PathParams = table.Column<string>(type: "jsonb", nullable: false),
                    QueryParams = table.Column<string>(type: "jsonb", nullable: false),
                    RequestBody = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseSchema = table.Column<string>(type: "jsonb", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adapter_actions", x => x.AdapterActionId);
                });

            migrationBuilder.CreateTable(
                name: "adapters",
                columns: table => new
                {
                    AdapterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    BaseURL = table.Column<string>(type: "text", nullable: false),
                    AuthConfig = table.Column<string>(type: "jsonb", nullable: false),
                    Headers = table.Column<string>(type: "jsonb", nullable: false),
                    TLSSkipVerify = table.Column<bool>(type: "boolean", nullable: false),
                    HealthCheck = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adapters", x => x.AdapterId);
                });

            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    AgentRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TraceId = table.Column<string>(type: "text", nullable: false),
                    AgentName = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentAgentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToolCalls = table.Column<string>(type: "jsonb", nullable: false),
                    TokensIn = table.Column<int>(type: "integer", nullable: false),
                    TokensOut = table.Column<int>(type: "integer", nullable: false),
                    CostEstimateUSD = table.Column<double>(type: "double precision", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_runs", x => x.AgentRunId);
                });

            migrationBuilder.CreateTable(
                name: "ai_agents",
                columns: table => new
                {
                    AIAgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModelOverride = table.Column<string>(type: "text", nullable: true),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    Tools = table.Column<List<string>>(type: "text[]", nullable: false),
                    MaxIterations = table.Column<int>(type: "integer", nullable: false),
                    Temperature = table.Column<double>(type: "double precision", nullable: false),
                    Config = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_agents", x => x.AIAgentId);
                });

            migrationBuilder.CreateTable(
                name: "ai_conversations",
                columns: table => new
                {
                    AIConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Messages = table.Column<string>(type: "jsonb", nullable: false),
                    Context = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TokenUsage = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_conversations", x => x.AIConversationId);
                });

            migrationBuilder.CreateTable(
                name: "ai_providers",
                columns: table => new
                {
                    AIProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    BaseURL = table.Column<string>(type: "text", nullable: true),
                    EncryptedApiKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    DefaultModel = table.Column<string>(type: "text", nullable: false),
                    Config = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_providers", x => x.AIProviderId);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    EncryptedPassword = table.Column<byte[]>(type: "bytea", nullable: true),
                    EncryptedPrivateKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    Extra = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credentials", x => x.CredentialId);
                });

            migrationBuilder.CreateTable(
                name: "device_pools",
                columns: table => new
                {
                    DevicePoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    FilterRules = table.Column<string>(type: "jsonb", nullable: false),
                    StaticMembers = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_pools", x => x.DevicePoolId);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceName = table.Column<string>(type: "text", nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    Vendor = table.Column<string>(type: "text", nullable: false),
                    OsVersion = table.Column<string>(type: "text", nullable: false),
                    Site = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Properties = table.Column<string>(type: "jsonb", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "inventory_sources",
                columns: table => new
                {
                    InventorySourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Config = table.Column<string>(type: "jsonb", nullable: false),
                    Mapping = table.Column<string>(type: "jsonb", nullable: false),
                    SyncMode = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_sources", x => x.InventorySourceId);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Tag = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ClaimedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "service_definitions",
                columns: table => new
                {
                    ServiceDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    InputSchema = table.Column<string>(type: "jsonb", nullable: false),
                    OutputSchema = table.Column<string>(type: "jsonb", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: true),
                    ScriptLanguage = table.Column<string>(type: "text", nullable: true),
                    TargetMode = table.Column<string>(type: "text", nullable: false),
                    MaxParallel = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    RetryPolicy = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_definitions", x => x.ServiceDefinitionId);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Triggers = table.Column<List<string>>(type: "text[]", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SkillType = table.Column<string>(type: "text", nullable: false),
                    ActionConfig = table.Column<string>(type: "jsonb", nullable: false),
                    ParameterMapping = table.Column<string>(type: "jsonb", nullable: false),
                    Examples = table.Column<string>(type: "jsonb", nullable: false),
                    Template = table.Column<string>(type: "jsonb", nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmationRequired = table.Column<bool>(type: "boolean", nullable: false),
                    LearnedFrom = table.Column<string>(type: "text", nullable: false),
                    UseCount = table.Column<int>(type: "integer", nullable: false),
                    SuccessCount = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.SkillId);
                });

            migrationBuilder.CreateTable(
                name: "step_runs",
                columns: table => new
                {
                    StepRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "text", nullable: false),
                    ServiceDefId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    InputPayload = table.Column<string>(type: "jsonb", nullable: false),
                    OutputPayload = table.Column<string>(type: "jsonb", nullable: false),
                    Logs = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkerId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_step_runs", x => x.StepRunId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_plans",
                columns: table => new
                {
                    WorkflowPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Intent = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Steps = table.Column<string>(type: "jsonb", nullable: false),
                    ServicesToCreate = table.Column<string>(type: "jsonb", nullable: false),
                    ServicesToReuse = table.Column<string>(type: "jsonb", nullable: false),
                    TargetDevices = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    TargetPools = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Risks = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    ApprovedBy = table.Column<string>(type: "text", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                    VerificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_plans", x => x.WorkflowPlanId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    InputPayload = table.Column<string>(type: "jsonb", nullable: false),
                    TargetDevices = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    TargetPools = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.WorkflowRunId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_triggers",
                columns: table => new
                {
                    WorkflowTriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Route = table.Column<string>(type: "text", nullable: true),
                    CronExpression = table.Column<string>(type: "text", nullable: true),
                    Timezone = table.Column<string>(type: "text", nullable: false),
                    InputSchema = table.Column<string>(type: "jsonb", nullable: false),
                    InputDefaults = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunStatus = table.Column<string>(type: "text", nullable: true),
                    NotificationWebhookURL = table.Column<string>(type: "text", nullable: true),
                    NotifyOn = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_triggers", x => x.WorkflowTriggerId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                columns: table => new
                {
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Nodes = table.Column<string>(type: "jsonb", nullable: false),
                    Edges = table.Column<string>(type: "jsonb", nullable: false),
                    Services = table.Column<string>(type: "jsonb", nullable: false),
                    ChangeSummary = table.Column<string>(type: "text", nullable: false),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PromotedBy = table.Column<string>(type: "text", nullable: false),
                    TestRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => x.WorkflowVersionId);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    InputSchema = table.Column<string>(type: "jsonb", nullable: false),
                    Nodes = table.Column<string>(type: "jsonb", nullable: false),
                    Edges = table.Column<string>(type: "jsonb", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    Environment = table.Column<string>(type: "text", nullable: false),
                    PromotedFrom = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeSummary = table.Column<string>(type: "text", nullable: false),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.WorkflowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adapter_actions_AdapterId",
                table: "adapter_actions",
                column: "AdapterId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_ConversationId",
                table: "agent_runs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_TraceId",
                table: "agent_runs",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_AgentId",
                table: "ai_conversations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_IpAddress",
                table: "devices",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_Tag_Priority_CreatedAt",
                table: "jobs",
                columns: new[] { "Status", "Tag", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_service_definitions_Type",
                table: "service_definitions",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_step_runs_WorkflowRunId",
                table: "step_runs",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_plans_ConversationId",
                table: "workflow_plans",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_plans_Status",
                table: "workflow_plans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_Status",
                table: "workflow_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId",
                table: "workflow_runs",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_Enabled_NextRunAt",
                table: "workflow_triggers",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_WorkflowId",
                table: "workflow_triggers",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowId_Version",
                table: "workflow_versions",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_Environment",
                table: "workflows",
                column: "Environment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adapter_actions");

            migrationBuilder.DropTable(
                name: "adapters");

            migrationBuilder.DropTable(
                name: "agent_runs");

            migrationBuilder.DropTable(
                name: "ai_agents");

            migrationBuilder.DropTable(
                name: "ai_conversations");

            migrationBuilder.DropTable(
                name: "ai_providers");

            migrationBuilder.DropTable(
                name: "credentials");

            migrationBuilder.DropTable(
                name: "device_pools");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "inventory_sources");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "service_definitions");

            migrationBuilder.DropTable(
                name: "skills");

            migrationBuilder.DropTable(
                name: "step_runs");

            migrationBuilder.DropTable(
                name: "workflow_plans");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "workflow_triggers");

            migrationBuilder.DropTable(
                name: "workflow_versions");

            migrationBuilder.DropTable(
                name: "workflows");
        }
    }
}
