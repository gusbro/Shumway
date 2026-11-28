// Draws the DOT a server sends for a graph (ADR-072, D15): the part of DOT
// the servers write, laid out by dagre and drawn as SVG. The kernel loads
// this module the first time an answer has a graph, so a notebook that draws
// none never downloads it.

import { Graph, layout } from '@dagrejs/dagre';

type Attributes = Map<string, string>;

interface IDotNode {
  id: string;
  attributes: Attributes;
}

interface IDotEdge {
  from: string;
  to: string;
  attributes: Attributes;
}

export interface IDotGraph {
  directed: boolean;
  nodes: IDotNode[];
  edges: IDotEdge[];
}

// --- reading ---------------------------------------------------------------

type Token = { kind: 'id'; text: string } | { kind: 'punct'; text: string };

function tokens(text: string): Token[] {
  const found: Token[] = [];
  let at = 0;
  while (at < text.length) {
    const c = text[at];
    if (/\s/.test(c)) { at++; continue; }
    if (text.startsWith('//', at) || (c === '#' && (at === 0 || text[at - 1] === '\n'))) {
      while (at < text.length && text[at] !== '\n') at++;
      continue;
    }
    if (text.startsWith('/*', at)) {
      const end = text.indexOf('*/', at + 2);
      at = end < 0 ? text.length : end + 2;
      continue;
    }
    if (text.startsWith('->', at) || text.startsWith('--', at)) {
      found.push({ kind: 'punct', text: text.slice(at, at + 2) });
      at += 2;
      continue;
    }
    if ('{}[]=;,:'.includes(c)) {
      found.push({ kind: 'punct', text: c });
      at++;
      continue;
    }
    if (c === '"') {
      let value = '';
      at++;
      while (at < text.length && text[at] !== '"') {
        // \" is a quote; any other escape is the label's, such as \n.
        if (text[at] === '\\' && text[at + 1] === '"') { value += '"'; at += 2; continue; }
        if (text[at] === '\\' && text[at + 1] === '\\') { value += '\\'; at += 2; continue; }
        value += text[at++];
      }
      at++;
      found.push({ kind: 'id', text: value });
      continue;
    }
    const word = /^[^\s{}[\]=;,:"]+/.exec(text.slice(at));
    const end = word ? word[0].length : 1;
    found.push({ kind: 'id', text: text.slice(at, at + end) });
    at += end;
  }
  return found;
}

/** Reads a graph: its nodes and edges, with their attributes. Default
 *  attribute statements apply to what follows them, as in DOT; a subgraph's
 *  statements are the graph's. */
export function parseDot(text: string): IDotGraph {
  const list = tokens(text);
  let at = 0;
  const peek = (offset = 0) => list[at + offset];
  const is = (t: string, offset = 0) => peek(offset)?.text === t;
  const next = () => list[at++];

  const graph: IDotGraph = { directed: false, nodes: [], edges: [] };
  const byId = new Map<string, IDotNode>();
  const node = (id: string) => {
    let found = byId.get(id);
    if (!found) {
      found = { id, attributes: new Map(nodeDefaults) };
      byId.set(id, found);
      graph.nodes.push(found);
    }
    return found;
  };
  const nodeDefaults: Attributes = new Map();
  const edgeDefaults: Attributes = new Map();

  const attributeList = (into: Attributes) => {
    while (is('[')) {
      next();
      while (peek() && !is(']')) {
        const name = next().text;
        if (is('=')) {
          next();
          into.set(name, next()?.text ?? '');
        }
        if (is(',') || is(';')) next();
      }
      next();
    }
  };
  // A node id, without the port DOT may append to it.
  const nodeId = () => {
    const id = next().text;
    while (is(':')) { next(); next(); }
    return id;
  };

  const statements = () => {
    while (peek() && !is('}')) {
      if (is('{') || is('subgraph')) {
        if (is('subgraph')) { next(); if (!is('{')) next(); }
        next();
        statements();
        next();
      } else if ((is('node') || is('edge') || is('graph')) && is('[', 1)) {
        const kind = next().text;
        const target = kind === 'node' ? nodeDefaults : kind === 'edge' ? edgeDefaults : new Map();
        attributeList(target);
      } else if (is('=', 1)) {
        next(); next(); next();
      } else {
        const chain = [nodeId()];
        while (is('->') || is('--')) {
          next();
          chain.push(nodeId());
        }
        const attributes: Attributes = new Map(chain.length > 1 ? edgeDefaults : []);
        attributeList(attributes);
        if (chain.length === 1) {
          const found = node(chain[0]);
          attributes.forEach((value, name) => found.attributes.set(name, value));
        } else {
          chain.forEach(id => node(id));
          for (let i = 0; i + 1 < chain.length; i++)
            graph.edges.push({ from: chain[i], to: chain[i + 1], attributes: new Map(attributes) });
        }
      }
      if (is(';') || is(',')) next();
    }
  };

  if (is('strict')) next();
  graph.directed = next()?.text === 'digraph';
  if (!is('{')) next();
  if (!is('{')) throw new Error('not a DOT graph');
  next();
  statements();
  return graph;
}

// --- drawing ---------------------------------------------------------------

const FONT_SIZE = 14;
const LINE_HEIGHT = 17;
const FONT = 'Helvetica, Arial, sans-serif';

let measurer: CanvasRenderingContext2D | null | undefined;
// Ids of one drawing apart from another's, should two share a document.
let drawings = 0;

function textSize(text: string): { width: number; height: number } {
  const lines = text.split('\n');
  if (measurer === undefined) {
    measurer = typeof document === 'undefined' ? null : document.createElement('canvas').getContext('2d');
    if (measurer) measurer.font = `${FONT_SIZE}px ${FONT}`;
  }
  const width = Math.max(...lines.map(line =>
    measurer ? measurer.measureText(line).width : line.length * FONT_SIZE * 0.6));
  return { width, height: lines.length * LINE_HEIGHT };
}

const escapeXml = (text: string) =>
  text.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);

// A label's line breaks, as DOT writes them; \l and \r end a line too.
const labelText = (label: string) => label.replace(/\\[nlr]/g, '\n');

type Shape = 'ellipse' | 'box' | 'diamond' | 'plain' | 'point' | 'circle' | 'doublecircle';

function shapeOf(name: string | undefined): Shape {
  switch ((name ?? 'ellipse').toLowerCase()) {
    case 'box': case 'rect': case 'rectangle': case 'square':
    case 'note': case 'tab': case 'folder': case 'component': case 'box3d':
      return 'box';
    case 'diamond': case 'mdiamond':
      return 'diamond';
    case 'plaintext': case 'plain': case 'none':
      return 'plain';
    case 'point':
      return 'point';
    case 'circle':
      return 'circle';
    case 'doublecircle':
      return 'doublecircle';
    default:
      return 'ellipse';
  }
}

function nodeSize(shape: Shape, label: { width: number; height: number }) {
  switch (shape) {
    case 'box': return { width: Math.max(54, label.width + 16), height: Math.max(36, label.height + 12) };
    case 'diamond': return { width: (label.width + 16) * 1.6, height: (label.height + 12) * 1.6 };
    case 'plain': return { width: label.width + 8, height: label.height + 8 };
    case 'point': return { width: 8, height: 8 };
    case 'circle': case 'doublecircle': {
      const side = Math.max(36, label.width + 20, label.height + 16) + (shape === 'doublecircle' ? 8 : 0);
      return { width: side, height: side };
    }
    default: return { width: Math.max(54, label.width * 1.25 + 20), height: Math.max(36, label.height + 16) };
  }
}

const styles = (attributes: Attributes) =>
  new Set((attributes.get('style') ?? '').split(',').map(s => s.trim()).filter(s => s !== ''));

function stroke(style: Set<string>): string {
  if (style.has('dashed')) return ' stroke-dasharray="5,2"';
  if (style.has('dotted')) return ' stroke-dasharray="1,3"';
  return '';
}

/** A smooth path through the points dagre gives an edge. */
function curve(points: { x: number; y: number }[]): string {
  const p = points;
  let path = `M${p[0].x.toFixed(1)},${p[0].y.toFixed(1)}`;
  if (p.length === 2) return path + `L${p[1].x.toFixed(1)},${p[1].y.toFixed(1)}`;
  for (let i = 0; i + 1 < p.length; i++) {
    const before = p[Math.max(0, i - 1)];
    const after = p[Math.min(p.length - 1, i + 2)];
    const c1 = { x: p[i].x + (p[i + 1].x - before.x) / 6, y: p[i].y + (p[i + 1].y - before.y) / 6 };
    const c2 = { x: p[i + 1].x - (after.x - p[i].x) / 6, y: p[i + 1].y - (after.y - p[i].y) / 6 };
    path += `C${c1.x.toFixed(1)},${c1.y.toFixed(1)} ${c2.x.toFixed(1)},${c2.y.toFixed(1)} `
      + `${p[i + 1].x.toFixed(1)},${p[i + 1].y.toFixed(1)}`;
  }
  return path;
}

/** The nodes in the order a walk from the roots meets them, following the
 *  edges in their order: dagre keeps that order within a rank, so the
 *  arguments of a term are drawn left to right. */
function visitOrder(graph: IDotGraph): IDotNode[] {
  const byId = new Map(graph.nodes.map(node => [node.id, node]));
  const out = new Map<string, string[]>();
  const entered = new Set<string>();
  for (const edge of graph.edges) {
    out.set(edge.from, [...(out.get(edge.from) ?? []), edge.to]);
    if (edge.from !== edge.to) entered.add(edge.to);
  }
  const ordered: IDotNode[] = [];
  const seen = new Set<string>();
  const visit = (start: string) => {
    const stack = [start];
    while (stack.length > 0) {
      const id = stack.pop()!;
      if (seen.has(id)) continue;
      seen.add(id);
      ordered.push(byId.get(id)!);
      stack.push(...(out.get(id) ?? []).filter(next => !seen.has(next)).reverse());
    }
  };
  graph.nodes.filter(node => !entered.has(node.id)).forEach(node => visit(node.id));
  graph.nodes.forEach(node => visit(node.id));
  return ordered;
}

/** In a forest, such as the tree of a term, each node's place in a walk from
 *  the roots that takes each node's edges in their order. Undefined for any
 *  other graph. */
function treeWalk(graph: IDotGraph): Map<string, number> | undefined {
  const parents = new Map<string, Set<string>>();
  const children = new Map<string, string[]>();
  for (const edge of graph.edges) {
    parents.set(edge.to, (parents.get(edge.to) ?? new Set()).add(edge.from));
    const known = children.get(edge.from) ?? [];
    if (!known.includes(edge.to)) children.set(edge.from, [...known, edge.to]);
  }
  for (const [node, from] of parents) {
    if (from.size > 1 || from.has(node)) return undefined;
    // A node that is its own ancestor is on a cycle.
    const seen = new Set([node]);
    for (let up = [...from][0]; up !== undefined; up = [...(parents.get(up) ?? [])][0]) {
      if (seen.has(up)) return undefined;
      seen.add(up);
    }
  }
  const walk = new Map<string, number>();
  const stack = graph.nodes.filter(node => !parents.has(node.id)).map(node => node.id).reverse();
  while (stack.length > 0) {
    const id = stack.pop()!;
    walk.set(id, walk.size);
    stack.push(...[...(children.get(id) ?? [])].reverse());
  }
  return walk;
}

/** dagre's order of each layer, from a forest's walk: siblings keep the
 *  order of their edges, and the children of one node stay left of those of
 *  the next, so no edge crosses another. dagre's own ordering, even with
 *  left-right constraints, crosses the edges of two subtrees. The node
 *  dagre adds on an edge goes where the edge's child goes. */
function orderLayers(layered: Graph, walk: Map<string, number>): void {
  const layers = new Map<number, { v: string; key: number }[]>();
  for (const v of layered.nodes()) {
    const node = layered.node(v) as { rank?: number; edgeObj?: { w: string } };
    if (node.rank === undefined) continue;
    const key = walk.get(v) ?? (node.edgeObj ? walk.get(node.edgeObj.w) : undefined) ?? walk.size;
    layers.set(node.rank, [...(layers.get(node.rank) ?? []), { v, key }]);
  }
  for (const layer of layers.values())
    layer.sort((a, b) => a.key - b.key)
      .forEach((entry, i) => { (layered.node(entry.v) as { order?: number }).order = i; });
}

const LOOP_REACH = 24;
const LOOP_BULGE = 10;

/** Where the right side of a node's shape is, dy above or below its centre. */
function rightSide(shape: Shape, x: number, w: number, h: number, dy: number): number {
  const across = Math.min(1, Math.abs(dy) / (h / 2));
  switch (shape) {
    case 'box': case 'plain': return x + w / 2;
    case 'diamond': return x + (w / 2) * (1 - across);
    case 'point': return x + Math.sqrt(Math.max(0, 16 - dy * dy));
    default: return x + (w / 2) * Math.sqrt(1 - across * across);
  }
}

/** An edge from a node to itself, drawn as Graphviz draws one: a loop out of
 *  the node's right side and back into it, reach past that side. */
function loop(shape: Shape, x: number, y: number, w: number, h: number, reach: number, bulge: number): string {
  const dy = shape === 'point' ? 2 : h / 4;
  const side = rightSide(shape, x, w, h, dy);
  const out = (shape === 'point' ? x + 4 : x + w / 2) + reach;
  // A cubic whose two inner control points share an x goes 3/4 of the way to it.
  const c = side + (out - side) / 0.75;
  return `M${side.toFixed(1)},${(y - dy).toFixed(1)}`
    + `C${c.toFixed(1)},${(y - dy - bulge).toFixed(1)} ${c.toFixed(1)},${(y + dy + bulge).toFixed(1)}`
    + ` ${side.toFixed(1)},${(y + dy).toFixed(1)}`;
}

/** The SVG of a DOT graph: black on white, as Graphviz draws, so that it
 *  reads in a dark theme and in a saved notebook alike. */
export function drawDot(dot: string): string {
  const graph = parseDot(dot);
  const g = new Graph({ multigraph: true });
  g.setGraph({ rankdir: 'TB', nodesep: 30, ranksep: 45, edgesep: 15, marginx: 10, marginy: 10 });
  g.setDefaultEdgeLabel(() => ({}));

  const labelOf = (node: IDotNode) => labelText(node.attributes.get('label') ?? node.id);
  for (const node of visitOrder(graph)) {
    const shape = shapeOf(node.attributes.get('shape'));
    g.setNode(node.id, nodeSize(shape, shape === 'point' ? { width: 0, height: 0 } : textSize(labelOf(node))));
  }
  // An edge from a node to itself is drawn below, not by dagre, which only
  // keeps the room for it on the node's right. A node's second loop goes
  // around the label of its first, and so on.
  const loops = new Map<number, { reach: number; bulge: number }>();
  const room = new Map<number, number>();
  const reached = new Map<string, { reach: number; count: number; first: number }>();
  graph.edges.forEach((edge, i) => {
    if (edge.from !== edge.to) return;
    const label = edge.attributes.get('label');
    const beside = label ? 4 + textSize(labelText(label)).width : 0;
    const at = reached.get(edge.from) ?? { reach: LOOP_REACH, count: 0, first: i };
    loops.set(i, { reach: at.reach, bulge: LOOP_BULGE + 8 * at.count });
    room.set(at.first, at.reach + beside);
    reached.set(edge.from, { reach: at.reach + beside + 10, count: at.count + 1, first: at.first });
  });
  graph.edges.forEach((edge, i) => {
    const label = edge.attributes.get('label');
    const size = label ? textSize(labelText(label)) : { width: 0, height: 0 };
    if (loops.has(i)) {
      g.setEdge(edge.from, edge.to, { width: room.get(i) ?? 0, height: size.height, labelpos: 'c' }, String(i));
      return;
    }
    // Beside the edge, as Graphviz puts it, not across it.
    g.setEdge(edge.from, edge.to,
      { width: size.width, height: size.height, labelpos: 'r', labeloffset: 4 }, String(i));
  });
  const walk = treeWalk(graph);
  layout(g, walk ? { customOrder: layered => orderLayers(layered, walk) } : {});
  const byId = new Map(graph.nodes.map(node => [node.id, node]));

  const { width = 0, height = 0 } = g.graph();
  // One arrowhead for each colour of an edge.
  const arrows = new Map<string, string>();
  const prefix = `shumway-graph-${++drawings}`;
  const parts: string[] = [];

  graph.edges.forEach((edge, i) => {
    const style = styles(edge.attributes);
    if (style.has('invis')) return;
    const laid = g.edge({ v: edge.from, w: edge.to, name: String(i) });
    const color = edge.attributes.get('color') ?? 'black';
    if (!arrows.has(color)) arrows.set(color, `${prefix}-arrow-${arrows.size + 1}`);
    const arrow = graph.directed ? ` marker-end="url(#${arrows.get(color)})"` : '';
    const label = edge.attributes.get('label');
    const fontColor = edge.attributes.get('fontcolor') ?? 'black';
    const looped = loops.get(i);
    let path: string;
    let written = '';
    if (looped) {
      const node = g.node(edge.from);
      const shape = shapeOf(byId.get(edge.from)?.attributes.get('shape'));
      path = loop(shape, node.x, node.y, node.width, node.height, looped.reach, looped.bulge);
      if (label) {
        const content = labelText(label);
        const right = (shape === 'point' ? node.x + 4 : node.x + node.width / 2) + looped.reach + 4;
        written = text(content, right + textSize(content).width / 2, node.y, fontColor);
      }
    } else {
      path = curve(laid.points ?? []);
      if (label && laid.x !== undefined && laid.y !== undefined)
        written = text(labelText(label), laid.x, laid.y, fontColor);
    }
    parts.push(`<path d="${path}" fill="none" stroke="${escapeXml(color)}"`
      + `${style.has('bold') ? ' stroke-width="2"' : ''}${stroke(style)}${arrow}/>`, written);
  });

  for (const node of graph.nodes) {
    const style = styles(node.attributes);
    if (style.has('invis')) continue;
    const laid = g.node(node.id);
    const shape = shapeOf(node.attributes.get('shape'));
    const color = node.attributes.get('color') ?? 'black';
    const fill = style.has('filled')
      ? node.attributes.get('fillcolor') ?? node.attributes.get('color') ?? 'lightgrey'
      : 'none';
    const look = ` fill="${escapeXml(fill)}" stroke="${escapeXml(color)}"`
      + `${style.has('bold') ? ' stroke-width="2"' : ''}${stroke(style)}`;
    const { x, y, width: w, height: h } = laid;
    switch (shape) {
      case 'box':
        parts.push(`<rect x="${(x - w / 2).toFixed(1)}" y="${(y - h / 2).toFixed(1)}" width="${w.toFixed(1)}"`
          + ` height="${h.toFixed(1)}"${style.has('rounded') ? ' rx="6"' : ''}${look}/>`);
        break;
      case 'diamond':
        parts.push(`<polygon points="${x},${(y - h / 2).toFixed(1)} ${(x + w / 2).toFixed(1)},${y}`
          + ` ${x},${(y + h / 2).toFixed(1)} ${(x - w / 2).toFixed(1)},${y}"${look}/>`);
        break;
      case 'point':
        parts.push(`<circle cx="${x}" cy="${y}" r="4" fill="${escapeXml(color)}" stroke="${escapeXml(color)}"/>`);
        break;
      case 'plain':
        break;
      case 'doublecircle':
        parts.push(`<circle cx="${x}" cy="${y}" r="${(w / 2 - 4).toFixed(1)}"${look}/>`);
        parts.push(`<circle cx="${x}" cy="${y}" r="${(w / 2).toFixed(1)}" fill="none" stroke="${escapeXml(color)}"/>`);
        break;
      case 'circle':
        parts.push(`<circle cx="${x}" cy="${y}" r="${(w / 2).toFixed(1)}"${look}/>`);
        break;
      default:
        parts.push(`<ellipse cx="${x}" cy="${y}" rx="${(w / 2).toFixed(1)}" ry="${(h / 2).toFixed(1)}"${look}/>`);
    }
    if (shape !== 'point')
      parts.push(text(labelOf(node), x, y, node.attributes.get('fontcolor') ?? 'black'));
  }

  const markers = [...arrows].map(([color, id]) =>
    `<marker id="${id}" viewBox="0 0 10 10" refX="10" refY="5"`
    + ` markerWidth="8" markerHeight="8" orient="auto-start-reverse">`
    + `<path d="M0,0L10,5L0,10z" fill="${escapeXml(color)}"/></marker>`);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${Math.ceil(width)}" height="${Math.ceil(height)}"`
    + ` viewBox="0 0 ${Math.ceil(width)} ${Math.ceil(height)}" font-family="${FONT}" font-size="${FONT_SIZE}">`
    + `<rect width="100%" height="100%" fill="white"/>`
    + (markers.length > 0 ? `<defs>${markers.join('')}</defs>` : '')
    + parts.join('') + '</svg>';
}

function text(content: string, x: number, y: number, color: string): string {
  const lines = content.split('\n');
  const first = y - ((lines.length - 1) * LINE_HEIGHT) / 2;
  return `<text text-anchor="middle" dominant-baseline="central" fill="${escapeXml(color)}">`
    + lines.map((line, i) =>
      `<tspan x="${x.toFixed(1)}" y="${(first + i * LINE_HEIGHT).toFixed(1)}">${escapeXml(line)}</tspan>`).join('')
    + '</text>';
}
