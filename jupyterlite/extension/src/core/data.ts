// The plots of show_data/1 and %%data (ADR-072, D15): the server sends the
// keys of a matplotlib call, as the reference's server does, and this module
// writes the Vega-Lite specification JupyterLab draws. An option it does not
// draw is named, never dropped in silence.

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };
type Dict = { [key: string]: Json };
type Spec = { [key: string]: unknown };

export const VEGA_LITE = 'application/vnd.vegalite.v5+json';
const SCHEMA = 'https://vega.github.io/schema/vega-lite/v5.json';

/** A plot: its specification, and the keys it did not draw. */
export interface IPlot {
  spec: Spec;
  ignored: string[];
}

const isDict = (value: Json | undefined): value is Dict =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** The reference's server writes a word as text, true and false included. */
function bool(value: Json | undefined): boolean | undefined {
  if (typeof value === 'boolean') return value;
  if (value === 'true') return true;
  if (value === 'false') return false;
  return undefined;
}

const num = (value: Json | undefined): number | undefined => (typeof value === 'number' ? value : undefined);
const text = (value: Json | undefined): string | undefined =>
  typeof value === 'string' ? value : typeof value === 'number' ? String(value) : undefined;
const list = (value: Json | undefined): Json[] => (Array.isArray(value) ? value : value === undefined ? [] : [value]);
const numbers = (value: Json | undefined): number[] => list(value).filter((v): v is number => typeof v === 'number');

/** matplotlib's line styles, as dash patterns. */
function dash(style: Json | undefined): number[] | undefined {
  switch (style) {
    case '--': case 'dashed': return [6, 3];
    case ':': case 'dotted': return [1, 3];
    case '-.': case 'dashdot': return [6, 3, 1, 3];
    default: return undefined;
  }
}

/** matplotlib's markers, as Vega-Lite's point shapes. */
function shape(marker: Json | undefined): string | undefined {
  switch (marker) {
    case 'o': case '.': return 'circle';
    case 's': return 'square';
    case '^': return 'triangle-up';
    case 'v': return 'triangle-down';
    case '<': return 'triangle-left';
    case '>': return 'triangle-right';
    case 'D': case 'd': return 'diamond';
    case '+': return 'cross';
    case 'x': return 'cross';
    default: return undefined;
  }
}

// A title is text, or the keyword arguments of plt.title: label and its looks.
function titleOf(value: Json | undefined, key = 'label'): { text?: string; extra: Spec } {
  if (isDict(value)) {
    const extra: Spec = {};
    const size = num(value.fontsize);
    if (size !== undefined) extra.fontSize = size;
    if (text(value.color)) extra.color = text(value.color);
    if (value.loc === 'left') extra.anchor = 'start';
    if (value.loc === 'right') extra.anchor = 'end';
    return { text: text(value[key]) ?? text(value.t) ?? text(value.s), extra };
  }
  return { text: text(value), extra: {} };
}

const columns = (rows: { [name: string]: unknown }[]) => ({ values: rows });

/** The Vega-Lite specification of a show_data dictionary. */
export function plotOf(data: Dict): IPlot {
  const d: Dict = { ...data };
  const used = new Set<string>(['type']);
  const take = (key: string): Json | undefined => {
    if (key in d) used.add(key);
    return d[key];
  };
  const type = text(d.type) ?? '';
  // matplotlib names no axis unless xlabel or ylabel do.
  const x: Spec = { field: 'x', type: 'quantitative', title: null };
  const y: Spec = { field: 'y', type: 'quantitative', title: null };
  let width: number | undefined;
  let height: number | undefined;
  let spec: Spec | null = null;

  const markLooks = (mark: Spec) => {
    const color = text(take('color')) ?? text(take('c'));
    if (color) mark.color = color;
    const alpha = num(take('alpha'));
    if (alpha !== undefined) mark.opacity = alpha;
    const lineStyle = dash(take('linestyle') ?? take('ls'));
    if (lineStyle) mark.strokeDash = lineStyle;
    const lineWidth = num(take('linewidth') ?? take('lw'));
    if (lineWidth !== undefined) mark.strokeWidth = lineWidth;
    return mark;
  };
  const xy = () => {
    const xs = list(take('x'));
    const ys = list(take('y'));
    return xs.map((value, i) => ({ x: value, y: ys[i] ?? null }));
  };

  switch (type) {
    case 'plot': case 'loglog': case 'semilogx': case 'semilogy': {
      const mark = markLooks({ type: 'line' });
      const marker = shape(take('marker'));
      if (marker) mark.point = { shape: marker, filled: true, ...(mark.color ? { color: mark.color } : {}) };
      if (type === 'loglog' || type === 'semilogx') x.scale = { type: 'log' };
      if (type === 'loglog' || type === 'semilogy') y.scale = { type: 'log' };
      spec = { data: columns(xy()), mark, encoding: { x, y } };
      break;
    }
    case 'scatter': {
      const mark = markLooks({ type: 'point', filled: true });
      const marker = shape(take('marker'));
      if (marker) mark.shape = marker;
      const sizes = list(take('s'));
      const rows = xy().map((row, i) => ({ ...row, size: num(sizes[i] ?? sizes[0]) }));
      const encoding: Spec = { x, y };
      if (sizes.length > 1) encoding.size = { field: 'size', type: 'quantitative', legend: null };
      else if (sizes.length === 1 && num(sizes[0]) !== undefined) mark.size = num(sizes[0]);
      spec = { data: columns(rows), mark, encoding };
      break;
    }
    case 'step': {
      const where = text(take('where')) ?? 'pre';
      const mark = markLooks({
        type: 'line', interpolate: where === 'post' ? 'step-after' : where === 'mid' ? 'step' : 'step-before'
      });
      spec = { data: columns(xy()), mark, encoding: { x, y } };
      break;
    }
    case 'stem': {
      const rows = xy();
      const color = text(take('color')) ?? '#1f77b4';
      spec = {
        data: columns(rows),
        layer: [
          { mark: { type: 'rule', color }, encoding: { x, y, y2: { datum: 0 } } },
          { mark: { type: 'point', filled: true, color }, encoding: { x, y } }
        ]
      };
      break;
    }
    case 'ecdf': {
      const values = numbers(take('x'));
      spec = {
        data: columns(values.map(v => ({ v }))),
        transform: [
          { sort: [{ field: 'v' }], window: [{ op: 'cume_dist', as: 'p' }] }
        ],
        mark: markLooks({ type: 'line', interpolate: 'step-after' }),
        encoding: {
          x: { field: 'v', type: 'quantitative', title: null },
          y: { field: 'p', type: 'quantitative', title: null, scale: { domain: [0, 1] } }
        }
      };
      break;
    }
    case 'bar': case 'barh': {
      const horizontal = type === 'barh';
      const at = list(take(horizontal ? 'y' : 'x'));
      const length = list(take(horizontal ? 'width' : 'height'));
      take(horizontal ? 'height' : 'width');
      const colors = list(take('color'));
      const rows = at.map((value, i) => ({ at: value, length: length[i] ?? null, i }));
      const category = { field: 'at', type: 'ordinal', sort: null, title: null, axis: { labelAngle: 0 } };
      const amount = { field: 'length', type: 'quantitative', title: null };
      const encoding: Spec = horizontal ? { y: category, x: amount } : { x: category, y: amount };
      const mark: Spec = { type: 'bar' };
      const bars: Spec = { mark };
      if (colors.length > 1) {
        bars.encoding = {
          color: {
            field: 'i', type: 'nominal', legend: null,
            scale: { domain: rows.map(r => r.i), range: colors.map(c => text(c) ?? 'steelblue') }
          }
        };
      } else if (text(colors[0])) mark.color = text(colors[0]);
      const edge = text(take('edgecolor'));
      if (edge) mark.stroke = edge;
      const alpha = num(take('alpha'));
      if (alpha !== undefined) mark.opacity = alpha;
      const label = take('bar_label');
      spec = { data: columns(rows), encoding, layer: [bars] };
      if (label !== undefined) {
        (spec.layer as Spec[]).push({
          mark: {
            type: 'text', color: 'black',
            ...(horizontal ? { align: 'left', dx: 3 } : { baseline: 'bottom', dy: -2 })
          },
          encoding: { text: { field: 'length', type: 'quantitative' } }
        });
      }
      break;
    }
    case 'hist': {
      const values = numbers(take('x'));
      const bins = num(take('bins')) ?? 10;
      const mark = markLooks({ type: 'bar' });
      const edge = text(take('edgecolor'));
      if (edge) mark.stroke = edge;
      const density = bool(take('density'));
      spec = {
        data: columns(values.map(v => ({ v }))),
        mark,
        encoding: {
          x: { field: 'v', bin: { maxbins: bins }, type: 'quantitative', title: null },
          y: density
            ? { aggregate: 'count', type: 'quantitative', title: null, stack: 'normalize' }
            : { aggregate: 'count', type: 'quantitative', title: null }
        }
      };
      break;
    }
    case 'pie': {
      const values = numbers(take('x'));
      const labels = list(take('labels'));
      const colors = list(take('colors'));
      const percent = take('autopct');
      const rows = values.map((v, i) => ({ v, label: text(labels[i]) ?? String(i + 1), i }));
      const color: Spec = { field: 'label', type: 'nominal', sort: null, title: null };
      if (colors.length > 0) color.scale = { range: colors.map(c => text(c) ?? 'steelblue') };
      const pie: Spec = {
        data: columns(rows),
        encoding: { theta: { field: 'v', type: 'quantitative', stack: true }, color, order: { field: 'i' } },
        layer: [{ mark: { type: 'arc', outerRadius: 120 } }]
      };
      if (percent !== undefined) {
        pie.transform = [
          { joinaggregate: [{ op: 'sum', field: 'v', as: 'total' }] },
          { calculate: "format(datum.v / datum.total, '.1%')", as: 'share' }
        ];
        (pie.layer as Spec[]).push({
          mark: { type: 'text', radius: 80, fill: 'white' },
          encoding: { text: { field: 'share', type: 'nominal' } }
        });
      }
      width = height = 260;
      spec = pie;
      break;
    }
    case 'stackplot': {
      const xs = list(take('x'));
      const series = list(take('y')).map(s => list(s));
      const labels = list(take('labels'));
      const colors = list(take('colors'));
      const rows: { [name: string]: unknown }[] = [];
      series.forEach((s, k) => s.forEach((value, i) =>
        rows.push({ x: xs[i] ?? i, y: value, series: text(labels[k]) ?? `y${k + 1}`, k })));
      const color: Spec = { field: 'series', type: 'nominal', sort: null, title: null };
      if (colors.length > 0) color.scale = { range: colors.map(c => text(c) ?? 'steelblue') };
      spec = {
        data: columns(rows),
        mark: { type: 'area' },
        encoding: { x, y: { ...y, stack: 'zero' }, color, order: { field: 'k' } }
      };
      break;
    }
    case 'errorbar': {
      const rows = xy().map((row, i) => {
        const yerr = list(take('yerr'));
        const xerr = list(take('xerr'));
        const ye = num(yerr[i] ?? yerr[0]) ?? 0;
        const xe = num(xerr[i] ?? xerr[0]) ?? 0;
        const yv = num(row.y as Json) ?? 0;
        const xv = num(row.x as Json) ?? 0;
        return { ...row, ylo: yv - ye, yhi: yv + ye, xlo: xv - xe, xhi: xv + xe };
      });
      const color = text(take('color')) ?? text(take('ecolor')) ?? '#1f77b4';
      const point = shape(take('fmt') ?? take('marker')) ?? 'circle';
      spec = {
        data: columns(rows),
        layer: [
          { mark: { type: 'rule', color }, encoding: { x, y: { field: 'ylo', type: 'quantitative', title: null }, y2: { field: 'yhi' } } },
          { mark: { type: 'rule', color }, encoding: { y, x: { field: 'xlo', type: 'quantitative', title: null }, x2: { field: 'xhi' } } },
          { mark: { type: 'point', filled: true, color, shape: point }, encoding: { x, y } }
        ]
      };
      break;
    }
    case 'boxplot': {
      const sets = list(take('x'));
      const groups = sets.some(Array.isArray) ? sets.map(list) : [sets];
      const labels = list(take('labels') ?? take('tick_labels'));
      const rows: { [name: string]: unknown }[] = [];
      groups.forEach((g, k) => g.forEach(v => rows.push({ v, set: text(labels[k]) ?? String(k + 1) })));
      spec = {
        data: columns(rows),
        mark: { type: 'boxplot', extent: 1.5 },
        encoding: {
          x: { field: 'set', type: 'ordinal', sort: null, title: null, axis: { labelAngle: 0 } },
          y: { field: 'v', type: 'quantitative', title: null }
        }
      };
      break;
    }
    case 'eventplot': {
      const positions = list(take('positions'));
      const rowsOf = positions.some(Array.isArray) ? positions.map(list) : [positions];
      const vertical = text(take('orientation')) === 'vertical';
      // A number for every row, or one per row, as matplotlib takes them.
      const perRow = (value: Json | undefined, fallback: (k: number) => number) => (k: number) =>
        (Array.isArray(value) ? num(value[k]) : num(value)) ?? fallback(k);
      const length = perRow(take('linelengths'), () => 1);
      const offset = perRow(take('lineoffsets'), k => k);
      const rows: { [name: string]: unknown }[] = [];
      rowsOf.forEach((r, k) => r.forEach(v =>
        rows.push({ v, lo: offset(k) - length(k) / 2, hi: offset(k) + length(k) / 2 })));
      const mark = markLooks({ type: 'rule' });
      if (!mark.color) mark.color = '#4c78a8';
      const at = { field: 'v', type: 'quantitative', title: null };
      const across = { field: 'lo', type: 'quantitative', title: null };
      spec = {
        data: columns(rows),
        mark,
        encoding: vertical ? { y: at, x: across, x2: { field: 'hi' } } : { x: at, y: across, y2: { field: 'hi' } }
      };
      break;
    }
    default:
      throw new Error(type === ''
        ? 'the data has no type'
        : `a ${type} plot is not supported here: plot, scatter, step, stem, ecdf, bar, barh, hist, `
          + 'pie, stackplot, errorbar, boxplot, eventplot, loglog, semilogx and semilogy are');
  }

  // The options every plot takes: titles, axes, scales, limits, legend.
  const top: Spec = { $schema: SCHEMA, ...spec };
  const title = titleOf(take('title'));
  const suptitle = titleOf(take('suptitle'), 't');
  if (suptitle.text || title.text) {
    top.title = suptitle.text
      ? { text: suptitle.text, ...suptitle.extra, ...(title.text ? { subtitle: title.text } : {}) }
      : { text: title.text, ...title.extra };
  }
  const axisTitle = (key: string) => {
    const label = titleOf(take(key));
    if (!label.text) return null;
    return { title: label.text, ...(label.extra.color ? { titleColor: label.extra.color } : {}) };
  };
  const config: Spec = {};
  const xAxis: Spec = {};
  const yAxis: Spec = {};
  const xTitle = axisTitle('xlabel');
  const yTitle = axisTitle('ylabel');
  const scaleOf = (key: string): Spec | undefined => {
    const s = text(take(key));
    return s === 'log' ? { type: 'log' } : s === 'symlog' ? { type: 'symlog' } : undefined;
  };
  const xScale = scaleOf('xscale');
  const yScale = scaleOf('yscale');
  const limits = (key: string, low: string, high: string): number[] | undefined => {
    const v = take(key);
    if (Array.isArray(v)) return numbers(v).length === 2 ? numbers(v) : undefined;
    if (isDict(v)) {
      const a = num(v[low]) ?? num(v[`${key[0]}min`]);
      const b = num(v[high]) ?? num(v[`${key[0]}max`]);
      return a !== undefined && b !== undefined ? [a, b] : undefined;
    }
    return undefined;
  };
  const xLim = limits('xlim', 'left', 'right');
  const yLim = limits('ylim', 'bottom', 'top');
  const ticks = (key: string, axis: Spec) => {
    const v = take(key);
    if (!isDict(v)) return;
    const at = list(v.ticks);
    const labels = list(v.labels);
    if (at.length > 0) axis.values = at;
    if (labels.length === at.length && labels.length > 0) {
      const cases = at.map((t, i) => `datum.value == ${JSON.stringify(t)} ? ${JSON.stringify(text(labels[i]) ?? '')} : `).join('');
      axis.labelExpr = `${cases}datum.label`;
    }
  };
  ticks('xticks', xAxis);
  ticks('yticks', yAxis);
  const grid = take('grid');
  if (grid !== undefined && bool(grid) !== false) {
    const look: Spec = { grid: true };
    if (isDict(grid)) {
      if (text(grid.color)) look.gridColor = text(grid.color);
      const style = dash(grid.linestyle);
      if (style) look.gridDash = style;
    }
    Object.assign(xAxis, look);
    Object.assign(yAxis, look);
  } else {
    config.axis = { grid: false };
  }
  const legend = take('legend');
  if (legend !== undefined) {
    const where = isDict(legend) ? text(legend.loc) : undefined;
    const orient: { [loc: string]: string } = {
      'upper left': 'top-left', 'upper right': 'top-right', 'lower left': 'bottom-left',
      'lower right': 'bottom-right', 'center left': 'left', 'center right': 'right'
    };
    config.legend = { orient: where && orient[where] ? orient[where] : 'right' };
  }
  const label = text(take('label'));
  if (label && !top.title) top.title = label;

  // Annotations: plt.annotate and plt.text/figtext, as text marks.
  const notes: Spec[] = [];
  const annotate = take('annotate');
  if (isDict(annotate) && text(annotate.text)) {
    const at = numbers(isDict(annotate) ? annotate.xytext ?? annotate.xy : []);
    if (at.length === 2)
      notes.push({ mark: { type: 'text', text: text(annotate.text), align: 'left', dx: 4 }, encoding: { x: { datum: at[0] }, y: { datum: at[1] } } });
  }
  const note = take('text');
  if (isDict(note) && text(note.s)) {
    const at = [num(note.x), num(note.y)];
    if (at[0] !== undefined && at[1] !== undefined)
      notes.push({ mark: { type: 'text', text: text(note.s), align: 'left' }, encoding: { x: { datum: at[0] }, y: { datum: at[1] } } });
  }

  // Axis settings go on the encodings of whatever drew the data.
  const encodings: Spec[] = [];
  const collect = (s: Spec) => {
    if (s.encoding) encodings.push(s.encoding as Spec);
    for (const layer of (s.layer as Spec[] | undefined) ?? []) collect(layer);
  };
  collect(top);
  for (const encoding of encodings) {
    for (const [channel, axis, title, scale, limit] of [
      ['x', xAxis, xTitle, xScale, xLim], ['y', yAxis, yTitle, yScale, yLim]
    ] as [string, Spec, Spec | null, Spec | undefined, number[] | undefined][]) {
      const e = encoding[channel] as Spec | undefined;
      if (!e || 'datum' in e) continue;
      if (Object.keys(axis).length > 0) e.axis = { ...(e.axis as Spec | undefined), ...axis };
      if (title) {
        e.title = title.title;
        if (title.titleColor) e.axis = { ...(e.axis as Spec | undefined), titleColor: title.titleColor };
      }
      if (scale || limit) e.scale = { ...(e.scale as Spec | undefined), ...scale, ...(limit ? { domain: limit } : {}) };
    }
  }
  if (notes.length > 0) {
    const drawn = top.layer ? (top.layer as Spec[]) : [{ mark: top.mark, encoding: top.encoding, ...(top.transform ? { transform: top.transform } : {}) }];
    if (!top.layer) { delete top.mark; delete top.encoding; delete top.transform; }
    top.layer = [...drawn, ...notes];
  }
  if (Object.keys(config).length > 0) top.config = config;
  top.width = width ?? 360;
  top.height = height ?? 240;

  const ignored = Object.keys(d).filter(key => !used.has(key));
  return { spec: top, ignored };
}
