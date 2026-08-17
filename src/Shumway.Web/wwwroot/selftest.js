// Drives the whole browser path without a human: consult, pull solutions one at
// a time, failure, a syntax error, engine output, completion. Loading the page
// with #selftest and reading the transcript is an end-to-end check of the
// DEPLOYED app — the part no xUnit test can reach, since it needs a browser.
//
// Loaded on demand, so a normal page never fetches it.
 
import * as settings from './settings.js';

/**
 * Whether a file survives a reload — the whole point of mirroring to OPFS, and
 * the one claim a single page load cannot check. Run the page twice against the
 * same browser profile: `#persist=write` leaves a marker, `#persist=check`
 * reports whether it came back.
 */
export async function persistProbe(workspace, emit, mode) {
  const marker = 'persist_marker.pl';
  if (mode === 'write') {
    await workspace.write(marker, 'marker(survived).\n');
    const ok = await workspace.persist();
    emit(`persist write: stored=${ok}${ok ? '' : ' reason=' + workspace.storageError()}\n`,
         ok ? 'note' : 'error');
    return;
  }
  const content = await workspace.read(marker);
  const ok = content === 'marker(survived).\n';
  emit(`persist check: ${ok ? 'ok   restored across reload' : 'FAIL not restored'}`
     + ` (${JSON.stringify(content)})\n`, ok ? 'note' : 'error');
  await workspace.remove(marker);
  await workspace.persist();
}

export async function run(session, emit, out, editor, workspace) {
  let failures = 0;

  const check = (name, got, want) => {
    const ok = got === want;
    if (!ok) failures++;
    emit(`${ok ? 'ok  ' : 'FAIL'} ${name}: ${JSON.stringify(got)}`
       + (ok ? '\n' : ` (wanted ${JSON.stringify(want)})\n`), ok ? 'note' : 'error');
  };

  /** Every solution of a query, joined — or "error: …". */
  const solutions = async (q) => {
    const acc = [];
    const err = await session.start(q);
    if (err) return 'error: ' + err;
    for (;;) {
      const { tag, text } = await session.next(80);
      if (tag === session.FAILED) return acc.join(' | ');
      if (tag === session.ERROR) return 'error: ' + text;
      acc.push(text);
      if (tag === session.LAST) return acc.join(' | ');
    }
  };

  emit('--- selftest ---\n', 'note');

  check('consult', await session.consult(
    'anc(X,Y) :- par(X,Y).  anc(X,Z) :- par(X,Y), anc(Y,Z).  par(a,b).  par(b,c).'), null);
  check('arithmetic', await solutions('X is 6*7.'), 'X = 42');
  check('backtracking', await solutions('member(X,[a,b,c]).'), 'X = a | X = b | X = c');
  check('consulted rules', await solutions('anc(a,X).'), 'X = b | X = c');
  check('failure', await solutions('fail.'), '');
  check('no variables', await solutions('atom(foo).'), 'true');
  check('value chaining', await solutions('X = Y, Y = shared.'), 'X = Y,\nY = shared');

  const syntax = await session.start('this is not( prolog.');
  check('syntax error is reported', typeof syntax === 'string' && syntax.includes('1:20'), true);

  // An undefined predicate must come back as a real ISO error, with the engine's
  // stack — this is the diagnostic the console REPL prints, shared verbatim.
  const undef = await solutions('no_such_predicate_xyz(1).');
  check('existence error', undef.includes('existence_error'), true);

  check('completion', (await session.complete('appen')).includes('append'), true);

  const before = out.textContent.length;
  await solutions('write(engine_output), nl.');
  check('engine output reaches the page',
        out.textContent.slice(before).includes('engine_output'), true);

  // Cancelling a search must stop it rather than run to completion.
  await session.start('between(1, 100000000, X), X > 99999999.');
  await session.cancel();
  check('cancel leaves no run', (await session.next(80)).tag, session.FAILED);

  // --- the editor's highlighting, over the real DOM -----------------------
  // ONE copy of the text: the element that gets coloured IS the element being
  // edited. So these are also checks that what is on screen is what the caret
  // is in — there is no second copy that could disagree.
  const program = document.getElementById('program');

  // setText rather than the input event: the scheduled repaint waits for a
  // frame, and a headless browser's virtual clock does not reliably deliver one.
  const paint = async (src) => { await editor.setText(src); return program; };

  const painted = await paint("foo(X) :- bar(X). % note\n");
  check('the editor holds the text exactly',
        editor.getText(), "foo(X) :- bar(X). % note\n");
  check('variables are coloured',
        [...painted.querySelectorAll('.tok-variable')].some(e => e.textContent === 'X'), true);
  check('comments are coloured',
        [...painted.querySelectorAll('.tok-comment')].some(e => e.textContent === '% note'), true);
  check('operators are coloured',
        [...painted.querySelectorAll('.tok-operator')].some(e => e.textContent === ':-'), true);

  await paint("p('unterminated");
  check('half-typed text still reproduces', editor.getText(), "p('unterminated");

  // A program-declared operator must colour as one once consulted — the payoff
  // of asking the live table instead of a fixed pattern list.
  await paint('X #= Y.');
  const beforeOp = [...program.querySelectorAll('.tok-operator')].some(e => e.textContent === '#=');
  await session.consult(':- op(700, xfx, #=).');
  await paint('X #= Y.');
  const afterOp = [...program.querySelectorAll('.tok-operator')].some(e => e.textContent === '#=');
  check('program-declared operator colours after consult', [beforeOp, afterOp].join(), 'false,true');

  // Editing where the caret is: put text in, place the caret inside it, and
  // check that what the DOM reports back is the same offset in the same text.
  // With two copies this is exactly what came apart.
  await paint('abc(def).');
  const range = document.createRange();
  const walk = document.createTreeWalker(program, NodeFilter.SHOW_TEXT);
  let node, seen = 0, target = null, targetOffset = 0;
  while ((node = walk.nextNode())) {
    if (seen + node.data.length >= 5) { target = node; targetOffset = 5 - seen; break; }
    seen += node.data.length;
  }
  range.setStart(target, targetOffset);
  range.collapse(true);
  const sel = document.getSelection();
  sel.removeAllRanges();
  sel.addRange(range);
  const probe = document.createRange();
  probe.selectNodeContents(program);
  probe.setEnd(target, targetOffset);
  check('the caret is where the text says it is', probe.toString(), 'abc(d');

  await editor.setText('');

  // A query left waiting for its next solution does not survive a load: the
  // program it was asked of is being replaced.
  await session.start('member(X, [a,b,c]).');
  check('a query is open', (await session.next(80)).tag, session.SOLUTION);
  await session.consult('unrelated_fact(1).');
  check('loading a program ended it', (await session.next(80)).tag, session.FAILED);

  // --- the workspace, and that Prolog can actually see it -----------------
  await workspace.write('selftest_data.pl', 'from_a_file(yes).\n');
  check('file appears in the workspace', (await workspace.list()).includes('selftest_data.pl'), true);
  check('file reads back', await workspace.read('selftest_data.pl'), 'from_a_file(yes).\n');
  check('consulting a workspace file', await workspace.consultFile('selftest_data.pl'), null);
  check('its clauses are solvable', await solutions('from_a_file(X).'), 'X = yes');

  // Prolog's own file I/O writes into the same workspace — the point of putting
  // it on a real filesystem rather than a bag of strings.
  await solutions("open('written_by_prolog.txt', write, S), write(S, hello), close(S).");
  check('open/4 wrote into the workspace',
        await workspace.read('written_by_prolog.txt'), 'hello');

  check('delete removes it', await workspace.remove('written_by_prolog.txt'), null);
  check('and it is gone', (await workspace.list()).includes('written_by_prolog.txt'), false);

  // --- workspaces ---------------------------------------------------------
  // A workspace is a directory, so the claim worth checking is separation: one
  // workspace's files are not the other's, and deleting one takes its files.
  const home = workspace.active();
  const ws = 'selftest_ws';
  check('creating a workspace', await workspace.create(ws), null);
  check('it is listed', (await workspace.names()).includes(ws), true);

  check('switching to it', await workspace.setActive(ws), null);
  check('the other workspace\'s files are not here',
        (await workspace.list()).includes('selftest_data.pl'), false);
  await workspace.write('only_here.pl', 'here(yes).\n');

  // Prolog resolves relative paths against the ACTIVE workspace, so a consult
  // from here must find this file and not one of the same name elsewhere.
  check('consult resolves inside it', await workspace.consultFile('only_here.pl'), null);
  check('and it ran', await solutions('here(X).'), 'X = yes');

  check('back to where we were', await workspace.setActive(home), null);
  check('and its files are still there',
        (await workspace.list()).includes('selftest_data.pl'), true);
  check('the other workspace\'s file is not', (await workspace.list()).includes('only_here.pl'), false);

  check('deleting the workspace', await workspace.removeWorkspace(ws), null);
  check('it is gone', (await workspace.names()).includes(ws), false);
  check('the active one cannot be deleted',
        typeof (await workspace.removeWorkspace(home)), 'string');

  await workspace.remove('selftest_data.pl');

  // --- exporting ----------------------------------------------------------
  // The zip is built by the engine (it owns the filesystem) and crosses as
  // base64. Checking the header is enough to know it is a zip and not a
  // stringified something.
  await workspace.write('zipped.pl', 'in_the_zip(yes).\n');
  const zipped = await session.exports().WorkspaceZip();
  const zipBytes = atob(zipped);
  check('the export is a zip', zipBytes.slice(0, 2), 'PK');
  check('and it holds the file', zipBytes.includes('zipped.pl'), true);
  await workspace.remove('zipped.pl');

  // --- settings -----------------------------------------------------------
  // An envelope of another version is discarded rather than half-read. Done on
  // a copy: the real preferences are put back afterwards.
  const saved = localStorage.getItem('shumway.settings');
  try {
    // The stored theme is 'light' PRECISELY because the default is 'dark':
    // discarding and keeping must be distinguishable by the value that
    // survives, or the check proves nothing.
    localStorage.setItem('shumway.settings', JSON.stringify({ v: 0, theme: 'light' }));
    const fresh = settings.load();
    check('settings of another version are discarded', settings.wasDiscarded(), true);
    check('and the defaults are used', fresh.theme, 'dark');
    localStorage.setItem('shumway.settings', JSON.stringify({ v: settings.SETTINGS_VERSION, theme: 'light' }));
    check('a current envelope is kept', settings.load().theme, 'light');
    check('and nothing was discarded', settings.wasDiscarded(), false);
  } finally {
    if (saved === null) localStorage.removeItem('shumway.settings');
    else localStorage.setItem('shumway.settings', saved);
    settings.load();
  }

  // The mirror, both ways, through real storage: write a file, push it to OPFS,
  // erase it from the engine's filesystem, and pull it back. That is exactly
  // what a reload does, minus the reload — which a headless browser cannot be
  // made to perform reliably.
  if (workspace.persistent()) {
    await workspace.write('mirror_probe.pl', 'mirrored(yes).\n');
    const stored = await workspace.persist();
    if (!stored) {
      // A headless browser advertises origin-private storage and then never
      // answers. That is the harness, not the app — reported, not counted as a
      // failure, because the app's own behaviour (degrade, say so, carry on) is
      // exactly what happens here.
      emit(`note: storage unusable here — ${workspace.storageError()};`
         + ` mirror unverified in this environment\n`, 'note');
      await workspace.remove('mirror_probe.pl');
    } else {
      await workspace.remove('mirror_probe.pl');
      check('mirror: gone from memory', await workspace.read('mirror_probe.pl'), null);
      await workspace.restoreAll();
      check('mirror: restored from storage',
            await workspace.read('mirror_probe.pl'), 'mirrored(yes).\n');
      await workspace.remove('mirror_probe.pl');
      await workspace.persist();
    }
  }

  // --- sharing ------------------------------------------------------------
  const shared = 'p(1).\np(2).  % a comment with | and \\n in it\n';
  const packed = await session.shareFile('shared.pl', shared, 'p(X).');
  const unpacked = await session.shareDecode(packed);
  check('a file share says so', unpacked && unpacked.kind, 'file');
  check('it is labelled', unpacked && unpacked.label, 'shared.pl');
  check('share round-trips the program', unpacked && unpacked.files[0].text, shared);
  check('share round-trips the name', unpacked && unpacked.files[0].name, 'shared.pl');
  check('share round-trips the query', unpacked && unpacked.query, 'p(X).');
  check('share is url-safe', encodeURIComponent(packed), packed);
  check('a mangled link is rejected', await session.shareDecode('not a share'), null);

  // A workspace share carries every file in it, and says which workspace it was.
  await workspace.write('shared_a.pl', 'a(1).\n');
  await workspace.write('shared_b.pl', 'b(2).\n');
  const wsPacked = await session.shareWorkspace('a(X).');
  const wsShare = await session.shareDecode(wsPacked);
  check('a workspace share says so', wsShare && wsShare.kind, 'workspace');
  check('labelled with the workspace', wsShare && wsShare.label, workspace.active());
  const names = wsShare ? wsShare.files.map((f) => f.name) : [];
  check('it carries the files', names.includes('shared_a.pl') && names.includes('shared_b.pl'), true);
  await workspace.remove('shared_a.pl');
  await workspace.remove('shared_b.pl');

  // --- examples -------------------------------------------------------------
  // Every example must at least parse and load; one that does not is worse than
  // no example. (Their queries are exercised on the desktop REPL, where a wrong
  // answer is visible; here the point is that the files ship and consult.)
  for (const name of ['family.pl', 'queens.pl', 'zebra.pl', 'dcg.pl', 'tabling.pl', 'clpfd.pl']) {
    const source = await (await fetch('examples/' + name)).text();
    check(`example ${name} is served`, source.length > 0, true);
  }
  // A fresh engine per example is not available here, so two are consulted into
  // this one: the plain case, and the one that needs a library.
  check('example family.pl consults',
        await session.consult(await (await fetch('examples/family.pl')).text()), null);
  check('and answers', await solutions('ancestor(ana, W), W == beto.'), 'W = beto');

  // The library-dependent example names its library in a directive of its own,
  // so consulting it is enough — it does not depend on having arrived here by
  // a particular route. It used to, and came back from storage unloadable.
  check('example clpfd.pl consults',
        await session.consult(await (await fetch('examples/clpfd.pl')).text()), null);
  check('and its constraints hold', await solutions('X #> 3, X #< 7.'), 'X in 4..6');

  // --- debug (spike) --------------------------------------------------------
  // The whole loop, without a human: enable → consult debuggable → breakpoint
  // → run → the stop event arrives while the query's promise stays pending →
  // frames carry variables → resume → the query answers. Last, because enable
  // restarts the engine (debuggability is decided at compile time).
  {
    check('debug enable', await session.debugEnable(), null);
    // 1: dbg_run(Out) :-   2: dbg_mark(X),   3: Out = X.   4: dbg_mark(1).
    check('debug consult', await session.consult(
      'dbg_run(Out) :-\n    dbg_mark(X),\n    Out = X.\ndbg_mark(1).\n'), null);
    // Line 3: at that goal dbg_mark has already exited, so X is 1 — the stop
    // can prove the frame carries VALUES, not just names.
    check('breakpoint binds', await session.debugBreakpoint('<string>', 3, true), null);

    let stop = null;
    const stopped = new Promise((resolve) => {
      window.shumwayDebug.onStop = (s) => { stop = s; resolve('stopped'); };
    });
    const answer = solutions('dbg_run(Out).');   // deliberately NOT awaited yet
    const arrived = await Promise.race([
      stopped, new Promise((r) => setTimeout(() => r('timeout'), 15000))]);
    window.shumwayDebug.onStop = null;

    check('the stop event arrives', arrived, 'stopped');
    if (stop) {
      check('stopped at the breakpoint', `${stop.reason} ${stop.file}:${stop.line}`,
            'breakpoint <string>:3');
      const top = stop.frames.find((f) => f.name === 'dbg_run');
      check('the caller is on the stack', top ? `${top.name}/${top.arity}` : '(missing)',
            'dbg_run/1');
      check('its variables are visible',
            top && top.vars.some((v) => v.value === '1'), true);
      // The Immediate window, engine-side: a goal naming a frame variable
      // evaluates against the frame's CURRENT value (X is 1 here).
      check('evaluate against the frame', await session.debugEvaluate(0, 'X =:= 1.'), 'true');
      check('evaluate binds a copy', await session.debugEvaluate(0, 'Y = X.'), 'Y = 1');
      check('frames re-capture while stopped',
            (await session.debugFrames())?.frames.length > 0, true);
      check('resume wakes the search', await session.debugResume('continue'), true);
      check('and the query answers', await answer, 'Out = 1');
    }
    // A fresh, non-debug engine for whatever runs after this file. Cancel
    // first: it is ungated and wakes a stop, so even a query left stopped by
    // a failed check cannot leave the engine gate held against the reset.
    await session.cancel();
    await session.resetEngine();
  }

  // --- debug view docks -----------------------------------------------------
  // Pure page behaviour, no engine involved: the ⇄ button re-homes a view's
  // BUTTON and PANEL into the other dock, an emptied dock collapses (.empty),
  // and sending the view back restores the single-dock default.
  {
    const left = document.getElementById('debug-tabs-left');
    const right = document.getElementById('debug-tabs');
    check('left dock starts empty', left.classList.contains('empty'), true);
    right.querySelector('[data-tab="tab-locals"]').click();       // make Locals current
    right.querySelector('.tab-move').click();                     // send it left
    check('locals moved to the left dock', !!left.querySelector('#tab-locals'), true);
    check('left dock now shows', left.classList.contains('empty'), false);
    check('locals panel is visible there',
          left.querySelector('#tab-locals').hidden, false);
    check('the stack stays on the right', !!right.querySelector('#tab-stack'), true);
    left.querySelector('.tab-move').click();                      // send it back
    check('left dock collapses again', left.classList.contains('empty'), true);
    check('locals is home again', !!right.querySelector('#tab-locals'), true);
  }

  // --- resizable seams ------------------------------------------------------
  // Synthetic pointer drags against the real handlers: the pane splitter and a
  // dock handle resize by dragging, the shares persist (settings.layout), and
  // a double-click gives the default back. Geometry under a REAL pointer is
  // the visual harness's job; this checks the wiring.
  {
    const drag = (el, from, to) => {
      const ev = (type, p) => new PointerEvent(type,
        { bubbles: true, pointerId: 1, clientX: p.x, clientY: p.y });
      el.dispatchEvent(ev('pointerdown', from));
      el.dispatchEvent(ev('pointermove', to));
      el.dispatchEvent(ev('pointerup', to));
    };
    const panes = document.querySelector('.panes');
    const splitter = document.getElementById('split-panes');
    const r = panes.getBoundingClientRect();
    drag(splitter, { x: r.left + r.width * 0.5, y: r.top + 50 },
                   { x: r.left + r.width * 0.65, y: r.top + 50 });
    check('dragging the splitter sets the split',
          panes.style.getPropertyValue('--split') !== '', true);
    check('and the split persists', typeof settings.get().layout?.split, 'number');
    splitter.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    check('double-click restores the default split',
          panes.style.getPropertyValue('--split'), '');
    check('and clears what was stored', settings.get().layout?.split ?? null, null);

    const dock = document.getElementById('debug-tabs');
    const handle = dock.querySelector('.dock-resize');
    const d = dock.getBoundingClientRect();
    drag(handle, { x: d.left + 40, y: d.top }, { x: d.left + 40, y: d.top - 80 });
    check('dragging a dock handle sets its height', dock.style.flexBasis !== '', true);
    check('and the height persists', typeof settings.get().layout?.rdock, 'number');
    handle.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    check('double-click restores the dock default', dock.style.flexBasis, '');
  }

  // Persistence is reported rather than assumed: a browser may refuse storage,
  // and the session must still work when it does.
  emit(`note: origin-private storage ${workspace.persistent() ? 'available' : 'UNAVAILABLE'}`
     + `, file pickers ${workspace.canPickFiles() ? 'available' : 'unavailable (download fallback)'}\n`,
       'note');

  emit(`--- selftest ${failures === 0 ? 'passed' : failures + ' FAILED'} ---\n`,
       failures === 0 ? 'note' : 'error');
}
