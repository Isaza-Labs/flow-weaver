// Per-route guide content registry.
//
// Each entry has:
//   match    — function that takes the current pathname and returns true
//              if this entry applies. More specific routes should be
//              listed BEFORE less specific ones — the first match wins.
//   title    — short title shown at the top of the panel
//   intro    — one or two sentences describing the page
//   tips     — bullet-point list of "what you can do here"
//   aiContext — short paragraph fed to the AI as system context when the
//               user clicks "Ask the guide"
//
// To add a new page: append an entry to GUIDE_ENTRIES below. There is
// no build step.

export interface GuideEntry {
  match: (pathname: string) => boolean;
  title: string;
  intro: string;
  tips: string[];
  aiContext: string;
  // Optional deep link to the matching /docs page. Rendered as
  // "Full documentation →" at the bottom of the PageHeader hint popover.
  docsHref?: string;
}

export const GUIDE_ENTRIES: GuideEntry[] = [
  // ---------- Import ----------
  // Listed before the /workflows/<id> editor entry: "import" is a literal
  // segment that the editor's own regex would otherwise swallow.
  {
    match: (p) => p === '/workflows/import',
    title: 'Import workflow',
    intro: 'Upload a workflow definition from FlowWeaver v1, n8n, Itential, or any DAG format. The agent translates it into a FlowWeaver graph and reports what it could not resolve.',
    tips: [
      'The parse step runs first and shows the detected source format before anything is written',
      'Unresolved dependencies (missing snippets, integrations, credentials) are listed so you can create them before committing',
      'The imported workflow always lands in the draft environment — promote it once you have reviewed the graph',
      'Nothing is persisted until you commit the import, so a bad parse costs nothing',
    ],
    aiContext:
      'The user is on /workflows/import, uploading a foreign workflow definition (FlowWeaver v1, n8n, Itential or generic DAG) for the agent to translate, with a dependency-resolution step before the import is committed.',
    docsHref: '/docs/workflows',
  },

  // ---------- Per-resource permissions ----------
  {
    match: (p) => /^\/workflows\/[^/]+\/permissions$/.test(p),
    title: 'Workflow permissions',
    intro: 'Per-resource grants for this one workflow: give individual users owner, editor, runner or viewer on it without changing their global role.',
    tips: [
      'Grants here only raise privileges on this workflow — they never reduce what a role already allows',
      'Admins always pass, so there is no need to grant them anything',
      'owner can delete and re-grant; editor can change the graph; runner can only execute; viewer is read-only',
      'Global RBAC still applies underneath — this is an overlay, not a replacement',
    ],
    aiContext:
      'The user is on the per-workflow permissions page at /workflows/<id>/permissions, managing per-resource grants (owner/editor/runner/viewer) for a single workflow.',
  },
  {
    match: (p) => /^\/integrations\/[^/]+\/permissions$/.test(p),
    title: 'Integration permissions',
    intro: 'Per-resource grants for this one integration: give individual users owner, editor, runner or viewer on it without changing their global role.',
    tips: [
      'Grants here only raise privileges on this integration',
      'Admins always pass',
      'runner is the useful one for service accounts — it can invoke actions but not edit the connection',
    ],
    aiContext:
      'The user is on the per-integration permissions page at /integrations/<id>/permissions, managing per-resource grants for a single integration.',
    docsHref: '/docs/integrations',
  },

  // ---------- Runs ----------
  {
    match: (p) => /^\/runs\/[^/]+$/.test(p),
    title: 'Run detail',
    intro: 'Everything one workflow run did: its status, timing, input payload, and the step-by-step outcome of every node the DAG reached.',
    tips: [
      'Click any step to expand its input, output and error in full',
      'A run that is still executing can be cancelled from here',
      'Steps that never ran show as skipped — usually a conditional edge that did not fire',
      'For a live view of a run in progress, use the Monitor view instead',
    ],
    aiContext:
      'The user is on a run detail page at /runs/<id>, inspecting one workflow run: status, timings, input payload and per-step results.',
    docsHref: '/docs/runs',
  },

  // ---------- Inventory ----------
  {
    match: (p) => p === '/device-pools',
    title: 'Device pools',
    intro: 'Named groups of devices that workflows target together, so a workflow can say "the core routers" instead of listing device ids.',
    tips: [
      'Each pool declares which environments may target it — draft, qa, production, in any combination',
      'Pool flags apply on top of the per-device ones: a run must be allowed by the pool AND by the member it reaches',
      'Membership is a static list; the filter_rules field exists in the model but the UI does not expose dynamic pools yet',
      'Deleting a pool does not touch its devices — only the grouping',
    ],
    aiContext:
      'The user is on /device-pools, managing named groups of devices with per-environment flags (allow_draft / allow_qa / allow_production) and a static member list.',
    docsHref: '/docs/device-pools',
  },
  {
    match: (p) => p === '/credentials',
    title: 'Credentials',
    intro: 'The auth material devices use at run time — username plus password, or an SSH private key. Secrets are encrypted at rest and never returned to the UI.',
    tips: [
      'A device with no credential fails its SSH steps with "credential 00000000-… not found"',
      'You can bulk-assign a credential to many devices at once from the Devices page',
      'Editing a credential leaves the stored secret untouched unless you type a new one',
      'Git repositories use this same catalog: HTTPS+PAT for token auth, SSH key for git@ URLs',
    ],
    aiContext:
      'The user is on /credentials, managing the encrypted credential catalog (username/password or SSH key) that devices and Git remotes reference by id.',
    docsHref: '/docs/credentials',
  },
  {
    match: (p) => p === '/qa',
    title: 'QA lab',
    intro: 'The workflows currently sitting in the qa environment and how close each one is to being promoted to production.',
    tips: [
      'Promotion to production requires a completed qa run within the last 48 hours',
      'The top tiles count the devices and pools that allow qa — a qa run can only reach those',
      'If a workflow shows no recent run, open it and run it against qa inventory first',
      'A run whose targets all disallow qa is refused outright rather than executing against nothing',
    ],
    aiContext:
      'The user is on /qa, the QA lab dashboard listing workflows in the qa environment with their promotion readiness, plus counts of qa-enabled devices and pools.',
    docsHref: '/docs/qa-lab',
  },
  {
    match: (p) => p === '/subflows',
    title: 'Reusable subflows',
    intro: 'Workflows tagged as subflows, which other workflows can invoke through a subflow node instead of duplicating the graph.',
    tips: [
      'Tag or untag a workflow from its metadata pane in the editor — this page is the read-only index',
      'A subflow inherits the parent run\'s target devices',
      'Cycles are detected at enqueue time, so a subflow cannot invoke itself transitively',
    ],
    aiContext:
      'The user is on /subflows, the index of workflows tagged as reusable subflows that other workflows invoke via a subflow node.',
    docsHref: '/docs/workflows',
  },
  {
    match: (p) => p === '/vendor-commands',
    title: 'Vendor commands',
    intro: 'The catalog the SSH command validator checks against. Commands it does not recognise surface as warnings when a workflow is saved, before any run reaches a device.',
    tips: [
      'Warnings are advisory — they never block a save or a run',
      'The catalog is per vendor/platform, so the same verb can be valid on one OS and unknown on another',
      'The authoring agent reads these warnings and can correct typos on its own',
    ],
    aiContext:
      'The user is on /vendor-commands, the per-vendor command catalog that drives the SSH command validator warnings on workflow create/update.',
  },

  // ---------- Govern ----------
  {
    match: (p) => p === '/policies/audit',
    title: 'Policy audit',
    intro: 'Every time a policy blocked a workflow create, update or promotion, it emitted a trace event. This rolls those events up so you can see which rules actually fire.',
    tips: [
      'A rule with a high block count may be too aggressive — or may be doing exactly its job',
      'A rule that never fires is either dead or well respected; check its conditions before deleting it',
      'Events are derived from traces, so the retention window is the trace window',
    ],
    aiContext:
      'The user is on /policies/audit, reviewing the rolled-up history of policy denials emitted on workflow create/update/promote.',
    docsHref: '/docs/policies',
  },
  {
    match: (p) => p === '/policies',
    title: 'Policies',
    intro: 'Corporate guardrails evaluated on every workflow create, update and promotion. Deny rules block matching operations; gates require historical conditions before a promotion proceeds.',
    tips: [
      'Policies apply to everyone — unlike permission grants, admin does not bypass them',
      'A deny rule with an empty "when" matches always; add env / snippet_type / ssh_command_regex to narrow it',
      'Gates are for promotion only: require N successful runs, or a successful run within N days',
      'ssh_command_regex rules are evaluated per command inside the SSH handler, so they can block a single verb',
    ],
    aiContext:
      'The user is on /policies, authoring guardrails: deny rules matched on env/device/snippet_type/ssh command, and promotion gates requiring historical run conditions.',
    docsHref: '/docs/policies',
  },
  {
    match: (p) => p === '/permissions',
    title: 'Permissions',
    intro: 'Granular capability grants. Each grant confers a set of capabilities on the users assigned to it, optionally scoped to an environment, device or resource.',
    tips: [
      'Built-in grants reproduce the legacy operator / viewer roles and are read-only',
      'Admin bypasses every capability check, so admins never need a grant',
      'Scoping a grant to an environment means it does not apply to calls that do not supply one — grants never leak into unscoped checks',
      'Granular mode has to be enabled in Settings before these grants take effect',
    ],
    aiContext:
      'The user is on /permissions, managing granular capability grants (capability keys plus optional environment/device/resource conditions) assigned to users.',
  },

  // ---------- Intelligence ----------
  {
    match: (p) => p === '/ai/agents',
    title: 'AI agents',
    intro: 'Named roles the chat controller binds to. Each agent picks a provider, a model, a tool allowlist, and an optional extra system prompt.',
    tips: [
      'The default agent is the one the chat surface and the messaging channels use',
      'The tool allowlist is the hard boundary of what an agent can do — narrowing it is the cheapest safety control',
      'Prompt skills are prepended automatically; the extra system prompt is appended after them',
      'Permissions still apply to every tool call: an agent cannot do what its caller cannot',
    ],
    aiContext:
      'The user is on /ai/agents, configuring named agent roles with a provider, model, tool allowlist and optional trailing system prompt.',
    docsHref: '/docs/ai/agents',
  },
  {
    match: (p) => p === '/ai/providers',
    title: 'AI providers',
    intro: 'The LLM backends that serve the assistant. API keys are encrypted server-side and never returned to the UI.',
    tips: [
      'At least one enabled provider is required before the chat surface works',
      'Each agent picks its provider and model independently',
      'A key can be replaced but never read back — leave the field blank to keep the current one',
    ],
    aiContext:
      'The user is on /ai/providers, connecting LLM backends with encrypted API keys that agents bind to.',
    docsHref: '/docs/ai/providers',
  },
  {
    match: (p) => p === '/ai/specs',
    title: 'API specs',
    intro: 'OpenAPI documents the agent reads to discover and execute REST operations. The `api` field is the identifier the agent sees when it picks an operation.',
    tips: [
      'Specs can be scoped to an integration or left global',
      'Reference stored secrets from a spec as ${secret:secret:<name>:value} rather than pasting credentials',
      'The active specs are exposed to the agent when a conversation starts, so changes apply to new conversations',
    ],
    aiContext:
      'The user is on /ai/specs, managing the OpenAPI YAML catalog the agent uses to discover and call REST operations.',
    docsHref: '/docs/ai/specs',
  },
  {
    match: (p) => p === '/ai',
    title: 'Intelligence',
    intro: 'The agent workspace: the chat surface, the providers that serve it, the agents that configure it, the prompt skills that shape it, and the API specs it can call.',
    tips: [
      'Start at Chat if you just want to build a workflow from a plain-English description',
      'Providers must be configured before anything else here works',
      'Skills teach the assistant local facts and conventions without touching agent config',
    ],
    aiContext:
      'The user is on /ai, the Intelligence landing page linking to chat, providers, agents, prompt skills and API specs.',
    docsHref: '/docs/ai',
  },

  // ---------- Integrations ----------
  {
    match: (p) => /^\/integrations\/git\/[^/]+$/.test(p),
    title: 'Git repository',
    intro: 'One registered remote: browse its branches and files, edit, commit and push, and inspect diffs — all against a server-side working copy.',
    tips: [
      'Auth comes from the Credentials catalog: HTTPS+PAT for token auth, an SSH key for git@ / ssh:// URLs',
      'Operations are serialized per repository, so two writes cannot trash the index',
      'Webhooks registered on this repo can trigger workflows on push',
    ],
    aiContext:
      'The user is on a Git repository detail page at /integrations/git/<id>, browsing and editing a server-side working copy of a registered remote.',
    docsHref: '/docs/integrations',
  },
  {
    match: (p) => p === '/integrations/git',
    title: 'Git repositories',
    intro: 'Remotes the platform can clone, edit, commit and push to. Each registration points at a URL and the credential that authenticates against it.',
    tips: [
      'HTTPS remotes need a token credential; git@ / ssh:// remotes need an SSH key credential',
      'Cloning happens server-side into a per-repository working copy',
      'Push webhooks can start a workflow — useful for config-as-code pipelines',
    ],
    aiContext:
      'The user is on /integrations/git, registering Git remotes with credentials for server-side clone/commit/push.',
    docsHref: '/docs/integrations',
  },

  // ---------- Administration ----------
  {
    match: (p) => p === '/admin/artifacts',
    title: 'Artifacts',
    intro: 'Every document generated by the platform, split by origin: Reports come out of workflow runs, Exports are generated without a workflow — by the agent in chat or a direct API call.',
    tips: [
      'The Report tab lists workflow output — each row links to the run that produced it, and schedule-fired runs are attributed to the schedule',
      'The Export tab lists agent- and API-generated documents — agent rows link back to the originating conversation',
      'Opening a row lands on the artifact detail, which logs a read_prompt audit entry',
    ],
    aiContext:
      'The user is on /admin/artifacts, the audit index of generated documents split into Report (workflow-generated) and Export (agent- or API-generated, no workflow) sections.',
    docsHref: '/docs/admin/artifacts',
  },
  {
    match: (p) => /^\/admin\/artifacts\/[^/]+$/.test(p),
    title: 'Artifact detail',
    intro: 'One generated document with the user, prompt and origin that produced it. Opening this page writes a read_prompt audit row.',
    tips: [
      'The prompt is retained deliberately — this page exists for audit and data-exfiltration review',
      'Downloading the document does not remove it from the audit trail',
    ],
    aiContext:
      'The user is viewing a single generated document at /admin/artifacts/<id>, including the originating prompt or workflow run; opening it emits a read_prompt audit event.',
    docsHref: '/docs/admin/artifacts',
  },
  {
    match: (p) => p === '/admin/audit',
    title: 'Audit log',
    intro: 'The trail of sign-ins, sensitive writes and entity mutations. Every meaningful write on your data lands here.',
    tips: [
      'Filter by actor, action or entity to narrow a large window',
      'Auth events (sign-in, lockout, role change) and domain events (workflow created, device deleted) share one timeline',
      'Rows are append-only — nothing here can be edited or removed from the UI',
    ],
    aiContext:
      'The user is on /admin/audit, reviewing the append-only audit trail of authentication events and entity mutations.',
    docsHref: '/docs/admin/audit',
  },
  {
    match: (p) => p === '/admin/traces',
    title: 'Traces',
    intro: 'One row per critical action. Duration is recorded when the action closes, so a "started" row older than a few seconds means the handler is still running — or died silently.',
    tips: [
      'Long-lived "started" rows are the fastest way to spot a hung handler',
      'Policy denials emit trace events too — the Policy audit view rolls those up',
      'Traces are the retention window for anything derived from them',
    ],
    aiContext:
      'The user is on /admin/traces, inspecting per-action trace rows with durations, used to spot hung or silently-failed handlers.',
    docsHref: '/docs/admin/traces',
  },
  {
    match: (p) => p === '/admin/users',
    title: 'Users',
    intro: 'Create users, assign roles, and disable accounts.',
    tips: [
      'admin bypasses every permission and policy check — grant it sparingly',
      'Disabling an account is reversible; deleting it is not',
      'For finer control than the three roles, enable granular mode and use Permissions',
    ],
    aiContext:
      'The user is on /admin/users, managing accounts and their admin/operator/viewer roles.',
    docsHref: '/docs/admin/users',
  },
  {
    match: (p) => p === '/admin/settings',
    title: 'Settings',
    intro: 'Global toggles — the granular RBAC overlay and feature flags. Changes propagate within about a minute because the server caches them.',
    tips: [
      'Granular RBAC has to be switched on here before capability grants take effect',
      'A toggle that seems not to apply is usually just the cache TTL — wait a minute before debugging',
    ],
    aiContext:
      'The user is on /admin/settings, flipping global feature flags including the granular RBAC overlay, with a server-side cache TTL of about a minute.',
  },
  {
    match: (p) => p === '/admin/secrets',
    title: 'Secrets',
    intro: 'Named credentials the AI agent consumes when calling REST operations. Reference them from an OpenAPI spec instead of pasting the value into the document.',
    tips: [
      'Reference syntax in a spec is ${secret:secret:<name>:value}',
      'Values are encrypted at rest and never returned to the UI',
      'These are separate from device Credentials — those authenticate to devices, these to REST APIs',
    ],
    aiContext:
      'The user is on /admin/secrets, managing named encrypted secrets that OpenAPI specs reference when the agent calls REST operations.',
  },
  {
    match: (p) => p === '/admin/mcp-servers',
    title: 'MCP servers',
    intro: 'External Model Context Protocol servers. Once registered, both the chat agent and the mcp_call workflow node can invoke their tools.',
    tips: [
      'Test & sync discovers the server\'s tool list — a 401, or a 403 that names a credential problem, means re-authorising',
      'A 403 about the host not being allowed is a reachability problem, not an auth one',
      'Tool invocation is still permission-checked: mcp.execute must be granted for the server and tool',
    ],
    aiContext:
      'The user is on /admin/mcp-servers, registering external MCP servers whose tools the chat agent and the mcp_call workflow node can call.',
    docsHref: '/docs/admin/mcp-servers',
  },
  {
    match: (p) => p === '/admin/messaging-channels',
    title: 'Messaging channels',
    intro: 'Connect Slack, Teams, WhatsApp or Telegram to the agent so people can talk to it without opening the web UI.',
    tips: [
      'A channel never widens privileges — the linked user\'s permissions apply exactly as they do on the web',
      'Each channel maps inbound messages onto the default assistant agent',
      'Unlinked senders get no access at all, so linking is the enrolment step',
    ],
    aiContext:
      'The user is on /admin/messaging-channels, connecting Slack/Teams/WhatsApp/Telegram to the assistant with the linked user\'s own permissions.',
    docsHref: '/docs/ai/channels',
  },
  {
    match: (p) => p === '/email',
    title: 'Email',
    intro: 'SMTP relays a workflow sends mail through. Register one per provider account — Gmail, Microsoft 365, SendGrid, Amazon SES, Mailgun or your own server.',
    tips: [
      'Picking a provider fills in host, port and encryption — you only supply the credentials',
      'Send a test message before wiring a workflow to it; a bad password fails identically at run time',
      'A workflow sends with the email_send node, which names a channel or falls back to the default one',
    ],
    aiContext:
      'The user is on /email, configuring outbound SMTP channels that the email_send workflow node delivers through.',
    docsHref: '/docs/email',
  },
  {
    match: (p) => p === '/admin/python-packages',
    title: 'Python packages',
    intro: 'The extra modules python_snippet scripts are allowed to import. Adding a pip package makes the worker install it into the shared environment.',
    tips: [
      'The import name and the pip spec are separate fields — they often differ (e.g. yaml vs PyYAML)',
      'Installation happens on the worker, so a newly added package is usable once that finishes',
      'A snippet importing a module that is not on this list fails at run time, not at save time',
    ],
    aiContext:
      'The user is on /admin/python-packages, managing the allowlist of importable modules for python_snippet plus the pip spec the worker installs.',
  },
  {
    match: (p) => p === '/admin/slo',
    title: 'Service-level objectives',
    intro: 'The operational targets the platform commits to, aggregated over the window you pick.',
    tips: [
      'Targets are platform-wide defaults, not per-installation negotiated numbers',
      'A missed objective here is a starting point for investigation, not an incident on its own',
    ],
    aiContext:
      'The user is on /admin/slo, reviewing platform service-level objectives aggregated over a selectable window.',
  },
  {
    match: (p) => p === '/admin/actions',
    title: 'Reusable actions',
    intro: 'A read-only view of the legacy skills table, kept for reference while that data is migrated.',
    tips: [
      'Nothing here is editable — this page exists so the rows stay inspectable',
      'For current agent guidance, use Prompt skills under Intelligence instead',
    ],
    aiContext:
      'The user is on /admin/actions, a read-only view of the legacy reusable-actions/skills table.',
  },
  {
    match: (p) => p === '/admin',
    title: 'Admin',
    intro: 'Health signals at a glance: queue depth, recent runs, sign-in activity, and the way into the audit trail. Auto-refreshes every 30 seconds.',
    tips: [
      'The queue pills are hidden when the queue is empty, so an idle system stays clean',
      'A queue that only grows means no worker is claiming jobs',
      'Cards below link into users, the audit log, traces, reports, SLO and global settings',
    ],
    aiContext:
      'The user is on /admin, the administration landing page showing queue depth, run activity, auth activity and links into users/audit/traces/reports/slo/settings.',
    docsHref: '/docs/admin',
  },

  // ---------- Account ----------
  {
    match: (p) => p === '/settings/password',
    title: 'Change password',
    intro: 'Update the password on your own account.',
    tips: [
      'There is no self-service reset flow — if you are locked out, an admin has to set a new password',
      'Changing your password does not sign out your other sessions',
    ],
    aiContext:
      'The user is on /settings/password, changing their own account password.',
    docsHref: '/docs/account',
  },

  // ---------- Schedules ----------
  {
    match: (p) => p === '/schedules/calendar',
    title: 'Schedule calendar',
    intro: 'A month-grid view of every scheduled run across every workflow. Click any day to see exactly which schedules fire that day.',
    tips: [
      'Use the ← / → arrows to move between months, or "Today" to jump to the current month',
      'Each cell shows the count of fires plus the first three; "+N more" means the day has more',
      'Click any day to open a detail panel listing every fire that day with workflow + schedule links',
      'All times are computed in the schedule\'s own timezone but displayed in your local timezone',
      'Only enabled schedule-type triggers are shown — disabled ones do not appear on the calendar',
    ],
    aiContext:
      'The user is on the global schedule calendar at /schedules/calendar. It shows a month-grid view of all enabled cron-driven schedule triggers across every workflow, with click-to-drill-down per day.',
    docsHref: '/docs/schedules',
  },
  {
    match: (p) => p === '/schedules',
    title: 'All schedules',
    intro: 'Every cron-driven schedule across every workflow in one filterable, sortable table. Use this when you need a system-wide view.',
    tips: [
      'Filter by name/workflow/cron/timezone using the search box',
      'Filter by enabled / disabled status using the dropdown',
      'Click column headers (Workflow, Schedule, Next run) to sort',
      'Per-row "Open" jumps to the per-workflow schedules page',
      'Use the "📅 Calendar view" button (top right) to see all fires on a month grid',
      'To create a new schedule, open any workflow and use its Schedules page',
    ],
    aiContext:
      'The user is on the global /schedules page that lists every cron-driven schedule trigger in the system, joined with its workflow name. It supports search, status filter, sort by next-run, and per-row toggle/delete.',
    docsHref: '/docs/schedules',
  },
  {
    match: (p) => /^\/workflows\/[^/]+\/schedules\/new$/.test(p),
    title: 'New schedule',
    intro: 'Create a recurring cron-based trigger for this workflow. Pick a Repeat preset (Hourly / Daily / Weekdays / Weekly / Monthly) and the form fills in the matching cron under the hood.',
    tips: [
      'The Repeat picker generates the cron expression for you — pick "Custom" for raw cron entry',
      'The "Next 5 runs" preview updates live so you can sanity-check before saving',
      'Timezone defaults to UTC; pick from the dropdown or type any IANA name',
      'Add a webhook URL to be notified when this schedule fails or completes (Slack/Discord/PagerDuty all work)',
      '"Notify on failure" is enabled by default, "On completion" is opt-in',
    ],
    aiContext:
      'The user is on the new schedule form at /workflows/<id>/schedules/new. They are creating a cron-driven schedule trigger for a specific workflow with a Repeat picker (Hourly/Daily/Weekdays/Weekly/Monthly/Custom), timezone, optional webhook notification URL, and "Next 5 runs" preview.',
    docsHref: '/docs/schedules',
  },
  {
    match: (p) => /^\/workflows\/[^/]+\/schedules\/[^/]+\/edit$/.test(p),
    title: 'Edit schedule',
    intro: 'Modify an existing schedule. The form pre-fills with the current cron expression, parsed back into the Repeat picker where possible.',
    tips: [
      'If the existing cron does not match a known pattern, the form falls back to "Custom" mode',
      'Saving recomputes next_run_at immediately — no need to wait for the next scheduler tick',
      'To delete the schedule entirely, go back to the per-workflow Schedules list and use the Delete button',
    ],
    aiContext:
      'The user is editing an existing schedule trigger at /workflows/<id>/schedules/<triggerId>/edit. The form pre-fills from the saved cron expression and timezone.',
    docsHref: '/docs/schedules',
  },
  {
    match: (p) => /^\/workflows\/[^/]+\/schedules$/.test(p),
    title: 'Schedules for this workflow',
    intro: 'Every cron-driven schedule attached to this workflow. AWX-style.',
    tips: [
      'Click "+ Add Schedule" to create a new one',
      'Each row shows the next run time, last run time, and last status',
      'Edit / Disable / Delete actions live in the rightmost column',
      'Disabled schedules stay in the list but the scheduler skips them on every tick',
    ],
    aiContext:
      'The user is on the per-workflow schedules page at /workflows/<id>/schedules. It lists only schedule-type triggers for this workflow with edit / toggle / delete per row.',
    docsHref: '/docs/schedules',
  },
  {
    match: (p) => /^\/workflows\/[^/]+\/triggers$/.test(p),
    title: 'All triggers for this workflow',
    intro: 'Every way this workflow can be started: manually, by API call, on a schedule, or by an event.',
    tips: [
      'Tabs at the top filter by trigger type (All / Schedule / API / Manual / Event)',
      'For schedule triggers, click Edit to modify the cron expression',
      'API triggers expose a POST endpoint at /api/v1/triggers/endpoint/<route> — the request body becomes the workflow input',
      'Event triggers can be defined here but the event subsystem is not yet dispatching them (Phase 6+)',
    ],
    aiContext:
      'The user is on the generic triggers page at /workflows/<id>/triggers, which surfaces all four trigger types (manual, api, schedule, event) for one workflow with type-tabbed filtering.',
    docsHref: '/docs/schedules',
  },

  // ---------- Workflows ----------
  {
    match: (p) => /^\/workflows\/[^/]+$/.test(p),
    title: 'Workflow editor',
    intro: 'The visual DAG editor for one workflow. Drag snippets from the left palette onto the canvas, connect them with edges, and run the workflow against target devices.',
    tips: [
      'Drag any service from the left palette onto the canvas to add a node',
      'Click a node to open its config panel on the right',
      'Drag from a node\'s right handle to another node\'s left handle to create a success edge; use the edge context menu to switch to failure or always',
      '"Save" persists the workflow; "Run" executes it against the device picker selection',
      '"Schedules" opens the per-workflow schedules manager (cron triggers)',
      '"Triggers" opens the all-types trigger manager',
      '"Show Run Data" overlays the latest run\'s step outputs onto the graph',
      'Open Chat (top right, if shown) jumps back to the AI conversation that built this workflow',
    ],
    aiContext:
      'The user is in the workflow editor at /workflows/<id>. It uses Svelte Flow for a DAG canvas with a service palette on the left and a node config panel on the right. Header buttons include Save, Export, Run, Schedules, Triggers, Show Run Data, Back, and Open Chat.',
    docsHref: '/docs/workflows',
  },
  {
    match: (p) => p === '/workflows',
    title: 'Workflows',
    intro: 'Every workflow in the system. Workflows are DAGs of snippets — pick one to edit it, or run one to execute it against a set of devices.',
    tips: [
      'Toggle between Draft and Production environments using the tabs',
      'Click any workflow name to open the editor',
      'Use Promote to move a draft workflow into production after testing',
      'Use Rollback to revert a production workflow to an earlier version',
      'The Run button on the editor lets you execute against arbitrary target devices',
    ],
    aiContext:
      'The user is on the workflows list at /workflows. It supports draft/production environment tabs, search, promote, rollback, and version history. Each workflow is a DAG of snippets that can be run against target devices.',
    docsHref: '/docs/workflows',
  },

  // ---------- Snippets ----------
  {
    match: (p) => p === '/snippets/new',
    title: 'New snippet',
    intro: 'Create a reusable building block that workflows can call. Pick a type and a starter template loads automatically.',
    tips: [
      'Python snippet is the most flexible — get_input() and set_output({...}) are your I/O primitives',
      'REST call lets you POST/GET to any HTTP endpoint without writing Python',
      'Ansible playbook runs an Ansible play in a sandbox',
      'Per-device target mode runs once for each selected target; "Once" runs a single time',
      'After Create, you land on the full editor where you can refine the code, schemas, and retry policy',
    ],
    aiContext:
      'The user is on /snippets/new creating a new snippet. The form has Name, Description, Type (python_snippet/ansible_playbook/rest_call/transform/jmespath/ping/integration_action), and Target Mode (per_device/once). Picking a type loads a starter code template.',
    docsHref: '/docs/snippets',
  },
  {
    match: (p) => /^\/snippets\/[^/]+$/.test(p),
    title: 'Snippet editor',
    intro: 'Edit one snippet: name, code, schemas, retry policy. The "▶ Test Run" button at the top runs the snippet in isolation against custom input so you can verify it before wiring it into a workflow.',
    tips: [
      'Click "▶ Test Run" (top right) to test the snippet immediately — opens a dialog with input + device picker + live output panel',
      'Save persists every field; the indicator next to the button shows save status',
      'The input_schema and output_schema are JSON Schema — workflows generate forms from them',
      'retry_policy is JSON: {"max_retries": N, "backoff": "exponential"|"linear"|"fixed", "initial_delay": seconds}',
      'For Python snippets, use from flowweaver_runtime import get_input, set_output — define run(ctx); the runtime auto-invokes it on exit',
    ],
    aiContext:
      'The user is editing a snippet at /snippets/<id>. The page has Name, Description, Code, Input/Output Schema, Retry Policy, and type-specific fields. There is a "Test Run" button that runs the snippet in isolation via POST /api/v1/snippet/<id>/test.',
    docsHref: '/docs/snippets',
  },
  {
    match: (p) => p === '/snippets',
    title: 'Snippets',
    intro: 'Reusable building blocks workflows can call. Each snippet is a Python snippet, REST call, Ansible play, transform, ping, or integration action.',
    tips: [
      'Click "+ New Snippet" to create one — pick a type and a starter template loads',
      'Edit takes you to the full editor (code, schemas, retry policy)',
      'Test runs the snippet in isolation against custom input — great for catching syntax errors before adding it to a workflow',
      'Use the search box to filter by name, type, or description',
    ],
    aiContext:
      'The user is on the snippets list at /snippets. They can create new snippets, edit existing ones, test them in isolation, and delete them. Each row has Edit / Test / Delete buttons.',
    docsHref: '/docs/snippets',
  },

  // ---------- Runs / monitoring ----------
  {
    match: (p) => /^\/runs\/[^/]+\/monitor$/.test(p),
    title: 'Run monitor',
    intro: 'Live view of one workflow run. Each node turns green/red as its steps complete, edges animate as the run progresses.',
    tips: [
      'Click any node to see its per-device step results',
      'The timeline below the canvas shows step start/end times',
      'Failed steps show error + logs in their detail panel',
      'Auto-refresh keeps the view live while the run is in progress',
    ],
    aiContext:
      'The user is on the run monitor at /runs/<id>/monitor. It shows live status of a workflow run with per-node coloring, animated edges, and a step timeline below the canvas.',
    docsHref: '/docs/runs',
  },
  {
    match: (p) => p === '/runs',
    title: 'Runs',
    intro: 'Every workflow execution, past and present. Pick one to see its full step-by-step output.',
    tips: [
      'Status badges show queued / running / completed / failed',
      'The trigger column shows what started the run (manual, api, schedule, etc.)',
      'Click any run row to open the live monitor view',
      'Test runs created by the service test endpoint show up here with trigger "test:service:<id>"',
    ],
    aiContext:
      'The user is on the runs list at /runs. It shows every workflow_run row with status, trigger source, started_at, and completed_at. Each row links to the live run monitor.',
    docsHref: '/docs/runs',
  },

  // ---------- Inventory ----------
  {
    match: (p) => p === '/devices',
    title: 'Devices',
    intro: 'Your network device inventory — every router, switch, firewall, and host that workflows can target.',
    tips: [
      'Devices have a name, IP, platform, vendor, model, OS version, site, role, and arbitrary JSONB properties',
      'Inventory sources (NetBox etc.) sync into this table automatically — see /inventory-sources',
      'Devices can also be pooled (/pools) into static or dynamic groups for easy targeting',
    ],
    aiContext:
      'The user is on /devices, which lists every network device in the inventory. Devices can be grouped into pools and synced from external inventory sources like NetBox.',
    docsHref: '/docs/devices',
  },

  // ---------- Other ----------
  {
    match: (p) => p === '/ai/skills',
    title: 'AI Skills',
    intro: 'Markdown prompt fragments that shape every chat\'s system prompt. Edits invalidate the loader cache so the next message picks them up immediately.',
    tips: [
      'Skills are concatenated in SortOrder then Name',
      'Supports {{CurrentDate}} and {{ToolList}} placeholders',
      'Inactive skills stay in the DB but are excluded from the prompt',
    ],
    aiContext:
      'The user is on /ai/skills, the DB-backed prompt skill catalog. These markdown fragments are concatenated into every chat\'s system prompt.',
    docsHref: '/docs/ai/skills',
  },
  {
    match: (p) => p === '/integrations',
    title: 'Integrations',
    intro: 'External REST systems with their auth config plus scoped skills and specs. Workflows invoke them via the integration_action service type.',
    tips: [
      'Each integration has a base URL + auth config; actions are individual endpoints under it',
      '"New integration" opens a 3-tab modal — the Skills and Specs tabs attach prompt fragments and OpenAPI YAMLs scoped to the integration',
      'Workflows reference integration actions via the integration_action service type; the handler pulls base URL + credentials from the integration row',
    ],
    aiContext:
      'The user is on /integrations, which lists their integrations and the actions each one exposes. Integrations can own prompt skills and OpenAPI specs the agent uses when calling them.',
    docsHref: '/docs/integrations',
  },
  {
    match: (p) => p.startsWith('/ai/chat'),
    title: 'AI chat',
    intro: 'Talk to the FlowWeaver AI agents — orchestrator (general), inventory (device queries), and workflow-builder (creates workflows from natural language).',
    tips: [
      'Pick the agent from the bottom-left dropdown',
      'Streaming responses arrive token by token',
      'For workflow-builder: prefix with [PLAN MODE] to design the workflow first before building, [BUILD MODE] to generate immediately',
      'After approval, the agent emits both service JSON blocks and the workflow JSON, and the backend creates them automatically',
      'The model picker (bottom bar) lets you swap provider/model per message without persisting',
    ],
    aiContext:
      'The user is on the AI chat page at /ai/chat. They are talking to one of the FlowWeaver agents (orchestrator, inventory, workflow-builder) with streaming responses, conversation history, and a per-message model picker.',
    docsHref: '/docs/ai/chat',
  },
  {
    match: (p) => p === '/',
    title: 'Dashboard',
    intro: 'Welcome to FlowWeaver — the home page. Use the sidebar on the left to navigate.',
    tips: [
      'Workflows: build and run network automation workflows',
      'Schedules: manage cron-driven recurring runs across all workflows',
      'Devices: your network inventory',
      'Runs: history of every workflow execution',
      'Snippets: reusable building blocks workflows call',
      'AI: chat with the workflow-builder to create workflows by describing them',
    ],
    aiContext:
      'The user is on the FlowWeaver dashboard at /. This is the home page; the main navigation is in the left sidebar.',
    docsHref: '/docs/dashboard',
  },
];

export const FALLBACK_GUIDE: GuideEntry = {
  match: () => true,
  title: 'FlowWeaver',
  intro: 'A network automation workflow platform. Use the sidebar to navigate between workflows, devices, runs, snippets, schedules, and AI chat.',
  tips: [
    'Workflows are DAGs of reusable snippets that run against target devices',
    'Schedule triggers fire workflows on cron expressions',
    'AI chat builds workflows from plain English',
    'Click "Ask the guide" below to ask any question about FlowWeaver',
  ],
  aiContext:
    'The user is on a FlowWeaver page that does not have a specific guide entry yet. Answer general FlowWeaver questions.',
};

export function findGuideForRoute(pathname: string): GuideEntry {
  for (const entry of GUIDE_ENTRIES) {
    if (entry.match(pathname)) return entry;
  }
  return FALLBACK_GUIDE;
}

// True when the route has its own entry rather than falling through to
// FALLBACK_GUIDE. PageHeader uses this to hide the ⓘ hint on pages whose
// only available text is the generic blurb — a hint that always says the
// same thing trains people to stop opening it. The mascot still shows the
// fallback, because there it is the panel's whole content, not a promise
// of page-specific help.
export function hasGuideForRoute(pathname: string): boolean {
  return GUIDE_ENTRIES.some((entry) => entry.match(pathname));
}
