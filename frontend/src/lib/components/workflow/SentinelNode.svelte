<script lang="ts">
  // Circular start/end node with the same 4-side handle layout as DagNode
  // so the user can wire connections from any side. Replaces xyflow's
  // built-in `input` / `output` node types which only expose a single
  // default handle each.
  //
  // `data.kind` = 'start' | 'end' picks the color scheme. When omitted
  // the node falls back to a neutral disc.

  import { Handle, Position } from '@xyflow/svelte';

  let { data } = $props();

  const kind = $derived<'start' | 'end' | 'neutral'>(
    data?.kind === 'start' || data?.kind === 'end' ? data.kind : 'neutral',
  );
  const label = $derived<string>(data?.label ?? (kind === 'start' ? 'Start' : kind === 'end' ? 'End' : ''));

  // Matching the inline style the editor used before, but isolated here so
  // we can tweak the disc look without hunting through page code.
  const discStyle = $derived(
    kind === 'start'
      ? 'background: rgb(16 185 129 / 0.2); border: 2px solid rgb(16 185 129); color: rgb(110 231 183);'
      : kind === 'end'
        ? 'background: rgb(239 68 68 / 0.2); border: 2px solid rgb(239 68 68); color: rgb(252 165 165);'
        : 'background: var(--fw-node-bg); border: 2px solid var(--fw-node-border); color: var(--fw-node-text);',
  );
</script>

<!-- Same 8-handle layout as DagNode. Hidden by default via global CSS
     in app.css; visible on hover / while connecting. -->
<Handle id="t-top" type="target" position={Position.Top} />
<Handle id="s-top" type="source" position={Position.Top} />
<Handle id="t-right" type="target" position={Position.Right} />
<Handle id="s-right" type="source" position={Position.Right} />
<Handle id="t-bottom" type="target" position={Position.Bottom} />
<Handle id="s-bottom" type="source" position={Position.Bottom} />
<Handle id="t-left" type="target" position={Position.Left} />
<Handle id="s-left" type="source" position={Position.Left} />

<div
  class="flex items-center justify-center rounded-full text-xs font-semibold select-none"
  style="{discStyle} width: 56px; height: 56px;"
>
  {label}
</div>
