<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="AI chat"
  lead="The full conversation UI with the assistant. Streams responses as they are generated, renders tool calls as collapsible cards, and accepts deep-link context from any other page."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/ai/chat"><code>/ai/chat</code></a>.
    Sidebar → <strong>Intelligence</strong> → <strong>AI</strong> →
    <em>Open chat</em>.
  </Callout>

  <section>
    <h2>Layout</h2>
    <p>
      Two-column layout. Left (288 px): conversations sidebar. Right: the
      current thread and input bar.
    </p>
  </section>

  <section>
    <h2>Conversations sidebar</h2>
    <p>
      Vertical list of every past conversation scoped to your
      user (admins see every conversation).
    </p>

    <h3>Header</h3>
    <p>Left to right:</p>
    <ul>
      <li>A bot avatar and the <em>"Conversations"</em> label.</li>
      <li>
        <strong>Clear all</strong> — admin-only. Hidden when there are zero
        conversations. Opens a confirmation; on confirm soft-deletes every
        active conversation in one request.
      </li>
      <li>
        <strong>New</strong> — discards the current thread state and starts
        fresh (no conversation id, empty message list, blank input).
      </li>
    </ul>

    <h3>Conversation row</h3>
    <p>
      Each row shows the conversation title (first user message, truncated to
      80 chars), the message count, and the relative time. Clicking the row
      loads that conversation into the thread pane. On hover, a trash icon
      appears to the right — clicking deletes that single conversation after
      confirmation.
    </p>

    <h3>Empty state</h3>
    <p>
      <em>"No conversations yet. Start chatting and the thread will appear
      here."</em>
    </p>
  </section>

  <section>
    <h2>Thread pane</h2>

    <h3>Header</h3>
    <p>
      Left: <em>"New conversation"</em> or <em>"Continue conversation"</em>
      label, plus a small indicator when a <code>?fix=</code> or
      <code>?context=</code> query is in effect. Right: a <em>Back to /ai</em>
      shortcut back to the hub.
    </p>

    <h3>Messages</h3>
    <p>
      Each message is rendered based on its role:
    </p>
    <dl>
      <dt>User</dt>
      <dd>
        Right-aligned. Primary-tinted bubble with the raw text (whitespace
        preserved). A small <em>user</em> avatar on the right.
      </dd>
      <dt>Assistant</dt>
      <dd>
        Left-aligned. Bot avatar on the left. Tool-call cards (if any) render
        above the text. The text body is rendered as Markdown for
        rich formatting (code blocks, lists, links). While streaming, a
        blinking caret shows at the end of the text.
      </dd>
    </dl>

    <h3>Links in an answer</h3>
    <p>
      When the agent reports on a resource it links to its page — ask it about
      a workflow and the name comes back clickable. The link comes from the
      tool result itself (<code>get_workflow_details</code> and
      <code>list_workflows</code> return a <code>url</code>); the agent is
      told never to assemble one from an id, so a link you see is one the
      backend actually produced.
    </p>
    <p>
      Links open in a <strong>new tab</strong>, so following one never costs
      you the conversation.
    </p>
    <p>
      Two rules the renderer enforces, regardless of what the model writes:
      a link must point at one of this app's <em>resource</em> pages
      (<code>/workflows</code>, <code>/runs</code>, <code>/snippets</code>,
      <code>/devices</code>, …), and any origin in the href is discarded in
      favor of your own. Anything else — an external URL, a
      <code>javascript:</code> href, a route that acts rather than displays
      like <code>/logout</code> — renders as plain text. The model is
      untrusted input, and a markdown link is the cheapest phishing vector
      there is.
    </p>
    <p>
      Set <code>Workflow__PublicBaseUrl</code> (env <code>FRONTEND_ORIGIN</code>)
      to the URL operators actually type. It doesn't matter for the web chat —
      the path is what gets used — but answers relayed to a messaging channel
      carry the absolute URL, and there a wrong origin is a dead link.
    </p>

    <h3>Tool call cards</h3>
    <p>
      Each tool invocation produces a collapsible card:
    </p>
    <ul>
      <li><strong>Running</strong> — amber indicator, the tool name, and the arguments preview.</li>
      <li><strong>OK</strong> — green, shows the result preview. An attachment (e.g. a report file) links to its download URL.</li>
      <li><strong>Failed</strong> — red, with the error preview.</li>
    </ul>
    <p>
      Several cards can stack under a single assistant message when the agent
      chains tools in one turn.
    </p>

    <h3>Token telemetry</h3>
    <p>
      After a message completes, a tiny row shows <code>N in · M out</code> —
      hidden by default, surfaces on hover. Useful when tuning prompts for
      cost.
    </p>

    <h3>Timeout and errors</h3>
    <ul>
      <li>If the stream hits its deadline (configurable via <code>AI_STREAM_DEADLINE_SECONDS</code> in the deploy <code>.env</code>, default 240&nbsp;seconds), a warning alert reports the limit and suggests a narrower prompt.</li>
      <li>Per-message errors (e.g. <code>no_provider</code>) render as error alerts with a link to configure the provider.</li>
    </ul>
  </section>

  <section>
    <h2>Input bar</h2>
    <p>
      Pinned to the bottom. A textarea plus a send/stop button.
    </p>

    <h3>Agent picker</h3>
    <p>
      A compact control with a bot icon, sitting just above the text box so you
      always see who you are writing to. It appears whenever at least one agent
      exists and lists <em>Default agent</em> (the agent with role
      <code>assistant</code>) followed by every agent by name. The selected agent
      id is sent with each message. The picker is disabled while a reply is
      streaming. If the label reads <em>Unavailable agent</em>, the agent this
      conversation used has been deleted or disabled — pick another to continue.
    </p>

    <h3>Submission</h3>
    <ul>
      <li><kbd>Enter</kbd> sends the message.</li>
      <li><kbd>Shift+Enter</kbd> inserts a newline.</li>
      <li>The send button is disabled while the input is empty or a stream is in flight.</li>
      <li>During streaming, <em>Send</em> is replaced with a red <em>Stop</em> button that aborts the current request (a toast confirms).</li>
    </ul>

    <h3>Slash commands</h3>
    <p>
      Typing one of these followed by a space rewrites the input into a
      richer prompt:
    </p>
    <dl>
      <dt><code>/run</code></dt><dd>Rewrites to <em>"Analyze run "</em>.</dd>
      <dt><code>/workflow</code></dt><dd>Rewrites to <em>"Describe workflow "</em>.</dd>
      <dt><code>/device</code></dt><dd>Rewrites to <em>"Show info for device "</em>.</dd>
    </dl>
    <p>
      You paste the id / IP right after the space and send. The agent picks
      up the intent and calls the appropriate tool.
    </p>
  </section>

  <section>
    <h2>Deep-link entry points</h2>
    <p>
      The chat recognizes four query parameters when the page mounts:
    </p>

    <h3><code>?fix=&lt;json&gt;</code></h3>
    <p>
      Used by the run monitor's <em>Fix with AI</em> and <em>Edit with AI</em>
      buttons. The JSON blob contains context about the failing step (node
      id, error, logs preview, service id, run id, workflow id) or the
      target node for edits.
    </p>
    <p>
      On mount the chat builds a rich diagnostic prompt from this context and
      <strong>auto-sends</strong> it after the textarea binding settles —
      you land on a conversation already in progress. A small yellow
      <em>"Context attached from Fix with AI"</em> indicator appears under the
      thread header.
    </p>
    <p>
      The exact prompt shape:
    </p>
    <ul>
      <li><strong>Edit mode</strong> — "Edit node <code>{'{node_id}'}</code> in workflow <code>{'{workflow_id}'}</code>. Describe the change you need — the agent can inspect the current DAG and apply the new config directly via <code>update_workflow_node_config</code>."</li>
      <li><strong>Fix mode</strong> — a bullet list of the run id, node, service id, error, workflow, followed by "Diagnose the failure (use <code>get_run_details</code>, <code>get_step_logs</code>, <code>get_workflow_details</code>), explain the root cause, and apply a fix to the workflow if one is clear."</li>
    </ul>

    <h3><code>?context=&lt;slug&gt;</code></h3>
    <p>
      Used by the guide mascot's <em>"Open full chat"</em>. Pre-populates the
      input with <em>"Help me with: {'{slug}'}"</em>. You can edit before
      sending — no auto-send.
    </p>

    <h3><code>?q=&lt;text&gt;</code></h3>
    <p>
      Generic prompt prefill. Useful for links that plant a prompt in the
      user's input without assuming they want to send it.
    </p>

    <h3><code>?conversation=&lt;id&gt;</code></h3>
    <p>
      Opens the specified conversation by id. Used by the hub's "Recent
      conversations" links.
    </p>
  </section>

  <section>
    <h2>How a reply streams in</h2>
    <p>
      A reply arrives as a sequence of events:
    </p>
    <dl>
      <dt>conversation</dt><dd>First event. Delivers the conversation id so follow-up messages land in the same thread.</dd>
      <dt>text</dt><dd>Delta — appended to the assistant's content.</dd>
      <dt>tool_start</dt><dd>A new tool card appears in "running" state.</dd>
      <dt>tool_result</dt><dd>The most recent running card of that name flips to ok or failed with a preview.</dd>
      <dt>done</dt><dd>Final token counters.</dd>
      <dt>timeout</dt><dd>The stream deadline hit (the event carries the deadline in seconds). If any partial text was received it's kept with an appended notice.</dd>
      <dt>error</dt><dd>Stream-level error surfaced as a per-message error.</dd>
    </dl>
    <p>
      Once the stream ends (or the user clicks Stop), the thread scrolls to
      the bottom and the conversations sidebar refreshes so
      brand-new threads appear without a manual reload.
    </p>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — can chat. Can see and delete own conversations. Tool calls that need write access return a 403 at dispatch time; the card flips to failed with the error.</li>
      <li><strong>Operator</strong> — same plus full write tool access.</li>
      <li><strong>Admin</strong> — sees every conversation; can use the sidebar's <em>Clear all</em> bulk delete.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai/providers">Providers</a> — where the assistant's LLM comes from.</li>
      <li><a href="/docs/ai/agents">Agents</a> — profile configuration.</li>
      <li><a href="/docs/runs">Runs</a> — source of the <em>Fix with AI</em> flow.</li>
    </ul>
  </section>
</DocLayout>
