// More solutions from the answer (ADR-074): under the answer of a query
// that can give another solution are a top level's choices, Next, 5, All
// and Stop. The solutions asked for are shown together under the query's
// cell and saved with the notebook; only the latest query alive can be
// asked; Stop ends an All over a query with solutions without end; a cell
// run again ends the query its run before left; `;` in command mode asks
// the next solution; and an answer whose session ended has no buttons.
//
//   node answers.mjs <deployment dir> [chromium|firefox|webkit]
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';
import { guarded, hung, MINUTE, watch } from './watch.mjs';

const engineName = process.argv[3] ?? 'chromium';
const server = await serve(process.argv[2]);
const browser = await { webkit, chromium, firefox }[engineName].launch();
const page = await (await browser.newContext()).newPage();
const { diagnose } = watch(page, engineName);

let failures = 0;
const check = (ok, what, detail) => {
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} [${engineName}] ${what}${ok ? '' : ' ' + JSON.stringify(detail).slice(0, 900)}`);
};

const CELLS = [
  'between(1, 12, X).',
  'member(Y, [p, q]).',
  'between(1, 100000000, Z).',
  'between(1, 3, W).',
  'retry.',
  '( Q = 1 ; repeat, fail ).'
];
const OFF = ['Next(off)', '5(off)', 'All(off)', 'Stop(off)'];
const ON = ['Next', '5', 'All', 'Stop'];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** Runs a cell of the notebook as the Run button does. */
const runCell = (index) => guarded(page.evaluate(async (index) => {
  console.log(`[answers.mjs] running cell ${index}`);
  const notebook = window.answersPanel.content;
  notebook.activeCellIndex = index;
  await window.jupyterapp.commands.execute('notebook:run-cell');
}, index), 2 * MINUTE, `running cell ${index}`);

/** Presses a button of the answer that has them in a cell. */
const press = (index, choice) => guarded(page.evaluate(({ index, choice }) => {
  console.log(`[answers.mjs] pressing ${choice} in cell ${index}`);
  const buttons = window.answersPanel.content.widgets[index].node
    .querySelectorAll(`.shumway-answer-choice[data-choice="${choice}"]`);
  if (buttons.length === 0) throw new Error(`cell ${index} has no ${choice} button`);
  buttons[buttons.length - 1].click();
}, { index, choice }), MINUTE, `pressing ${choice} in cell ${index}`);

/** The kernel has nothing more to do, and the page has drawn what it sent. */
const settled = async () => {
  await guarded(page.waitForFunction(
    () => window.answersPanel.sessionContext.session?.kernel?.status === 'idle', null, { timeout: 90000, polling: 100 }),
  2 * MINUTE, 'the kernel to be idle');
  await page.waitForTimeout(300);
};

/** Each cell: the texts of its outputs as the page shows them, its buttons,
 *  and how many outputs its model has. */
const cells = () => page.evaluate(() => window.answersPanel.content.widgets.map((cell) => ({
  texts: [...cell.node.querySelectorAll('.jp-OutputArea-output')]
    .map((node) => (node.querySelector('.shumway-answer-text') ?? node).textContent.trim()),
  buttons: [...cell.node.querySelectorAll('.shumway-answer-choice')].map((b) => b.textContent + (b.disabled ? '(off)' : '')),
  outputs: cell.model.outputs.length
})));

try {
  await page.goto(server.url + 'jupyter/lab/index.html');
  await page.waitForFunction(() => window.crossOriginIsolated === true
      && !!window.jupyterapp?.serviceManager?.contents
      && !!document.querySelector('#jp-main-dock-panel, .jp-LabShell'),
    null, { timeout: 180000, polling: 500 });
  await page.evaluate(() => window.jupyterapp.serviceManager.ready);

  await guarded(page.evaluate(async (cells) => {
    const app = window.jupyterapp;
    await app.serviceManager.contents.save('answers.ipynb', { type: 'notebook', format: 'json', content: {
      cells: cells.map((source) => ({ cell_type: 'code', source, metadata: {}, outputs: [], execution_count: null })),
      nbformat: 4, nbformat_minor: 5,
      metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
    const panel = await app.commands.execute('docmanager:open', { path: 'answers.ipynb' });
    await panel.context.ready;
    await panel.sessionContext.ready;
    window.answersPanel = panel;
  }, CELLS), 4 * MINUTE, 'opening the notebook');

  await runCell(0); await settled();
  let now = await cells();
  check(same(now[0].texts, ['X = 1 ;']) && same(now[0].buttons, ON),
    'the answer of a query that left choice points has Next, 5, All and Stop under it', now[0]);

  await press(0, 'next'); await settled();
  now = await cells();
  check(same(now[0].texts, ['X = 1 ;', 'X = 2 ;']) && same(now[0].buttons, ON),
    'Next shows the next solution under the same cell, and the buttons move under it', now[0]);

  await press(0, 'five'); await settled();
  const afterFive = (await cells())[0];
  await press(0, 'five'); await settled();
  now = await cells();
  check(afterFive.outputs === 5 && now[0].outputs === 10 && now[0].texts.at(-1) === 'X = 10 ;',
    '5 goes on to the next multiple of five: three after two, then five', [afterFive, now[0]]);

  await runCell(1); await settled();
  now = await cells();
  check(same(now[0].buttons, OFF) && same(now[1].buttons, ON) && same(now[1].texts, ['Y = p ;']),
    'only the latest query alive can be asked: the buttons of the one before are disabled', [now[0].buttons, now[1]]);

  await press(1, 'stop'); await settled();
  now = await cells();
  check(same(now[1].texts, ['Y = p.']) && same(now[1].buttons, []) && same(now[0].buttons, ON),
    'Stop ends the query: its answer ends with a full stop, and the one before can be asked again', [now[1], now[0].buttons]);

  await press(0, 'all'); await settled();
  now = await cells();
  check(now[0].outputs === 12 && now[0].texts.at(-1) === 'X = 12.' && same(now[0].buttons, []),
    'All shows every remaining solution, and the last one ends the buttons', now[0]);

  // A query with solutions without end: All runs until Stop, which ends it
  // between two requests and leaves the session as it was.
  await runCell(2); await settled();
  await press(2, 'all');
  await guarded(page.waitForFunction(
    () => window.answersPanel.content.widgets[2].model.outputs.length >= 60, null, { timeout: 90000, polling: 100 }),
  2 * MINUTE, 'All to show some tens of solutions');
  const running = (await cells())[2];
  check(same(running.buttons, ['Next(off)', '5(off)', 'All(off)', 'Stop']),
    'while All runs only Stop can be pressed, where the page still draws the outputs', running.buttons);
  await press(2, 'stop'); await settled();
  const stoppedAt = (await cells())[2].outputs;
  await page.waitForTimeout(1500);
  now = await cells();
  const lastOfStopped = await page.evaluate(() => {
    const outputs = window.answersPanel.content.widgets[2].model.outputs;
    return String(outputs.get(outputs.length - 1).toJSON().data['text/plain']).replace(/\x1b\[[0-9;]*m/g, '');
  });
  check(now[2].outputs === stoppedAt && same(now[2].buttons, []) && /^Z = \d+\.$/.test(lastOfStopped),
    'Stop ends an All over a query with solutions without end, and its last answer ends with a full stop',
    { stoppedAt, after: now[2].outputs, buttons: now[2].buttons, lastOfStopped });

  // The session is as it was: a query runs. And a cell run again ends the
  // query its run before left alive: with the new one out of solutions, no
  // retry finds a query. Two of them: the first answers for the query that
  // ran out, as the reference's does, and it is the second that would reach
  // an older query and show its second solution.
  await runCell(3); await settled();
  await runCell(3); await settled();
  await press(3, 'all'); await settled();
  now = await cells();
  const retried = await page.evaluate(async () => {
    const answers = [];
    for (let i = 0; i < 2; i++) {
      const outputs = [];
      const future = window.answersPanel.sessionContext.session.kernel.requestExecute({ code: 'retry.', store_history: false });
      future.onIOPub = (msg) => { if (msg.header.msg_type === 'display_data') outputs.push(String(msg.content.data['text/plain'])); };
      const reply = await future.done;
      answers.push({ status: reply.content.status, texts: outputs.map((text) => text.replace(/\x1b\[[0-9;]*m/g, '')) });
    }
    return answers;
  });
  check(same(now[3].texts, ['W = 1 ;', 'W = 2 ;', 'W = 3.'])
      && retried.every((answer) => answer.status === 'error' && !answer.texts.some((text) => /W = /.test(text))),
    'a cell run again ends the query its run before left: no retry finds a query of it', { cell: now[3], retried });

  // A cell that is `retry.` shows the solution under itself, and the buttons
  // go with the latest solution: Next there adds to that cell.
  await runCell(0); await settled();
  await runCell(4); await settled();
  await press(4, 'next'); await settled();
  now = await cells();
  check(same(now[0].texts, ['X = 1 ;']) && same(now[0].buttons, [])
      && same(now[4].texts, ['X = 2 ;', 'X = 3 ;']) && same(now[4].buttons, ON),
    'retry. still gives the next solution, under its own cell, and the buttons go there', [now[0], now[4]]);

  // `;` in command mode asks the next solution of the active cell's answer.
  await runCell(0); await settled();
  await page.evaluate(() => {
    const notebook = window.answersPanel.content;
    notebook.activeCellIndex = 0;
    notebook.mode = 'command';
    notebook.node.focus();
  });
  await page.keyboard.press(';'); await settled();
  now = await cells();
  check(same(now[0].texts, ['X = 1 ;', 'X = 2 ;']) && same(now[0].buttons, ON),
    '; in command mode asks the next solution', now[0]);

  // The solutions are saved with the notebook, and after a restart the
  // answers are text with no buttons.
  const saved = await guarded(page.evaluate(async () => {
    await window.answersPanel.context.save();
    const file = await window.jupyterapp.serviceManager.contents.get('answers.ipynb', { content: true });
    return file.content.cells[0].outputs.map((o) => String(o.data['text/plain']).replace(/\x1b\[[0-9;]*m/g, ''));
  }), 2 * MINUTE, 'saving the notebook');
  check(same(saved, ['X = 1 ;', 'X = 2 ;']), 'the solutions asked for are saved with the notebook', saved);

  // A next solution that never comes: Stop cannot end it between two
  // solutions, the answer says so after a while, and an interrupt ends it
  // with the session.
  await runCell(5); await settled();
  await press(5, 'five');
  await page.waitForTimeout(500);
  await press(5, 'stop');
  const late = await guarded(page.waitForFunction(
    () => window.answersPanel.content.widgets[5].node.querySelector('.shumway-answer-late')?.textContent ?? null,
    null, { timeout: 30000, polling: 200 }), MINUTE, 'the answer to say the solutions have not come');
  const waiting = (await cells())[5];
  await guarded(page.evaluate(() => window.answersPanel.sessionContext.session.kernel.interrupt()), MINUTE, 'interrupting');
  await settled();
  now = await cells();
  check(/Interrupt/.test(await late.jsonValue()) && same(waiting.buttons, ['Next(off)', '5(off)', 'All(off)', 'Stopping…(off)'])
      && now[5].texts.some((text) => /Interrupted/.test(text)) && now.every((cell) => cell.buttons.length === 0),
    'a solution that never comes: Stop waits and the answer says only an interrupt ends it, which ends the session',
    { waiting, after: now[5] });

  await guarded(page.evaluate(() => window.answersPanel.sessionContext.session.kernel.restart()), 4 * MINUTE, 'restarting');
  await settled();
  now = await cells();
  check(same(now[0].texts, ['X = 1 ;', 'X = 2 ;']) && now.every((cell) => cell.buttons.length === 0),
    'after a restart the answers are there, with no buttons', now.map((cell) => cell.buttons));
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${e.message.split('\n')[0]}`);
  if (hung(e)) await diagnose();
}
console.log(`[${engineName}] ${failures} failed`);
await guarded(browser.close(), MINUTE, 'closing the browser').catch((e) => console.log(e.message));
server.close();
process.exit(failures ? 1 : 0);
