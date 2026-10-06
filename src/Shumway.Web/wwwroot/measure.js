// Measurement helpers for the browser hooks (docs/benchmarks/browser.md), and
// the generic #wasmprobe hook. Loaded on demand, like selftest.js: a user
// session never fetches it.
//
// Every hook reports by POSTing to /collect (WebShumwayServe.ps1 -Collect).

/** Posts one report line to the collecting server; never throws. */
export function mark(text) {
  try { fetch('/collect', { method: 'POST', body: text }); } catch { }
}

/**
 * Loads Scryer's library tree, served beside the page under scryerlib/ (never
 * vendored: it is regenerated per checkout), into a library collection.
 *
 * Throws when the tree is missing. A missing manifest fetches the 404 page,
 * which splits into one "file": the load then "succeeds", the engine's own
 * clpfd answers use_module(library(clpz)), and the run measures the wrong
 * library without saying so.
 */
export async function loadScryerLibrary(libraries, collection = 'scryer_lib') {
  const t0 = performance.now();
  const manifest = await (await fetch('scryerlib/manifest.txt')).text();
  const files = manifest.split('\n').map(x => x.trim()).filter(Boolean);
  if (files.length <= 1 || !files.every(f => f.endsWith('.pl')))
    throw new Error('scryerlib/ is missing or incomplete: regenerate it '
      + '(docs/benchmarks/browser.md, "Scryer\'s library")');
  await libraries.remove(collection);
  await libraries.create(collection, 'scryer');
  let bytes = 0;
  for (const f of files) {
    const text = await (await fetch('scryerlib/' + f)).text();
    bytes += text.length;
    await libraries.write(collection, f, text);
  }
  return { files: files.length, bytes, ms: performance.now() - t0 };
}

/**
 * Runs one goal to its first answer, bounded by a wall-clock budget: past it
 * the query is cancelled and left to settle, so the next start is clean.
 * An open query (more answers pending) is closed. `ok` is a success.
 */
export async function timedGoal(session, goal, seconds = 120) {
  const t = performance.now();
  const e0 = await session.start(goal);
  if (e0) return { ms: -1, ok: false, state: 'start error: ' + e0, text: '' };
  let timer = null;
  const answer = session.next(seconds).then(r => ({ tag: r.tag, text: r.text || '' }));
  const race = await Promise.race([
    answer,
    new Promise(res => { timer = setTimeout(() => res({ timeout: true }), seconds * 1000); }),
  ]);
  if (timer) clearTimeout(timer);
  const ms = performance.now() - t;
  if (race.timeout) {
    await session.cancel();
    const after = await Promise.race([
      answer,
      new Promise(res => setTimeout(() => res({ stuck: true }), 20000)),
    ]);
    return { ms, ok: false, text: '',
             state: after.stuck ? `WEDGED (>${seconds}s, cancel did not land)`
                                : `TIMEOUT >${seconds}s (cancelled)` };
  }
  if (race.tag === 's') await session.cancel();
  const ok = race.tag === 's' || race.tag === 'l';
  return { ms, ok, text: race.text,
           state: race.tag === 'e' ? 'error ' + race.text.slice(0, 300) : race.tag };
}

/**
 * Switches the tier (a jit_compile mode: 'off', 'all', ...). An eviction the
 * switch queues applies at the NEXT query setup, so one runs here.
 */
export async function setTierMode(session, mode) {
  const t = performance.now();
  const reply = await session.exports().JitCompileControl(mode);
  await session.start('true.'); await session.next(5);
  return { ms: performance.now() - t, reply };
}

/** The tier's counters since the last read; reading resets them. */
export async function readCounters(session) {
  const status = await session.exports().JitCompileControl('status');
  const line = (status.match(/chains=\S+ switches=\S+ hops=\S+ foreignExits=\S+ deopts=\S+ builtinExits=\S+ tailExits=\S+/) || [''])[0];
  const time = (status.match(/delegate .*; interp \S+ ms/) || [''])[0];
  return { status, line, time };
}

/** Diagnostic channels that arm, disarm and dump; and two that only dump. */
const TRACE_CHANNELS = ['trace', 'attrs', 'shapes', 'cells', 'builtins', 'commits'];
const DUMP_ONLY = ['live', 'seq'];

/**
 * Runs fn with the named diagnostic channels armed, then posts each dump.
 * They cost real time (a lock per builtin call, a walk per univ), so a timed
 * run must not carry them unless it asks.
 */
export async function withTraces(session, channels, label, fn) {
  const armed = channels.filter(c => TRACE_CHANNELS.includes(c));
  const unknown = channels.filter(c => !TRACE_CHANNELS.includes(c) && !DUMP_ONLY.includes(c));
  if (unknown.length > 0) mark(`${label}: unknown trace channel(s) ${unknown.join(',')}`);
  const control = (c) => session.exports().JitCompileControl(c);
  for (const c of armed) await control(`${c} on`);
  const result = await fn();
  for (const c of armed) await control(`${c} off`);
  for (const c of channels.filter(c => !unknown.includes(c))) {
    const dump = await control(`${c} dump`);
    mark(`${c.toUpperCase()} ${label} BEGIN\n${dump}\n${c.toUpperCase()} ${label} END`);
  }
  return result;
}

/**
 * #wasmprobe=<file>[&n=<N>][&rounds=<R>][&trace=<channel,...>][&budget=<s>]
 *
 * Measures the goals a probe file under probes/ declares, Tier-0 against the
 * wasm tier, ABBA within each round so drift cancels, each timed run preceded
 * by a discarded warm one (promotion happens at a dispatch threshold, so a
 * cold run measures the promotion). The file is Prolog, consulted as it is;
 * lines of this form, comments to Prolog, drive the hook:
 *
 *   %uses scryer                 load Scryer's library tree first
 *   %probe <name>: <goal>        one measured goal; {N} stands for n
 *
 * Reports, per goal, the best time of each tier, their ratio, and the tier's
 * counters for the timed run (chains, hops, deopts, exits) and where its time
 * went (inside the delegate, in the interpreter). With trace=, the
 * tier's timed runs carry those channels and post their dumps. With cps=ab,
 * each round also builds the program as continuation functions (ADR-061)
 * and times that form beside the other, ABBA; with cps=abc, both of their
 * grains (a function per entry point, and per partition). grain=lazy builds
 * a module per predicate as each crosses a dispatch threshold (threshold=,
 * default 2) instead of the program as one.
 */
export async function wasmProbe({ session, libraries, emit, hash }) {
  const eq = hash.indexOf('=');
  const params = new URLSearchParams(eq >= 0 ? hash.slice(eq + 1) : '');
  const [file] = (eq >= 0 ? hash.slice(eq + 1) : '').split('&');
  const n = Number(params.get('n') ?? 10000);
  const rounds = Number(params.get('rounds') ?? 1);
  const budget = Number(params.get('budget') ?? 300);
  const channels = (params.get('trace') ?? '').split(',').filter(Boolean);
  // getAll: the probe's name is the first key, and a probe named like an
  // option would shadow it.
  const cpsOpt = params.getAll('cps');
  const abcCps = cpsOpt.includes('abc');
  const abCps = abcCps || cpsOpt.includes('ab');
  const tierCmd = params.get('grain') === 'lazy' ? (params.get('threshold') ?? '2') : 'all';
  const label = `probe ${file}`;
  try {
    if (!/^[\w-]+$/.test(file ?? '')) throw new Error(`bad probe name '${file}'`);
    const source = await (await fetch(`probes/${file}.pl`)).text();
    const probes = [];
    let usesScryer = false;
    for (const line of source.split('\n')) {
      const p = /^%probe\s+([^:]+):\s*(.+?)\s*$/.exec(line);
      if (p) probes.push([p[1].trim(), p[2].replaceAll('{N}', String(n))]);
      if (/^%uses\s+scryer\s*$/.test(line)) usesScryer = true;
    }
    if (probes.length === 0) throw new Error(`probes/${file}.pl declares no %probe line`);
    emit(`--- ${label}: ${probes.length} goals, n=${n}, x${rounds} rounds ---\n`);
    mark(`${label}: start, ${probes.length} goals, n=${n}, rounds=${rounds}`);
    // The time split is made of clock reads; say what one costs here.
    const clock = (await session.exports().JitCompileControl('clock')).trim();
    mark(`${label}: ${clock}`);

    if (usesScryer) {
      const lib = await loadScryerLibrary(libraries, 'scryer_probe');
      mark(`${label}: scryer library ${lib.files} files, ${Math.round(lib.ms)}ms`);
    }
    const err = await session.consult(source);
    if (err) throw new Error('consult failed: ' + err);

    // A form change needs a rebuild, and only going off and back to all
    // makes one.
    const forms = { all: 'cps off', cps: 'cps on', cpsb: 'cps budget' };
    const enter = async (mode) => {
      if (!abCps) return setTierMode(session, mode === 'off' ? 'off' : tierCmd);
      await setTierMode(session, 'off');
      if (mode === 'off') return;
      await session.exports().JitCompileControl(forms[mode]);
      await setTierMode(session, tierCmd);
    };
    const tierModes = abcCps ? ['all', 'cps', 'cpsb'] : abCps ? ['all', 'cps'] : ['all'];
    const best = {}, ok = {}, counts = {}, times = {};
    for (let r = 0; r < rounds; r++) {
      const modes = abcCps ? ['off', 'all', 'cps', 'cpsb', 'cpsb', 'cps', 'all', 'off']
        : abCps ? ['off', 'all', 'cps', 'cps', 'all', 'off'] : ['off', 'all', 'all', 'off'];
      for (const mode of modes) {
        await enter(mode);
        for (const [name, goal] of probes) {
          await timedGoal(session, goal, budget);            // warm
          await readCounters(session);                       // reset
          const run = tierModes.includes(mode) && channels.length > 0
            ? await withTraces(session, channels, `${label} ${name}`,
                               () => timedGoal(session, goal, budget))
            : await timedGoal(session, goal, budget);
          const { line, time } = await readCounters(session);
          const key = `${name} ${mode}`;
          if (run.ok && (!(key in best) || run.ms < best[key])) {
            best[key] = run.ms;
            times[key] = time;
          }
          ok[key] = (ok[key] ?? true) && run.ok;
          if (tierModes.includes(mode)) counts[`${name} ${mode}`] = line;
          mark(`${label}: round ${r} ${mode.padEnd(3)} ${name} -> ${Math.round(run.ms)}ms `
             + `${run.state}  ${line}`);
        }
      }
    }

    const width = Math.max(...probes.map(([p]) => p.length));
    const rows = probes.map(([name]) => {
      const off = best[`${name} off`], all = best[`${name} all`];
      const ratio = off > 0 && all > 0 ? `x${(off / all).toFixed(2)}` : '?';
      const bad = tierModes.every((m) => ok[`${name} ${m}`]) && ok[`${name} off`] ? '' : '  FAILED';
      const col = (m) => {
        const t = best[`${name} ${m}`];
        return `  ${m} ${String(Math.round(t ?? -1)).padStart(7)}ms`
          + `  ${m}/tier1 ${all > 0 && t > 0 ? (t / all).toFixed(3) : '?'}`;
      };
      const cpsCol = tierModes.filter((m) => m !== 'all').map(col).join('');
      return `${name.padEnd(width)}  tier0 ${String(Math.round(off ?? -1)).padStart(7)}ms`
           + `  tier1 ${String(Math.round(all ?? -1)).padStart(7)}ms  ${ratio.padStart(6)}`
           + `${cpsCol}${bad}  ${counts[`${name} all`] ?? ''}`
           + tierModes.filter((m) => m !== 'all')
               .map((m) => `\n${''.padEnd(width)}  ${m}: ${counts[`${name} ${m}`] ?? ''}`).join('')
           // Where the tier's best run went: the delegate's four buckets and
           // the interpreter's own share; the rest of the wall is the query's
           // setup and its answer.
           + (times[`${name} all`] ? `\n${''.padEnd(width)}  ${times[`${name} all`].trim()}` : '');
    });
    const report = `${label} (n=${n}, ${tierCmd === 'all' ? 'batch' : `lazy at ${tierCmd}`}, best of ${rounds} ABBA rounds)\n${rows.join('\n')}\n${clock}\n`;
    emit(report);
    const pre = document.createElement('pre');
    pre.id = 'wasmprobe';
    pre.textContent = report;
    document.body.appendChild(pre);
    mark(report);
    mark(`${label}: done`);
  } catch (ex) {
    const text = `${label} CRASHED: ${ex && ex.stack ? ex.stack : ex}`;
    emit(text + '\n', 'error');
    mark(text);
  }
}
