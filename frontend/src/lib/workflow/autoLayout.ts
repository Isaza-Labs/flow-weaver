import dagre from '@dagrejs/dagre';
import type { Node, Edge } from '@xyflow/svelte';

// Dagre places nodes on a left-to-right Sugiyama layered graph. We feed it
// the existing workflow nodes + edges, it assigns ranks (columns) by
// topological distance from __start__ and distributes branches vertically
// to minimize edge crossings. That solves the classic "agent dumps every
// node at 0/0" problem: after this pass each node has a sensible x/y.
//
// Handle selection is done in a second pass once positions are known.
// xyflow otherwise defaults to the `top` handle, which makes horizontal
// flows curve upward for no reason — see DagNode.svelte for the eight
// handle ids this function emits into `sourceHandle` / `targetHandle`.

const SENTINEL_WIDTH = 56;
const SENTINEL_HEIGHT = 56;
const NODE_WIDTH = 180;
const NODE_HEIGHT = 72;
// Subflow nodes are visually wider so the encapsulated workflow
// reads as a "block" instead of a single step. Dagre uses these to leave
// extra horizontal whitespace around them, so neighbouring nodes don't
// crowd the subflow's drill-down hint.
const SUBFLOW_WIDTH = 220;
const SUBFLOW_HEIGHT = 96;

// A node is a subflow when its data carries the literal snippet id used
// by the schema's sentinel "subflow" entry. We accept both the camelCase
// (xyflow Node) and snake_case (raw JSON) shapes the editor cycles
// through during import / export.
function isSubflowNode(n: Node): boolean {
  const d = n.data as Record<string, unknown> | undefined;
  if (!d) return false;
  return d.snippetId === 'subflow' || d.snippet_id === 'subflow';
}

// Column / row spacing. Picked to match the visual rhythm of the editor:
// ~200 px between columns keeps the bezier curves gentle, 60 px between
// stacked branches leaves room for the edge labels (`failure`, etc.).
const RANK_SEP = 200;
const NODE_SEP = 60;

export interface AutoLayoutOptions {
  direction?: 'LR' | 'TB';
  rankSep?: number;
  nodeSep?: number;
}

export function autoLayout(
  nodes: Node[],
  edges: Edge[],
  options: AutoLayoutOptions = {},
): { nodes: Node[]; edges: Edge[] } {
  if (nodes.length === 0) return { nodes, edges };

  const direction = options.direction ?? 'LR';
  const g = new dagre.graphlib.Graph({ compound: false });
  g.setGraph({
    rankdir: direction,
    ranksep: options.rankSep ?? RANK_SEP,
    nodesep: options.nodeSep ?? NODE_SEP,
    // `tight-tree` is the dagre default and produces the most compact
    // layered layout for small DAGs (our typical 3–15 node workflow).
    ranker: 'tight-tree',
  });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    const isSentinel = n.type === 'sentinel';
    const isSubflow = !isSentinel && isSubflowNode(n);
    g.setNode(n.id, {
      width: isSentinel ? SENTINEL_WIDTH : isSubflow ? SUBFLOW_WIDTH : NODE_WIDTH,
      height: isSentinel ? SENTINEL_HEIGHT : isSubflow ? SUBFLOW_HEIGHT : NODE_HEIGHT,
    });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }

  dagre.layout(g);

  const placed: Node[] = nodes.map((n) => {
    const pos = g.node(n.id);
    if (!pos) return n;
    // Dagre returns the node *center*; xyflow positions by top-left, so we
    // subtract half the size to align the visual box with the rank grid.
    const isSentinel = n.type === 'sentinel';
    const isSubflow = !isSentinel && isSubflowNode(n);
    const w = isSentinel ? SENTINEL_WIDTH : isSubflow ? SUBFLOW_WIDTH : NODE_WIDTH;
    const h = isSentinel ? SENTINEL_HEIGHT : isSubflow ? SUBFLOW_HEIGHT : NODE_HEIGHT;
    return {
      ...n,
      position: { x: Math.round(pos.x - w / 2), y: Math.round(pos.y - h / 2) },
    };
  });

  const byId = new Map(placed.map((n) => [n.id, n] as const));
  const rewired: Edge[] = edges.map((e) => {
    const src = byId.get(e.source);
    const tgt = byId.get(e.target);
    if (!src || !tgt) return e;
    const { sourceHandle, targetHandle } = pickHandles(
      src.position,
      tgt.position,
      direction,
    );
    // Regenerate the id so xyflow doesn't dedupe two edges that now use
    // different handles between the same pair.
    const edgeType = typeof e.data?.edgeType === 'string' ? e.data.edgeType : '';
    return {
      ...e,
      sourceHandle,
      targetHandle,
      id: `e-${e.source}:${sourceHandle}-${e.target}:${targetHandle}-${edgeType}`,
    };
  });

  return { nodes: placed, edges: rewired };
}

// Picks the pair of handles (one source-side, one target-side) that puts
// the edge on the shortest straight-ish line given the post-layout node
// positions. The handle ids match those declared in DagNode.svelte /
// SentinelNode.svelte.
export function pickHandles(
  source: { x: number; y: number },
  target: { x: number; y: number },
  direction: 'LR' | 'TB' = 'LR',
): { sourceHandle: string; targetHandle: string } {
  const dx = target.x - source.x;
  const dy = target.y - source.y;

  if (direction === 'LR') {
    // Forward flow (left-to-right): right → left is the default. Handles
    // the 95% case — horizontal chain, success branches going slightly
    // up/down, failure lines curving underneath.
    if (dx > 0) return { sourceHandle: 's-right', targetHandle: 't-left' };
    // Backward edge (rare — loops or conditional re-entry). Route along
    // the bottom so it doesn't overlap the forward line above.
    if (dx < 0) return { sourceHandle: 's-bottom', targetHandle: 't-bottom' };
    // Same column (identical x after layout): route vertically.
    return dy >= 0
      ? { sourceHandle: 's-bottom', targetHandle: 't-top' }
      : { sourceHandle: 's-top', targetHandle: 't-bottom' };
  }

  // Top-to-bottom flow: mirror the logic on the vertical axis.
  if (dy > 0) return { sourceHandle: 's-bottom', targetHandle: 't-top' };
  if (dy < 0) return { sourceHandle: 's-right', targetHandle: 't-right' };
  return dx >= 0
    ? { sourceHandle: 's-right', targetHandle: 't-left' }
    : { sourceHandle: 's-left', targetHandle: 't-right' };
}

// Heuristic: should we run the auto-layout pass on this workflow? The
// primary signal is edges without handles — the AI agent's create_workflow
// never sets source_handle/target_handle, so xyflow defaults those edges
// to the Top handle and the diagram ends up with every line curving
// upward. If ANY edge is handle-less we re-run the layout so Dagre
// picks sides based on actual positions. Secondary signals cover the
// "all nodes at 0/0" case where positions also need fixing.
export function needsAutoLayout(nodes: Node[], edges: Edge[] = []): boolean {
  // Agent-authored edges (or any manual edit that skipped a handle drag)
  // — re-layout so handles land on the correct sides. This is the main
  // trigger; the other branches are fallbacks for edge-case workflows.
  if (edges.some((e) => !e.sourceHandle || !e.targetHandle)) return true;

  const taskNodes = nodes.filter((n) => n.type !== 'sentinel');
  if (taskNodes.length <= 1) return false;
  const xs = new Set(taskNodes.map((n) => Math.round(n.position.x)));
  const ys = new Set(taskNodes.map((n) => Math.round(n.position.y)));
  // Everyone stacked on the same point → definitely needs a layout.
  if (xs.size === 1 && ys.size === 1) return true;
  // Everyone on the same row at y=0 (the skill's old "0/0 is fine" default).
  if (ys.size === 1 && taskNodes[0].position.y === 0 && taskNodes.length >= 3) {
    return true;
  }
  return false;
}
