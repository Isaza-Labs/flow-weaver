// Per-control help text — the ⓘ that sits next to an individual field,
// toggle, column or section, as opposed to the page-level ⓘ in PageHeader
// (whose text lives in ./index.ts).
//
// One FLAT registry keyed by a namespaced slug rather than a per-route map,
// because several of these controls live in shared components: ScheduleForm
// renders under four different routes, DevicePicker under three. A flat key
// is the same key wherever the component is mounted.
//
// Key convention: `<area>.<control>`, lower-case, dots for nesting. Keep the
// area aligned with the page it predominantly appears on so the file stays
// scannable.
//
// Both halves are checked by `npm run check:hints`, which fails on a key used
// in markup but not declared here (the ⓘ would silently render nothing) and
// on a key declared here but used nowhere (dead copy that drifts out of date).

export interface FieldHint {
  /** What the control is called in the UI. Shown as the popover heading. */
  label: string;
  /** One or two sentences: what it does, in the user's terms. */
  help: string;
  /**
   * The non-obvious part — a consequence, a gotcha, or the interaction with
   * another control. Omit rather than padding: a detail that only restates
   * `help` makes every hint feel like filler.
   */
  detail?: string;
}

export const FIELD_HINTS: Record<string, FieldHint> = {
  // -------------------------------------------------- Schema / transform tools
  'schema.required': {
    label: 'Required',
    help: 'Whether a caller must supply this field. A required field with no default blocks the run dialog until it is filled.',
  },
  'transform.input_data': {
    label: 'Input data',
    help: 'Sample JSON to try the expression against. It is scratch data for this playground and is never saved with the snippet.',
  },
  'transform.expression': {
    label: 'JMESPath expression',
    help: 'The expression evaluated against the input on the left. The result updates as you type, so you can shape it before committing.',
    detail: 'Only the expression is saved to the snippet — the sample input stays here.',
  },
  'transform.reference': {
    label: 'JMESPath quick reference',
    help: 'The operators and functions available, with a worked example for each.',
  },

  // ------------------------------------------------------------------ Dashboard
  'dashboard.add_shortcut': {
    label: 'Add a shortcut',
    help: 'Pins a destination to your quick links. The choice is yours alone and does not change what anyone else sees.',
  },
  'dashboard.quick_links': {
    label: 'Quick links',
    help: 'Your own shortcuts into the parts of the app you use most. Edit to add or remove them.',
  },
  'dashboard.job_queue': {
    label: 'Job queue',
    help: 'What is waiting to be picked up by a worker, by status. A queue that only grows means nothing is claiming jobs.',
  },
  'dashboard.recent_runs': {
    label: 'Recent runs',
    help: 'The last workflow executions across the system, newest first. Click one to open its detail.',
  },

  // ------------------------------------------------------------- Admin landing
  'admin.health_section': {
    label: 'Health',
    help: 'Live operational signals — queue depth, run activity and sign-in activity. Refreshes on its own every 30 seconds.',
  },
  'admin.management_section': {
    label: 'Management',
    help: 'The way into the audit trail, user accounts and global settings.',
  },
  'themes.name': {
    label: 'Name',
    help: 'What the theme is called in the picker. Yours must be unique among your own themes; a shared one must be unique across the org.',
  },
  'themes.description': {
    label: 'Description',
    help: 'One line under the name in the picker. Say what the theme is for — "high contrast for the NOC wall" beats "blue one".',
  },
  'themes.share': {
    label: 'Share with everyone',
    help: 'Publishes the theme into every user\'s picker. Admin-only, in both directions: unpublishing removes it from anyone who selected it, and they fall back to Editorial.',
  },
  'themes.start_from': {
    label: 'Start from',
    help: 'Loads a curated palette into the editor as a starting point — it only fills the form, nothing is saved. "Surprise me" rolls a random palette that keeps green/amber/red where status colours belong.',
  },
  'themes.style': {
    label: 'Style',
    help: 'Everything beyond colour: corner roundness, interface scale, fonts and heading weight. Anything left at its default simply inherits the app\'s standard look.',
    detail: 'Interface scale resizes the whole app (text and spacing together), like a persistent browser zoom that follows the theme.',
  },
  'themes.fonts': {
    label: 'Body font',
    help: 'The main interface typeface. A fixed set of stacks — the two bundled fonts plus system-safe families — so a shared theme can never pull in a remote font.',
  },
  'themes.font_heading': {
    label: 'Heading font',
    help: 'Titles only. "Same as body" keeps one typeface everywhere; a serif here over a sans body is the classic editorial pairing.',
  },
  'themes.heading_weight': {
    label: 'Heading weight',
    help: 'How heavy titles render, 400–900. The app default is 650; the Brutalist built-in runs at 800 for comparison.',
  },
  'themes.font_mono': {
    label: 'Code font',
    help: 'Used for code, device output and anything monospace. JetBrains Mono ships with the app; the system stack uses whatever the OS provides.',
  },
  'admin.slo_method': {
    label: 'How these are computed',
    help: 'What each objective measures and over which window, so a number can be reproduced rather than trusted.',
  },

  // ---------------------------------------------------- Run inspection views
  'runs.data_flow': {
    label: 'Data flow',
    help: 'How each step output fed the next, so you can see where a value came from rather than guessing.',
  },
  'runs.steps': {
    label: 'Steps',
    help: 'One row per node the run reached. Click any of them to expand its input, output and error in full.',
    detail: 'A step marked skipped never ran — usually a conditional edge that did not fire, which is a normal outcome and not a failure.',
  },
  'runs.monitor_steps': {
    label: 'Steps',
    help: 'The live view: steps appear and change state as the workers report back.',
  },

  // --------------------------------------------------------- Other raw controls
  'settings.granular_toggle': {
    label: 'Per-resource grants',
    help: 'Makes workflow and integration writes also require an Editor or Owner grant on that row. Capability grants are a separate setting (RBAC mode).',
    detail: 'Grant your operators Editor or Owner on each resource’s Permissions tab before enabling — otherwise their writes start failing with missing_editor_grant / missing_owner_grant.',
  },
  'users.is_active': {
    label: 'Active',
    help: 'Whether this account may sign in. Deactivating is the reversible way to remove someone; deleting is not.',
  },
  'workflows.new_name': {
    label: 'Name',
    help: 'What the workflow is called in the list, in run history and in the subflow picker.',
  },
  'workflows.new_description': {
    label: 'Description',
    help: 'What this workflow is for. Policy rules can match on the description text, so the wording is occasionally load-bearing.',
  },
  'workflows.search': {
    label: 'Search',
    help: 'Filters the list by name and description.',
  },
  'workflows.environment_filter': {
    label: 'Environment',
    help: 'Shows only workflows in one environment. The same workflow promoted forward exists as a separate row per environment.',
  },
  'workflows.import_file': {
    label: 'Workflow file',
    help: 'A FlowWeaver bundle (JSON), a FlowWeaver YAML/JSON export, or a foreign definition (n8n, Itential, generic DAG) to translate. Max 5 MiB.',
    detail: 'Choosing a file only parses it. Nothing is written until you commit the import at the end.',
  },
  'snippets.search': {
    label: 'Search',
    help: 'Filters snippets by name and description.',
  },
  'snippets.settings_section': {
    label: 'Settings',
    help: 'How this snippet behaves when a node runs it: fan-out mode, concurrency ceiling, timeout and retry semantics.',
  },
  'vendor_commands.new_section': {
    label: 'New command',
    help: 'Adds an entry to the catalog the SSH validator checks against, for one platform.',
  },
  'workflows.palette': {
    label: 'Palette',
    help: 'Every snippet you can drop on the canvas. Drag one onto the graph to add it as a node.',
  },
  'devices.select_all': {
    label: 'Select all',
    help: 'Selects every row currently visible. With a filter active that means the matches, not the whole inventory.',
  },

  // -------------------------------------------------- Intelligence (raw controls)
  'agents.tool_checkbox': {
    label: 'Tool',
    help: 'Ticking a tool lets this agent call it. Everything unticked is unreachable to the agent, whatever the conversation asks for.',
    detail: 'Tools listed but not in the catalog stay selectable and are filtered out at chat time — that is how an allowlist survives a deploy that renames something.',
  },
  'agents.enabled': {
    label: 'Enabled',
    help: 'Whether this agent can be selected at all. Disabling hides it from the chat picker without discarding its configuration.',
  },
  'providers.enabled': {
    label: 'Enabled',
    help: 'Whether agents may use this provider. Disabling it breaks every agent bound to it, so check what points here first.',
  },
  'skills.upload_file': {
    label: 'Upload .md',
    help: 'Loads a Markdown file from disk into the editor. Nothing is stored until you save.',
  },
  'skills.active': {
    label: 'Active',
    help: 'Whether this skill is prepended to conversation prompts. Inactive skills keep their content and contribute nothing.',
    detail: 'Applies to new conversations; one already in progress keeps the prompt it started with.',
  },
  'specs.upload_file': {
    label: 'Upload .yaml',
    help: 'Loads an OpenAPI document from disk into the editor. Nothing is stored until you save.',
  },
  'specs.active': {
    label: 'Active',
    help: 'Whether the agent is shown this spec when a conversation starts. Inactive specs stay stored and invisible.',
  },

  // --------------------------------------------------------- Enable / disable
  'policies.enabled': {
    label: 'Enabled',
    help: 'Whether this policy is evaluated. A disabled policy keeps its rule but blocks nothing.',
    detail: 'Disabling is how you retire a guardrail without losing the rule — deleting it means rewriting the JSON when it turns out to have been load-bearing.',
  },
  'permissions.enabled': {
    label: 'Enabled',
    help: 'Whether this grant confers anything. A disabled grant stays assigned to its users and gives them nothing.',
  },
  'channels.require_linked_user': {
    label: 'Require linked account',
    help: 'Nobody gets agent access through this channel until their platform identity is bound to a FlowWeaver account.',
    detail: 'This is what makes the permission model work over a chat channel: with it off, there is no user to check permissions against. Leave it on.',
  },
  'channels.allow_unsigned': {
    label: 'Allow unsigned deliveries',
    help: 'Accepts inbound webhooks that carry no signature.',
    detail: 'The signature is what proves the delivery came from the platform. Without it, anyone who learns the URL can post as the provider — testing only.',
  },
  'channels.linked_accounts': {
    label: 'Linked accounts',
    help: 'The external identities bound to FlowWeaver accounts. Each binding is what lets a message carry that user permissions.',
    detail: 'Unlinking revokes access immediately for that identity without touching the channel or anyone else on it.',
  },

  // ------------------------------------------------------- Snippet detail view
  'snippets.code_editor': {
    label: 'Code',
    help: 'The body executed inside the sandbox. Read the step payload, and print or return what downstream nodes should consume.',
    detail: 'Imports are limited to the modules an admin allowed under Python packages. An unlisted import fails at run time, not on save.',
  },
  'snippets.transform_expression': {
    label: 'Transform expression',
    help: 'Reshapes the incoming payload into the shape the next node expects, without a full script.',
  },
  'snippets.network_enabled': {
    label: 'Network-enabled · interactive SSH',
    help: 'Lifts the sandbox network isolation for this one snippet, so it can drive an interactive session with netmiko or paramiko.',
    detail: 'The isolation is what stops a snippet reaching anything you did not intend. Admin-only, per snippet, and worth turning back off when the interactive step is gone.',
  },
  'snippets.input_schema': {
    label: 'Input schema',
    help: 'Declares what this snippet expects to receive. It drives the form rendered for a node using it.',
  },
  'snippets.output_schema': {
    label: 'Output schema',
    help: 'Declares what this snippet returns, so downstream nodes can reference steps.<node>.output.<field> with something to go on.',
  },
  'snippets.retry_policy': {
    label: 'Retry policy',
    help: 'How many times a failed step retries and how long it waits between attempts, as JSON.',
    detail: 'Only failures that are safe to repeat are retried: an SSH or HTTP connection that could not be opened, a timeout or 5xx on a read, a 429 or 503. A write that may have reached the server is never repeated.',
  },
  'snippets.logic_diagram': {
    label: 'Logic diagram',
    help: 'A Mermaid diagram of what this snippet does internally, shown to anyone reading the snippet later.',
    detail: 'Required for code and transform snippets: those are the ones whose behaviour is not obvious from their configuration.',
  },

  // ---------------------------------------------- Integrations (raw controls)
  'integrations.action_method': {
    label: 'Method',
    help: 'The HTTP verb this action sends. GET and HEAD are safe to retry; POST, PUT, PATCH and DELETE change state on the far side.',
    detail: 'The verb also steers the rollback policy a node should declare — a POST that is not idempotent needs a failure edge, not a retry.',
  },
  'integrations.upload_spec': {
    label: 'Upload .yaml',
    help: 'Loads an OpenAPI document from disk into the editor below. Nothing is saved until you attach it.',
  },
  'integrations.upload_skill': {
    label: 'Upload .md',
    help: 'Loads a Markdown skill from disk into the editor below. Nothing is saved until you attach it.',
  },
  'integrations.bundle': {
    label: 'Skills and specs',
    help: 'Prompt skills and OpenAPI specs scoped to this integration. Scoping them here keeps them out of every unrelated conversation.',
    detail: 'A spec teaches the agent which operations exist; a skill teaches it the conventions for using them. Global ones under Intelligence apply everywhere instead.',
  },
  'integrations.actions_section': {
    label: 'Actions',
    help: 'The individual HTTP calls this integration exposes. A workflow node picks one action, not a whole integration.',
    detail: 'The import wizard creates these automatically from an OpenAPI spec, filed under the "imported" category.',
  },
  'integrations.edit_section': {
    label: 'Edit integration',
    help: 'Connection-level settings — where it points, how it authenticates, and which network guards are relaxed. Changes apply to every action underneath.',
  },
  'integrations.skill_files': {
    label: 'Markdown skills',
    help: 'One or more .md files attached to this integration when it is created. Each becomes a prompt skill scoped to it.',
  },
  'integrations.spec_files': {
    label: 'OpenAPI specs',
    help: 'One or more .yaml files attached at creation. Each becomes a spec the agent can read to discover this integration operations.',
    detail: 'Attaching a spec here is what materialises the action list — without one you have a connection with nothing to call.',
  },

  // ------------------------------------------------------ Git repository detail
  'git.files_section': {
    label: 'Files',
    help: 'Browse and edit the server-side working copy of this repository at the current branch.',
    detail: 'Edits are local to that working copy until you commit; committing is a separate, explicit step.',
  },
  'git.webhooks_section': {
    label: 'Inbound webhooks',
    help: 'Lets the remote notify this system on push, so a workflow can react to a commit instead of polling.',
  },
  'git.hook_auto_pull': {
    label: 'Auto-pull on push',
    help: 'Pulls the new commits into the working copy as soon as a push arrives, so the files you browse are current.',
  },
  'git.hook_enabled': {
    label: 'Enabled',
    help: 'Whether this webhook is accepted at all. Disabling stops deliveries being acted on without deleting the registration.',
  },
  'git.hook_allow_unsigned': {
    label: 'Allow unsigned',
    help: 'Accepts deliveries that carry no signature header.',
    detail: 'The signature is what proves a delivery really came from the remote. With this on, anyone who learns the URL can trigger it — testing emitters only.',
  },
  'git.commit_push': {
    label: 'Push after commit',
    help: 'Pushes the commit to the tracking branch straight away instead of leaving it local to the working copy.',
    detail: 'Leaving it off is the reversible choice: a local commit can still be amended or dropped.',
  },

  // --------------------------------------------------------------------- Sign in
  'login.username': {
    label: 'Username',
    help: 'The account name an admin created for you.',
  },
  'login.password': {
    label: 'Password',
    help: 'There is no self-service reset. If you are locked out, an admin has to set a new password for you.',
  },

  // --------------------------------------------------- Per-resource permissions
  'resource_perm.user': {
    label: 'User',
    help: 'Who to grant this level to on this one resource. It never changes their global role.',
  },
  'resource_perm.role': {
    label: 'Level',
    help: 'With per-resource grants enabled, editing needs editor or owner and deleting needs owner. Runner and viewer are recorded but not checked today.',
    detail: 'Only consulted while per-resource grants are enabled in Application settings; then an operator without a grant on this row cannot change it. Admins pass regardless.',
  },
  'permissions.add_user': {
    label: 'Add user',
    help: 'Assigns this grant to an account. A grant does nothing until at least one user holds it.',
  },

  // ------------------------------------------------------------- Schema editor
  'schema.field_name': {
    label: 'Field name',
    help: 'The key this value is read by. Input fields become {{ input.<name> }}; output fields become steps.<node>.output.<name>.',
  },
  'schema.field_type': {
    label: 'Type',
    help: 'The JSON type this field holds. It drives which form control the run dialog renders for it.',
  },
  'schema.item_type': {
    label: 'Item type',
    help: 'For an array field, the type of each element.',
  },
  'schema.field_description': {
    label: 'Description',
    help: 'Shown next to the field in the run dialog, and read by the authoring agent when it fills the schema in.',
  },
  'schema.enum': {
    label: 'Enum',
    help: 'Comma-separated list of the only accepted values. Leave empty for free text.',
    detail: 'Turning a free-text field into an enum renders it as a dropdown, which is the cheapest way to stop an operator typing an invalid value.',
  },
  'schema.default': {
    label: 'Default',
    help: 'A JSON literal used when the caller supplies nothing — text, 5, true, [1,2].',
  },
  'schema.raw_json': {
    label: 'Raw JSON Schema',
    help: 'The schema as a document, for anything the visual editor cannot express.',
  },

  // -------------------------------------------------------------- Snippet test
  'snippet_test.input': {
    label: 'Input (JSON)',
    help: 'The payload handed to the snippet for this trial run, as it would arrive from a real workflow.',
  },
  'snippet_test.timeout': {
    label: 'Timeout (seconds)',
    help: 'How long this trial run may take before it is cut off. Applies to the test only, not to the snippet saved configuration.',
  },

  // ------------------------------------------------------------- Action testing
  'action_test.body': {
    label: 'Body (JSON)',
    help: 'The request body sent by this trial call.',
    detail: 'This is a real call against the real endpoint — it is not a simulation. Do not test a destructive action against production.',
  },
  'action_test.path_params': {
    label: 'Path params (JSON)',
    help: 'Values substituted into the placeholders in the action path.',
  },
  'action_test.query': {
    label: 'Query (JSON)',
    help: 'Query-string parameters appended to the request.',
  },

  // ------------------------------------------------------------ File uploads
  'upload.name': {
    label: 'Name',
    help: 'The file name this entry is stored and matched under. Re-importing from disk overwrites an existing row with the same name.',
  },
  'upload.sort_order': {
    label: 'Sort order',
    help: 'The order entries are concatenated. Lower goes first — base.md conventionally uses 0.',
  },
  'upload.preview': {
    label: 'Preview',
    help: 'The file content as it will be stored. Read it before saving — this is the last point at which a bad paste is cheap to fix.',
  },

  // ------------------------------------------------------------- Miscellaneous
  'chat.agent': {
    label: 'Agent',
    help: 'Which configured agent answers in this conversation. Each brings its own tool allowlist and system prompt.',
    detail: 'Switching agents starts the next turn under the new one; the messages already exchanged stay as they are.',
  },
  'policies_audit.window': {
    label: 'Window',
    help: 'How far back to roll up policy denials. Events derive from traces, so the trace retention window is the hard limit.',
  },
  'providers.base_url': {
    label: 'Base URL',
    help: 'Where the provider is reached. Required for Ollama and any self-hosted endpoint; leave empty to use the vendor default.',
  },
  'providers.api_key': {
    label: 'API key',
    help: 'Encrypted server-side and never returned to the UI. Leave empty on an edit to keep the stored key.',
  },
  'integrations.private_network_reason': {
    label: 'Reason',
    help: 'Why the private-network block is being lifted or restored. Recorded with the change so the decision is auditable later.',
    detail: 'Required when enabling. This is the record someone reads when asking why an integration can reach inside the network.',
  },
  'node.query_param': {
    label: 'Parameter',
    help: 'A value passed to this call. Templates like {{ input.x }} or {{ steps.<node>.output.y }} are resolved at run time.',
  },
  'workflows.snippet_search': {
    label: 'Search snippets',
    help: 'Filters the palette by name and description. Drag a result onto the canvas to add it as a node.',
  },
  'import.dependency_target': {
    label: 'Target',
    help: 'Which existing snippet or integration this imported reference should map onto.',
  },

  // ------------------------------------------------------------------ AI agents
  'agents.name': {
    label: 'Name',
    help: 'How the agent is picked in the chat surface when more than one exists.',
  },
  'agents.role': {
    label: 'Role',
    help: 'The short identifier the chat controller binds to. The default assistant is the agent the chat surface and the messaging channels use when nothing else is chosen.',
  },
  'agents.description': {
    label: 'Description',
    help: 'What this agent is for. Shown in the agent list; it does not reach the model.',
  },
  'agents.provider': {
    label: 'Provider',
    help: 'Which configured LLM backend serves this agent. At least one enabled provider is required before chat works at all.',
  },
  'agents.model_override': {
    label: 'Model override',
    help: 'Pins this agent to a specific model instead of the provider default. Leave empty to follow the provider.',
  },
  'agents.extra_prompt': {
    label: 'Extra system prompt',
    help: 'Appended after the prompt skills, so skills always come first and this is your per-agent adjustment on top.',
    detail: 'Use skills for facts everyone should share and this box for how this particular agent should behave.',
  },
  'agents.tool_allowlist': {
    label: 'Tool allowlist',
    help: 'Exactly which tools this agent may call, one per line or comma-separated. Names not in the catalog are filtered out at chat time, so forward-looking entries are harmless.',
    detail: 'This is the cheapest safety control you have: narrowing it bounds what the agent can do at all. Permissions still apply on top — an agent can never exceed what its caller is allowed.',
  },
  'agents.max_iterations': {
    label: 'Max iterations',
    help: 'How many tool-call rounds the agent may take before the runner stops it. Guards against a loop that never converges.',
  },
  'agents.temperature': {
    label: 'Temperature',
    help: 'How much the model varies its output. Low values are more repeatable, which is usually what you want for anything that writes config.',
  },

  // --------------------------------------------------------------- AI providers
  'providers.name': {
    label: 'Name',
    help: 'Label for this backend in the provider list and in the agent picker.',
  },
  'providers.type': {
    label: 'Type',
    help: 'Which vendor API this provider speaks. It decides the request shape and which models are valid.',
  },
  'providers.default_model': {
    label: 'Default model',
    help: 'The model used by any agent that does not pin its own override.',
  },
  'providers.context_window': {
    label: 'Context window',
    help: 'How many tokens the model can take in one request. Sent to Ollama as num_ctx — without it Ollama runs the model at its default window (often 2k–4k) and silently drops the beginning of a prompt that does not fit, which is where the system prompt lives. Optional for the cloud vendors, where it only bounds the output cap.',
  },
  'providers.max_output_tokens': {
    label: 'Max output tokens',
    help: 'The longest answer the model may write in one turn. Leave empty for the vendor default: 8192 for Anthropic and Gemini, the model maximum for OpenAI, the model default for Ollama. An answer that hits this cap is marked as cut off in the chat.',
  },
  'providers.workspace_id': {
    label: 'Workspace ID',
    help: 'Which Anthropic workspace this provider bills and scopes its requests to. Required when the API key is identity-linked — one created from your own account rather than inside a workspace — because such a key can reach several workspaces and the API refuses to guess. A key created inside a workspace carries its own and needs none.',
    detail: 'It is the opaque wrkspc_… segment of the workspace URL in the Anthropic Console, not the workspace name. Get it wrong and every chat turn fails with "must be a valid workspace ID".',
  },

  // ------------------------------------------------------------- Prompt skills
  'skills.name': {
    label: 'Name',
    help: 'The skill file name, such as base.md. Re-importing from disk overwrites an existing skill matched by this name.',
  },
  'skills.sort_order': {
    label: 'Sort order',
    help: 'The order skills are concatenated into the system prompt. Lower goes first, so put general context before narrow overrides.',
  },
  'skills.integration': {
    label: 'Integration',
    help: 'Scopes the skill to one integration, or leave it global to apply everywhere. A global skill is in the prompt on every turn; an integration skill is only indexed there (one line) and its full text is loaded when the user names the integration, when a call goes to its API, or when the agent asks for it with load_skill — and it then stays loaded for that conversation. Prefer scoping a skill that documents one system: it costs nothing on turns about anything else.',
  },
  'skills.content': {
    label: 'Content (markdown)',
    help: 'Markdown prepended to every conversation system prompt. Placeholders like {{CurrentDate}} and {{ToolList}} are expanded when the conversation starts.',
    detail: 'Changes apply to new conversations. One already in progress keeps the prompt it started with.',
  },

  // ----------------------------------------------------------------- API specs
  'specs.api': {
    label: 'API identifier',
    help: 'The name the agent sees when choosing which API to call. Re-importing from disk matches existing specs on this value.',
  },
  'specs.integration': {
    label: 'Integration',
    help: 'Scopes the spec to one integration, or leave it global.',
  },
  'specs.content': {
    label: 'Content (OpenAPI 3.x YAML)',
    help: 'The OpenAPI document the agent reads to discover and execute operations.',
    detail: 'Reference stored secrets as ${secret:secret:<name>:value} rather than pasting credentials into the document.',
  },

  // ---------------------------------------------------------------------- Users
  'users.username': {
    label: 'Username',
    help: 'The login name. Must be unique, and it is what the person types on the sign-in screen.',
  },
  'users.email': {
    label: 'Email',
    help: 'Contact address for the account. Must be unique.',
  },
  'users.password': {
    label: 'Password',
    help: 'The initial password. There is no self-service reset flow, so an admin setting a new one here is the recovery path.',
  },
  'users.role': {
    label: 'Role',
    help: 'admin sees and does everything; operator can run and edit; viewer is read-only.',
    detail: 'admin bypasses every permission and every per-resource grant — but not policies, which apply to everyone. Grant it sparingly.',
  },

  // ------------------------------------------------------------- Admin settings
  'settings.rbac_mode': {
    label: 'RBAC mode',
    help: 'legacy keeps the flat admin / operator / viewer tiers. granular makes the permission grants under Govern → Permissions authoritative for capability checks.',
    detail: 'Switch only after confirming the grants already cover what your operators do — otherwise the change locks people out of work they were doing yesterday. Changes take up to a minute to propagate.',
  },
  'settings.import_fuzzy_threshold': {
    label: 'Fuzzy match threshold',
    help: 'The minimum similarity score an import candidate must reach before the wizard resolves it automatically. Higher means fewer automatic matches and more manual review.',
  },
  'settings.import_fuzzy_gap': {
    label: 'Fuzzy match gap',
    help: 'How far ahead the best candidate must be of the runner-up before it is accepted automatically. Guards against confidently picking between two equally plausible matches.',
  },

  // -------------------------------------------------------------------- Secrets
  'secrets.name': {
    label: 'Name',
    help: 'The name used to reference this secret from an OpenAPI spec, as ${secret:secret:<name>:value}.',
  },
  'secrets.description': {
    label: 'Description',
    help: 'What this secret opens and who issued it. The value itself is never shown again after saving.',
  },
  'secrets.value': {
    label: 'Value',
    help: 'Encrypted at rest and never returned to the UI. Leave empty on an edit to keep the stored value.',
  },

  // -------------------------------------------------------------------- Account
  'account.current_password': {
    label: 'Current password',
    help: 'Confirms it is really you before the change is accepted.',
  },
  'account.new_password': {
    label: 'New password',
    help: 'The password you will sign in with from now on.',
    detail: 'Changing it does not sign out your other sessions.',
  },
  'account.confirm_password': {
    label: 'Confirm new password',
    help: 'Typed again to catch a mistake before you are locked out by it.',
  },

  // -------------------------------------------------------- Admin observability
  'slo.window': {
    label: 'Window (days)',
    help: 'How far back the objectives are aggregated over. A short window reacts quickly; a long one smooths out a single bad afternoon.',
  },
  'audit.entity_type': {
    label: 'Entity type',
    help: 'Narrows the trail to one kind of record — workflow, device, credential and so on.',
  },
  'audit.action': {
    label: 'Action',
    help: 'Narrows to one kind of mutation: created, updated, deleted.',
  },
  'audit.event': {
    label: 'Event',
    help: 'Narrows the authentication side of the trail — sign-ins, lockouts, role changes.',
  },
  'audit.entity_id': {
    label: 'Entity id',
    help: 'Shows the whole history of one record — every change to a single workflow, credential or device. Paste the id from a row you are already looking at.',
  },
  'audit.user_id': {
    label: 'User id',
    help: 'Shows only what one account did. Paste the id from a row you are already looking at.',
  },
  'audit.actor': {
    label: 'Actor',
    help: 'Who made the change, by name. A username for a signed-in person, or the automation identity for unattended changes — workflow-runner, git-webhook, workflow-webhook, messaging-send. Those have no user id, so this is the only way to filter them.',
  },
  'audit.date_range': {
    label: 'Date range',
    help: 'Bounds the window being queried. Narrow it before widening the other filters — the trail is append-only and grows without limit.',
  },
  'traces.category': {
    label: 'Category',
    help: 'Which subsystem emitted the trace.',
  },
  'traces.status': {
    label: 'Status',
    help: 'Filters by how the action ended. A row still marked started, several seconds old, means the handler is either still running or died without closing its trace.',
  },
  'traces.search': {
    label: 'Search',
    help: 'One term matched anywhere in the action, the category, the error message, the request id or the metadata JSON. Substring, not a prefix — searching timeout finds ai.chat.timeout, and pasting a request id here reconstructs that call.',
  },
  'traces.user': {
    label: 'User',
    help: 'Who set the action off, by user id, username or email — whichever you happen to be holding. Blank on rows with no signed-in user behind them: the seeder, the retention sweeper, anything the worker started on its own.',
  },
  'traces.min_duration': {
    label: 'Slower than',
    help: 'Only rows that finished and took at least this many milliseconds, and the one filter that also changes the order: results come back slowest first, because the newest rows over a wide window are not the slow ones you asked for. Rows still marked started have no duration yet and never match.',
  },
  'traces.window': {
    label: 'Window',
    help: 'Bounds every query. The table grows on every request, so narrow this before widening anything else; Custom range hands over to the two date pickers.',
  },
  'artifacts.search_title': {
    label: 'Search title',
    help: 'Substring match on the document title.',
  },
  'artifacts.format': {
    label: 'Format',
    help: 'Filters by the output format the document was generated in.',
  },
  'artifacts.user': {
    label: 'User',
    help: 'Shows only documents generated by one account. Exports always have a user; workflow reports may not.',
  },
  // ------------------------------------------------------------------- Policies
  'policies.name': {
    label: 'Name',
    help: 'Identifies the policy in the audit trail. When a rule blocks something, this is the name the operator is shown as the reason.',
  },
  'policies.description': {
    label: 'Description',
    help: 'Why the guardrail exists. Worth writing for whoever hits the block a year from now and has to decide whether it still applies.',
  },
  'policies.rule': {
    label: 'Rule (JSON)',
    help: 'A deny rule blocks matching operations; a gate requires historical evidence before a promotion proceeds. Conditions under "when" are AND-joined and all optional.',
    detail: 'An empty "when" matches everything. Available conditions: env, snippet_type, description_contains, action and ssh_command_regex — the last is checked per command inside the SSH handler.',
  },

  // ---------------------------------------------------------------- Permissions
  'permissions.name': {
    label: 'Name',
    help: 'How this grant appears when assigning it to users. Name it after the job it enables — "qa-runner", "read-only-auditor".',
  },
  'permissions.description': {
    label: 'Description',
    help: 'What this grant is for and who should hold it.',
  },
  'permissions.conditions': {
    label: 'Conditions (JSON)',
    help: 'Restricts the capabilities to a context: an environment, specific devices or device roles, or one named resource. Leave empty for an unconditional grant.',
    detail: 'These are allow grants, so a conditioned grant never applies to a call that supplies no context — it can only narrow, never leak into an unscoped check.',
  },

  // ----------------------------------------------------------- Vendor commands
  'vendor_commands.device_type_filter': {
    label: 'Filter by device_type',
    help: 'Narrows the catalog to one platform. The same command verb can be valid on one OS and unknown on another, so the catalog is per platform.',
  },
  'vendor_commands.device_type': {
    label: 'device_type',
    help: 'The platform this entry applies to — the same identifier used as a device Platform, such as cisco_ios or nokia_srl.',
  },
  'vendor_commands.kind': {
    label: 'kind',
    help: 'exact matches one literal command; pattern matches a case-insensitive regular expression, which is how you cover a whole family of commands in one entry.',
  },
  'vendor_commands.value': {
    label: 'Command or pattern',
    help: 'The literal command for an exact entry, or the regex for a pattern entry.',
    detail: 'Anything not matched by the catalog surfaces as a warning when a workflow is saved. Warnings never block a save or a run — they exist so the authoring agent can fix typos before they reach a device.',
  },
  'vendor_commands.new_device_type': {
    label: 'New device_type',
    help: 'Registers a platform the catalog does not know yet, so commands can be recorded against it.',
    detail: 'Use the same identifier devices carry as their Platform — the validator matches on it exactly, so paloalto_panos and panos are two different platforms.',
  },
  'ping.count': {
    label: 'Count',
    help: 'How many echo requests to send per device. More samples give a better read on intermittent loss at the cost of a slower step.',
  },
  'vendor_commands.notes': {
    label: 'Notes',
    help: 'Free-text context — what the command does, or why the pattern is written the way it is.',
  },

  // -------------------------------------------------------------- MCP servers
  'mcp.name': {
    label: 'Name',
    help: 'How this server appears when a chat agent or an mcp_call node picks a tool.',
  },
  'mcp.url': {
    label: 'Server URL',
    help: 'The Model Context Protocol endpoint to connect to. Test & sync uses it to discover the tool catalog.',
  },
  'mcp.auth': {
    label: 'Authentication',
    help: 'How to authenticate against the server: none, a header, basic credentials, or OAuth.',
    detail: 'A 401, or a 403 whose body names a credential problem, means re-authorising. A 403 about the host not being allowed is a reachability problem instead — the server is refusing where the request came from, not who sent it.',
  },
  'mcp.header_name': {
    label: 'Header name',
    help: 'Which header carries the credential, when authenticating by header.',
  },
  'mcp.username': {
    label: 'Username',
    help: 'The user half of basic authentication against the MCP server.',
  },
  'mcp.secret_headers': {
    label: 'Secret headers (JSON object)',
    help: 'Extra headers sent on every request, as a JSON object. Values are encrypted at rest and never returned to the UI.',
  },
  'mcp.client_id': {
    label: 'Client ID',
    help: 'The OAuth client identifier registered with the MCP server.',
  },
  'mcp.token_endpoint': {
    label: 'Token endpoint',
    help: 'Where the OAuth access token is requested from.',
  },
  'mcp.authorization_endpoint': {
    label: 'Authorization endpoint',
    help: 'Where the operator is sent to approve access during the OAuth flow.',
  },
  'mcp.redirect_uri': {
    label: 'Redirect URI',
    help: 'Where the provider returns after approval. It must match exactly what is registered on the provider side.',
  },
  'mcp.scopes': {
    label: 'Scopes',
    help: 'Which permissions to request during the OAuth flow. Ask for the narrowest set the tools actually need.',
  },
  'mcp.enabled': {
    label: 'Enabled',
    help: 'Whether agents and workflow nodes may call this server. Disabling hides its tools without discarding the registration or its credentials.',
  },
  'mcp.tls_skip_verify': {
    label: 'Skip TLS verification',
    help: 'Accepts any certificate the server presents, valid or not.',
    detail: 'This removes the protection against someone impersonating the server. Lab boxes with self-signed certs only.',
  },
  'mcp.allow_private_network': {
    label: 'Allow private network',
    help: 'Permits connections to private ranges and loopback, which are blocked by default.',
    detail: 'The block is what stops a registered server being used to reach internal services from the backend. Enable it only for a server you deliberately host inside.',
  },

  // ------------------------------------------------------- Messaging channels
  'channels.provider': {
    label: 'Provider',
    help: 'Which messaging platform this channel connects: Slack, Teams, WhatsApp or Telegram. Fixed once created, since the credentials are provider-specific.',
  },
  'channels.name': {
    label: 'Name',
    help: 'How this channel is identified in the list. Name it after the workspace or the audience — "ops-telegram", "noc-slack".',
  },
  'channels.bot_token': {
    label: 'Bot token',
    help: 'The outbound credential used to post messages back to the platform. Encrypted at rest and never returned to the UI.',
    detail:
      'Slack: the xoxb- bot token. Telegram: the @BotFather token. WhatsApp: the Cloud API access token. Teams: the Entra client secret of the bot\'s app registration. On an edit, leaving it blank keeps the token already stored.',
  },
  'channels.signing_secret': {
    label: 'Signing secret',
    help: 'Verifies that inbound webhooks really came from the platform. Without it, anyone who learns the webhook URL can impersonate the provider.',
    detail:
      'Teams does not use it: Bot Framework signs every activity with a JWT that is validated against Microsoft\'s public keys, so the field is hidden for that provider.',
  },
  'channels.app_token': {
    label: 'No-ingress credential',
    help: 'Optional. Lets the backend dial out and receive messages over that connection, so no publicly reachable webhook URL is needed — the answer for a VPN-only or NAT-ed deployment.',
    detail:
      'Slack: the App-Level Token (xapp-…, scope connections:write) that enables Socket Mode. Teams: an Azure Relay Hybrid Connection string — Teams has no Socket Mode, so the Relay stands in for one and the Azure Bot\'s messaging endpoint becomes the Relay URL. Telegram and WhatsApp are webhook-only.',
  },
  'channels.external_config': {
    label: 'External config (JSON)',
    help: 'Provider-specific settings that do not fit the fields above, as a JSON object.',
    detail:
      'Teams requires app_id (the Azure Bot\'s Microsoft App ID) and takes an optional tenant_id for single-tenant bots. WhatsApp takes phone_number_id and verify_token. Slack and Telegram need nothing here.',
  },
  'channels.max_role': {
    label: 'Max role (ceiling)',
    help: 'The highest privilege anything arriving through this channel may exercise, whatever the linked user normally holds.',
    detail: 'It is a ceiling, never a grant: a viewer talking to the bot stays a viewer. Setting it to operator does not promote anybody — it only caps admins down.',
  },
  'channels.allowed_external_ids': {
    label: 'Allowed external ids',
    help: 'Comma-separated list of platform user ids permitted to talk to the bot. Anyone not listed gets no access at all.',
  },

  // ------------------------------------------------------------ Email channels
  'email.provider': {
    label: 'Provider',
    help: 'Which SMTP service delivers the mail. Picking one fills in host, port and encryption; every field stays editable.',
    detail:
      'Custom SMTP is the escape hatch for an in-house relay (Postfix, Exchange on-prem). The provider only seeds the defaults — what gets stored and used is the host/port/security you end up with.',
  },
  'email.name': {
    label: 'Name',
    help: 'How this relay is identified in the list and in a workflow node. Name it after the mailbox or the audience — "alerts-gmail", "noc-relay".',
  },
  'email.host': {
    label: 'SMTP host',
    help: 'Hostname of the mail server. Filled from the provider preset; change it only if your account uses a different endpoint.',
    detail: 'Amazon SES is region-specific — the preset points at us-east-1, so repoint it if your identity lives elsewhere.',
  },
  'email.port': {
    label: 'Port',
    help: 'Must match the encryption mode: 587 for STARTTLS, 465 for implicit TLS, 25 for an unencrypted internal relay.',
  },
  'email.security': {
    label: 'Encryption',
    help: 'STARTTLS upgrades a plain connection and is what nearly every provider expects. SSL/TLS connects encrypted from the first byte.',
    detail: 'None sends everything, credentials included, in the clear. It is refused with a username unless the channel is also marked as reaching a private network.',
  },
  'email.username': {
    label: 'Username',
    help: 'The SMTP AUTH user. Usually the full sending address; SendGrid always uses the literal "apikey". Leave empty for an unauthenticated internal relay.',
  },
  'email.password': {
    label: 'Password',
    help: 'The SMTP AUTH secret. Encrypted at rest and never returned to the UI — on an edit, leaving it blank keeps the stored one.',
    detail:
      'Gmail needs a 16-character App Password, not the account password. SendGrid takes an API key with Mail Send. Amazon SES takes the SMTP password derived from an IAM credential.',
  },
  'email.from_address': {
    label: 'From address',
    help: 'The sender every message from this channel carries. Providers reject a From they do not recognise as yours, so it must be a verified identity.',
  },
  'email.from_name': {
    label: 'From name',
    help: 'Optional display name shown next to the address — "FlowWeaver Alerts".',
  },
  'email.reply_to': {
    label: 'Reply-To',
    help: 'Optional address replies should go to when it differs from the sender — useful when the sender is a no-reply mailbox.',
  },
  'email.test_to': {
    label: 'To',
    help: 'Where the test message goes. This sends a REAL email through the relay — it is not a dry run, so use an address you can actually check.',
    detail: 'The point of the test is to exercise the whole path: DNS, TLS, and the credential. A delivery that fails here fails the same way for an email_send step, and the error comes back verbatim.',
  },
  'email.test_subject': {
    label: 'Subject',
    help: 'Optional. Left empty, the test sends its own subject line.',
  },
  'email.test_body': {
    label: 'Body',
    help: 'Optional. Left empty, the test sends a default body naming the channel and the time.',
    detail: 'Worth filling in when you are checking how the message renders at the far end — a spam filter or a mail client can treat a one-line body differently from a real one.',
  },
  'email.is_default': {
    label: 'Default channel',
    help: 'The relay an email_send step uses when it does not name one. Only one channel can hold it; marking a new one demotes the previous.',
  },
  'email.allow_private_network': {
    label: 'Allow private network',
    help: 'Permits connecting to an SMTP server on a private range, which is blocked by default.',
    detail: 'The block is what stops a configured relay being used to probe internal services from the backend. Loopback and the cloud metadata IP stay blocked either way.',
  },
  'email.tls_skip_verify': {
    label: 'Skip TLS verification',
    help: 'Accepts a certificate that does not validate. Only ever appropriate for an internal relay with a self-signed certificate.',
    detail: 'On a public provider this removes the guarantee that you are talking to the real server — the credential can be captured by anyone able to intercept the connection.',
  },

  // ------------------------------------------------------------ Python packages
  'pypackages.import_name': {
    label: 'Import name',
    help: 'Exactly what the script writes after "import". Top-level module only — "yaml", not "yaml.loader".',
  },
  'pypackages.source': {
    label: 'Source',
    help: 'stdlib means the module already ships with the interpreter and only needs allowing. pip means the worker has to install it first.',
  },
  'pypackages.pip_spec': {
    label: 'PyPI package',
    help: 'The pip requirement to install, when it differs from the import name — import bs4 installs beautifulsoup4. Also where you pin a version.',
    detail: 'Installation happens on the worker, so a newly added package becomes usable once that finishes, not the moment you save.',
  },

  // ------------------------------------------------------------ Workflow editor
  'node.name': {
    label: 'Node name',
    help: 'The label on the canvas and the key other nodes use to read this one output, as steps.<name>.output.',
    detail: 'Renaming a node breaks every template that referenced the old name — the reference resolves to nothing rather than erroring, so check the nodes downstream.',
  },
  'node.type': {
    label: 'Node type',
    help: 'Which handler runs this step: ssh, python_snippet, integration_action, mcp_call, subflow and so on.',
  },
  'node.description': {
    label: 'Description',
    help: 'What this step is for. Shown on the node and read by the authoring agent when it edits the graph.',
  },
  'node.code': {
    label: 'Code',
    help: 'The script body executed inside the sandbox. Read step inputs from the payload and print or return the result other nodes will consume.',
    detail: 'Imports are limited to the modules an admin allowed under Python packages; an unlisted import fails at run time, not on save.',
  },
  'node.target_mode': {
    label: 'Target mode',
    help: 'per_device fans this node out to one step per target device; once runs it a single time with no particular device attached.',
    detail: 'In per_device the device is available to templates as {{ device.* }}. In once mode with several targets there is no unambiguous device, so that context is empty.',
  },
  'node.timeout': {
    label: 'Timeout (seconds)',
    help: 'How long one step of this node may run before the worker force-fails it. Bounds a single step, not the whole fan-out.',
  },
  'node.max_parallel': {
    label: 'Max parallel',
    help: 'Ceiling on simultaneous executions when this node fans out over devices.',
    detail: 'Worth lowering for anything that writes config or authenticates against a shared system.',
  },
  'node.script_language': {
    label: 'Script language',
    help: 'Whether the body above runs as Python or as Bash in the sandbox.',
  },
  'node.rollback_policy': {
    label: 'Rollback policy override',
    help: 'Whether this step is safe to retry. Idempotent means rollback always passes; requires compensation means a failure needs an explicit failure edge to undo it.',
    detail: 'Ignored when the handler itself declares something stricter — the override can tighten, not loosen.',
  },
  'node.retry_policy': {
    label: 'Retry policy',
    help: 'Raw JSON describing how many times to retry this step and how long to wait between attempts. Leave empty for no retries.',
    detail: 'Only failures that are safe to repeat are retried: an SSH or HTTP connection that could not be opened, a timeout or 5xx on a read, a 429 or 503. A write that may have reached the server is never repeated.',
  },
  'node.subflow': {
    label: 'Subflow',
    help: 'Which workflow this node invokes as a child run. Only workflows tagged as subflows appear here.',
    detail: 'The child inherits this run target devices. Transitive cycles are detected at enqueue time, so a subflow cannot end up invoking itself.',
  },
  'node.mcp_server': {
    label: 'MCP server',
    help: 'Which registered Model Context Protocol server this node calls. Registered by an admin under MCP servers.',
  },
  'node.mcp_tool': {
    label: 'Tool',
    help: 'Which tool on that server to invoke. The list comes from the tool catalog discovered when the server was last synced.',
    detail: 'Calling it still needs the mcp.execute capability for that server and tool — an unauthorised call fails at validation, before the run starts.',
  },
  'edges.condition': {
    label: 'Condition expression',
    help: 'The edge only fires when this evaluates true. Read upstream results as steps.<node>.output.<field>.',
    detail: 'A node whose incoming edges all evaluate false never activates, and the run finishes with that branch skipped — which is a success, not a failure.',
  },

  // --------------------------------------------------------------------- Import
  'import.format_hint': {
    label: 'Format hint',
    help: 'Tells the parser which dialect the uploaded file is — FlowWeaver v1, n8n, Itential, or a generic DAG. Leave it on auto unless detection guesses wrong.',
    detail: 'Nothing is written during parsing, so a wrong guess costs only a retry with the hint set.',
  },
  'import.conflict_resolution': {
    label: 'Resolution',
    help: 'What to do when a workflow of the same name already exists: rename this import, take a timestamped fresh copy, replace the existing one, or cancel.',
    detail: 'Replace deactivates a workflow other people may be running and needs an owner grant on it, the same as a delete. Rename is the reversible choice.',
  },
  'import.new_name': {
    label: 'New name',
    help: 'The name the imported workflow will take instead of the colliding one.',
  },
  'import.snippet_action': {
    label: 'Action for this snippet',
    help: 'What to do about a snippet the import references but this system does not have: create an empty stub to fill in later, or map it onto an existing snippet.',
    detail: 'A stub imports cleanly but fails at run time until someone writes its body — the workflow is complete on the canvas and hollow underneath.',
  },
  'import.integration_action': {
    label: 'Action for this integration',
    help: 'Create the integration in needs_config state, or map the reference onto an integration that already exists here.',
    detail: 'A needs_config integration blocks the run at enqueue time rather than failing mid-flight, so an unfinished import cannot quietly half-run.',
  },
  'import.target_integration': {
    label: 'Target integration',
    help: 'Which existing integration this imported reference maps onto. Candidates are ranked by name similarity, shown as a percentage.',
    detail: 'A low similarity score is worth checking by hand — the matcher compares names, not endpoints, so two unrelated APIs can score well.',
  },
  'import.agent_instructions': {
    label: 'Instructions for the agent',
    help: 'Free-text steer for the translation — naming conventions, which nodes to collapse, what to ignore. Optional.',
  },

  // --------------------------------------------------------------- Integrations
  'integrations.name': {
    label: 'Name',
    help: 'How the integration is picked when a workflow node calls one of its actions.',
  },
  'integrations.base_url': {
    label: 'Base URL',
    help: 'The scheme and host every action path is appended to. Set it once here rather than repeating it in each action.',
  },
  'integrations.auth_method': {
    label: 'Auth method',
    help: 'How requests authenticate. token sends "Authorization: Token <value>"; bearer sends "Authorization: Bearer <value>"; basic sends username and password; api_key sends a key header; oauth2_client_credentials obtains a Bearer token from a token endpoint at call time.',
    detail: 'The distinction between token and bearer matters more than it looks — Slack and most OAuth-style APIs need bearer, and token silently fails against them. Pick oauth2_client_credentials when the API hands out short-lived tokens from an identity provider instead of a static key.',
  },
  'integrations.auth_token': {
    label: 'Token',
    help: 'The secret sent on every request. Encrypted server-side and never returned to the UI.',
  },
  'integrations.auth_username': {
    label: 'Username',
    help: 'The user half of HTTP basic authentication.',
  },
  'integrations.auth_password': {
    label: 'Password',
    help: 'The secret half of HTTP basic authentication. Encrypted server-side and never returned to the UI.',
  },
  'integrations.auth_api_key': {
    label: 'API key',
    help: 'The key sent on every request for api_key authentication. Encrypted server-side and never returned to the UI.',
  },
  'integrations.auth_token_url': {
    label: 'Token URL',
    help: 'The OAuth2 token endpoint the backend calls with grant_type=client_credentials to obtain an access token. Subject to the same SSRF guard as every integration URL.',
  },
  'integrations.auth_client_id': {
    label: 'Client ID',
    help: 'The OAuth2 client identifier registered with your identity provider.',
  },
  'integrations.auth_client_secret': {
    label: 'Client secret',
    help: 'The OAuth2 client secret. Stored server-side and never returned to the UI. Access tokens obtained with it are cached in memory only.',
  },
  'integrations.auth_scope': {
    label: 'Scope',
    help: 'Optional space-separated scopes sent with the token request (e.g. "read write"). Leave empty to use the provider default.',
  },
  'integrations.health_path': {
    label: 'Health check path',
    help: 'Optional path (relative to the base URL) the health check probes in strict mode: exactly this path, exactly the expected status. Point it at a real authenticated endpoint so an invalid token turns the integration unhealthy.',
    detail: 'Empty = lenient mode: reachability plus best-effort credential verification. APIs whose root answers everyone (or rejects everyone, like Action1\'s /api/3.0/) can only be conclusively checked with an explicit path.',
  },
  'integrations.health_expected': {
    label: 'Expected status',
    help: 'HTTP status the strict health probe must receive from the health check path. Defaults to 200 when left empty.',
  },
  'integrations.tls_skip_verify': {
    label: 'Skip TLS verification',
    help: 'Accepts any certificate the endpoint presents, valid or not.',
    detail: 'This turns off the protection against someone impersonating the endpoint. Reasonable for a lab box with a self-signed cert; not for anything reachable off your network.',
  },
  'integrations.allow_private_network': {
    label: 'Allow private network',
    help: 'Permits this integration to call addresses inside private ranges and localhost, which are blocked by default.',
    detail: 'The block exists to stop a workflow being used to probe internal services from the server. Enable it only for an endpoint you deliberately host internally.',
  },
  'integrations.action_name': {
    label: 'Action name',
    help: 'How this operation appears in the workflow node picker. One action is one HTTP call.',
  },
  'integrations.action_path': {
    label: 'Path',
    help: 'Appended to the integration base URL — "/v1/notify", not the full address. Placeholders in the path are filled from node config at run time.',
  },
  'integrations.action_description': {
    label: 'Description',
    help: 'What the call does. The authoring agent reads this when choosing an action, so vague wording produces worse generated workflows.',
  },
  'integrations.action_category': {
    label: 'Category',
    help: 'Free-text grouping used to keep a long action list navigable. "imported" is what the import wizard assigns.',
  },
  'integrations.action_enabled': {
    label: 'Enabled',
    help: 'Whether this action shows up in the workflow node picker. Disabling hides it from new workflows without deleting it.',
    detail: 'Nodes that already reference a disabled action keep working — this controls discovery, not execution.',
  },

  // ------------------------------------------------------------- Git repositories
  'git.name': {
    label: 'Name',
    help: 'Label for this remote in the repository list and in webhook configuration.',
  },
  'git.url': {
    label: 'URL',
    help: 'The remote to clone. An https:// URL authenticates with a token credential; a git@ or ssh:// URL authenticates with an SSH key credential.',
    detail: 'The URL scheme decides which kind of credential works — pairing an https URL with an SSH key fails at clone time with an auth error.',
  },
  'git.default_branch': {
    label: 'Default branch',
    help: 'The branch checked out after cloning and the one operations assume when none is given.',
  },
  'git.credential': {
    label: 'Auth credential',
    help: 'Which stored credential authenticates against this remote. Comes from the Credentials page.',
    detail: 'For HTTPS with a personal access token, put the account name in the credential username and the token in its password field.',
  },
  'git.description': {
    label: 'Description',
    help: 'Free-text note about what this repository holds. Optional.',
  },

  // ------------------------------------------------------------------- Snippets
  'snippets.name': {
    label: 'Name',
    help: 'How this snippet appears in the workflow editor palette. Name it after what it does to a device, not after the workflow that first needed it.',
  },
  'snippets.description': {
    label: 'Description',
    help: 'Free-text note shown on the snippet card. The authoring agent also reads it when deciding which snippet fits a request, so a precise description makes generated workflows better.',
  },
  'snippets.type': {
    label: 'Type',
    help: 'Which handler executes this snippet: ssh, python_snippet, integration_action, mcp_call, ping and so on. It decides what config the node accepts and what the worker does with it.',
    detail: 'The type is fixed once the snippet exists — a node referencing it would otherwise change meaning underneath every workflow that uses it.',
  },
  'snippets.target_mode': {
    label: 'Target mode',
    help: 'per_device runs one step per target device and fans out; once runs a single step with no particular device attached.',
    detail: 'A per_device node in a run with no resolvable targets collapses to a single device-less step — which is why a run whose whole selection is blocked is refused instead.',
  },
  'snippets.max_parallel': {
    label: 'Max parallel',
    help: 'Upper bound on how many devices this snippet is executed against at the same time when it fans out.',
    detail: 'Lower it for anything that writes config or hits a shared system; a fan-out of 200 SSH sessions is a good way to trip a TACACS server.',
  },
  'snippets.timeout': {
    label: 'Timeout (s)',
    help: 'How long one step of this snippet may run before the worker force-fails it.',
    detail: 'It bounds a single step, not the whole run. A per_device node that fans out to 50 devices can still take far longer than this in total.',
  },
  'snippets.script_language': {
    label: 'Script language',
    help: 'Whether the snippet body is executed as Python or as Bash inside the sandbox.',
    detail: 'Python scripts may only import modules an admin allowed under Python packages; an unlisted import fails at run time, not at save time.',
  },
  'snippets.rollback_policy': {
    label: 'Rollback policy override',
    help: 'Declares whether this snippet is safe to retry. Idempotent means a rollback always passes; requires compensation means a failure needs an explicit failure edge to undo the change.',
    detail: 'Leave it inheriting the handler default unless you know better. The override is ignored when the handler itself is stricter.',
  },

  // ------------------------------------------------------------------ Workflows
  'workflows.name': {
    label: 'Name',
    help: 'Shown in the workflow list, in run history and in the subflow picker. Renaming is safe — everything references the workflow by id.',
  },
  'workflows.description': {
    label: 'Description',
    help: 'Context for whoever opens this next. Policy rules can match on the description text, so wording it carefully is occasionally load-bearing.',
  },
  'workflows.promote_summary': {
    label: 'Change summary',
    help: 'What is shipping and why. Stored on the immutable version snapshot taken at promotion, so it is the record anyone reads later when asking what changed.',
  },
  'workflows.promote_approved_by': {
    label: 'Approved by',
    help: 'The second signature required to promote into production. It must be someone other than whoever is running the promotion.',
    detail: 'Production also needs a completed qa run of this workflow within the last 48 hours — the backend rejects the promotion otherwise, and the dialog shows why.',
  },
  'subflows.environment_filter': {
    label: 'Environment',
    help: 'Limits the list to subflows sitting in one environment. A subflow is just a workflow tagged as reusable, so it moves through draft, qa and production like any other.',
    detail: 'A subflow node invokes a specific workflow id, so promoting the parent does not automatically promote the child.',
  },

  // ----------------------------------------------------------------------- Runs
  'runs.status_filter': {
    label: 'Status filter',
    help: 'Narrows the list to one run state. Applied by the backend, so it filters the whole history rather than just the page you are looking at.',
    detail: 'Skipped means the run finished but a node never activated — usually a conditional edge that did not fire, not an error.',
  },

  // ------------------------------------------------------------------- Run a workflow
  'workflow.run.inputs': {
    label: 'Runtime inputs',
    help: 'Values handed to this run, filling the {{ input.* }} placeholders in node config. The form is built from the input schema the workflow declares.',
    detail: 'A run always uses the last saved version of the workflow — unsaved canvas edits are not included, and the dialog warns you when there are any.',
  },
  'workflow.run.targets': {
    label: 'Target devices',
    help: 'The devices this run executes against. Nodes in per-device mode fan out over them, one step per device.',
    detail: 'Devices that do not allow the environment this workflow sits in are greyed out here; if the whole selection is blocked the run is refused rather than executing against nothing.',
  },
  // -------------------------------------------------------------------- Devices
  'devices.name': {
    label: 'Name',
    help: 'The display name used everywhere this device is referenced — pickers, run logs, per-device step results. Usually the hostname.',
    detail: 'Available to snippets as {{ device.name }}, so renaming it changes what commands built from templates actually send.',
  },
  'devices.ip': {
    label: 'IP address',
    help: 'Where connections are made. The SSH, ping and Ansible handlers all fall back to this when the node config does not override the host.',
    detail: 'A node that sets input.host bypasses this entirely — and bypasses the device record with it, including its credential.',
  },
  'devices.host_key_fingerprint': {
    label: 'SSH host key fingerprint',
    help: 'Pins this device\'s SSH host key. Once set, an ssh step refuses to connect if the device presents a different key — that is what stops someone on the network path impersonating it and collecting the credential the step was about to send.',
    detail: 'You rarely fill this in by hand: a device with no pin gets one from its first successful connect (trust-on-first-use), so the window where any key is accepted is that one run. To set it deliberately, paste the `ssh-keygen -lf` output or the host_key_fingerprint an ssh step reports, with or without the SHA256: prefix. After a legitimate key rotation the step starts failing with a mismatch — that is the control working: clear the field and the next connect re-pins.',
  },
  'devices.platform': {
    label: 'Platform',
    help: 'The Netmiko driver identifier: cisco_ios, arista_eos, juniper_junos, nokia_srl and so on. It selects how the SSH handler talks to the box.',
    detail: 'Left empty, the handler falls back to a generic driver and logs a warning. Generic works for simple shells but mangles prompts on real network OSes.',
  },
  'devices.vendor': {
    label: 'Vendor',
    help: 'Free-text vendor for display and filtering. Unlike Platform it drives no behaviour at connect time.',
    detail: 'The SSH command validator does use vendor/platform to decide which command catalog to check against, so keeping it accurate improves those warnings.',
  },
  'devices.site': {
    label: 'Site',
    help: 'Free-text location. Used for filtering and grouping only — a rack, a data centre, a city, whatever your inventory means by it.',
  },
  'devices.role': {
    label: 'Role',
    help: 'Free-text function: core, edge, spine, leaf, firewall. Used for filtering and for policy rules that match on device role.',
  },
  'devices.credential': {
    label: 'Credential',
    help: 'The stored username/password or SSH key used to authenticate to this device. Options come from the Credentials page.',
    detail: 'A device with no credential fails its SSH steps at run time with "credential 00000000-… not found" — not at save time. The banner above the table counts these.',
  },
  'devices.environments': {
    label: 'Environments',
    help: 'Which workflow environments may dispatch to this device — draft, qa, production, in any combination. A run drops every target that does not allow its environment.',
    detail: 'Turn all three off and the device is parked: no run can reach it. If a run\'s whole selection is dropped this way it is refused outright rather than executing against nothing.',
  },
  'devices.bulk_credential': {
    label: 'Credential to assign',
    help: 'Applied to every device you selected, overwriting whatever each one had. Built for the case where an inventory sync imported devices without auth.',
    detail: 'There is no undo. Selecting "(none)" is a valid choice and clears the credential on all of them.',
  },

  // --------------------------------------------------------------- Device pools
  'pools.name': {
    label: 'Name',
    help: 'How the pool is picked when a workflow or schedule chooses targets. Name it after what the group means — "core-routers", "dc1-edge".',
  },
  'pools.description': {
    label: 'Description',
    help: 'Free-text context for whoever inherits this pool. Optional, and shown on the pool card.',
  },
  'pools.environments': {
    label: 'Environments',
    help: 'Which workflow environments may target this pool. Applied on top of the per-device flags: a run must be allowed by the pool AND by the member it fans out to.',
    detail: 'So a pool open to production still only reaches the members that allow production. Turning all three off parks the whole pool.',
  },
  'pools.members': {
    label: 'Devices',
    help: 'The static list of devices this pool expands to at dispatch time. Search by name, IP, site, role or vendor and click to toggle membership.',
    detail: 'Membership is a plain list — the backend also has a filter-rules field for dynamic pools, but it is never evaluated, so a pool only ever resolves to what is picked here.',
  },

  // ------------------------------------------------------------------ Schedules
  'schedule.name': {
    label: 'Name',
    help: 'How this schedule is identified in the schedules list and the calendar. Describe the cadence and the intent — "Hourly LLDP sync" beats "schedule 2".',
  },
  'schedule.repeat': {
    label: 'Repeat',
    help: 'Presets that generate the cron expression for you. Pick Custom to type raw cron instead.',
    detail: 'Whatever you choose, the "Next 5 runs" preview below recomputes live — check it before saving rather than trusting the cron in your head.',
  },
  'schedule.timezone': {
    label: 'Timezone',
    help: 'The zone the cron expression is evaluated in. Defaults to UTC; any IANA name works.',
    detail: 'The preview shows fire times in your local zone, not this one — so a schedule set to 09:00 Europe/Madrid will read differently in the list if you are elsewhere.',
  },
  'schedule.cron': {
    label: 'Cron expression',
    help: 'Five fields, or six with a leading seconds column. The @hourly / @daily / @weekly / @monthly shorthands also work.',
    detail: 'Six fields is how you get sub-minute cadence — "*/30 * * * * *" fires every 30 seconds.',
  },
  'schedule.runtime_inputs': {
    label: 'Runtime inputs',
    help: 'Values handed to the workflow on every fire of this schedule. The scheduler re-reads them each time, so they stay fixed until edited here.',
    detail: 'These fill the {{ input.* }} placeholders in node config. A schedule with none simply passes an empty input payload.',
  },
  'schedule.target_devices': {
    label: 'Target devices',
    help: 'The devices every fire of this schedule runs against. Nodes in per-device mode fan out over them.',
    detail: 'Leave empty to run once with no device context. Note the scheduler passes devices only — a schedule cannot target a pool.',
  },
  'schedule.enabled': {
    label: 'Enabled',
    help: 'Whether the scheduler actually fires this trigger. Disabled schedules stay in the list, keep their configuration, and are skipped on every tick.',
    detail: 'Disabling is the safe way to pause something you intend to resume — deleting loses the cron, the inputs and the targets.',
  },
  'schedule.webhook_url': {
    label: 'Webhook URL',
    help: 'POSTed as JSON after each scheduled run finishes. Works with Slack and Discord incoming webhooks, Teams, PagerDuty Events API, n8n, or any endpoint that accepts JSON.',
    detail: 'Leave blank to disable notifications entirely — the two checkboxes below do nothing without a URL.',
  },
  'schedule.notify_on_failure': {
    label: 'Notify on failure',
    help: 'POST to the webhook when a scheduled run ends in failure. On by default, because a schedule nobody watches is the one you want to hear about.',
  },
  'schedule.notify_on_completion': {
    label: 'Notify on completion',
    help: 'POST to the webhook when a scheduled run finishes successfully. Off by default — on a frequent schedule this is a lot of traffic.',
  },

  // ---------------------------------------------------------------- Credentials
  'credentials.name': {
    label: 'Name',
    help: 'How this credential is picked from a dropdown on a device, a Git remote or a bulk assignment. It is a label only — nothing authenticates with it.',
    detail: 'Name it after what it opens, not who owns it: "core-routers-ssh" survives a change of on-call, "juan-key" does not.',
  },
  'credentials.type': {
    label: 'Type',
    help: 'What the credential is for. It drives which pickers offer it: ssh and netconf on devices, git_token on HTTPS Git remotes, api_key on integrations.',
    detail: 'Pick "custom" for anything the list does not cover. The type steers the pickers; it never blocks a consumer from using the credential.',
  },
  'credentials.username': {
    label: 'Username',
    help: 'The login name sent to the device or service. For a Git HTTPS remote using a personal access token this is the account name, with the token itself in the password field.',
  },
  'credentials.auth_method': {
    label: 'Auth method',
    help: 'Password/token authenticates with a secret string; SSH private key authenticates with a PEM key that never leaves the backend.',
    detail: 'Switching this after saving does not erase the other secret — the backend keeps whichever you last filled and uses the one this setting selects.',
  },
  'credentials.password': {
    label: 'Password or token',
    help: 'Encrypted at rest and never returned to the UI, which is why editing shows an empty box instead of the stored value.',
    detail: 'Leaving it empty on an edit keeps the current secret. Type something only when you mean to rotate it.',
  },
  'credentials.private_key': {
    label: 'Private key (PEM)',
    help: 'The full PEM block, including the BEGIN and END lines. The server parses it before storing, so a malformed key is rejected here rather than at 3am mid-run.',
    detail: 'Paste the private half, never the .pub. Leaving it empty on an edit keeps the key already stored.',
  },
  'credentials.key_passphrase': {
    label: 'Key passphrase',
    help: 'Only needed when the private key itself is encrypted. Leave empty for an unprotected key.',
    detail: 'A wrong passphrase does not fail here — it fails at connect time, as an auth error that looks like a bad key.',
  },
};

export function findFieldHint(id: string): FieldHint | undefined {
  return FIELD_HINTS[id];
}
