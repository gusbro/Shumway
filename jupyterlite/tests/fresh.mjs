// A site published again is taken by a browser that was there before: a
// file whose name stays the same across publishes is asked of the server
// each time, though the server lets the browser keep it for ten minutes, as
// GitHub Pages does; a file whose name changes with its contents is asked
// once and served from the page's own cache after.
//
// Without the first, a page ran for minutes the extension of one publish
// with the runtime of the next: its configuration, which names the
// extension, was the browser's copy, and the runtime was the new one.
//
//   node fresh.mjs <deployment dir> [chromium|firefox|webkit]
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';
import { guarded, hung, MINUTE, watch } from './watch.mjs';

const engineName = process.argv[3] ?? 'chromium';

// Two files of the site that say which publish they are of, each time they
// are asked for. The browser may keep either for ten minutes.
let publish = 0;
const published = () => ({
  body: JSON.stringify({ publish: ++publish }),
  headers: {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'max-age=600',
    ETag: `"publish-${publish}"`
  }
});
const KEPT_NAME = '/jupyter/published.json';
const NAMED_BY_CONTENTS = '/jupyter/published.0123456789abcdef.json';
const server = await serve(process.argv[2], null, { [KEPT_NAME]: published, [NAMED_BY_CONTENTS]: published });

const browser = await { webkit, chromium, firefox }[engineName].launch();
const page = await (await browser.newContext()).newPage();
const { diagnose } = watch(page, engineName);

let failures = 0;
const check = (ok, what, detail) => {
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} [${engineName}] ${what}${ok ? '' : ' ' + JSON.stringify(detail).slice(0, 900)}`);
};

try {
  await page.goto(server.url + 'jupyter/lab/index.html');
  // Isolated: the page is one the service worker serves.
  await page.waitForFunction(() => window.crossOriginIsolated === true && !!navigator.serviceWorker.controller,
    null, { timeout: 180000, polling: 500 });

  const asked = await guarded(page.evaluate(async ({ kept, named }) => {
    const ask = async (path) => (await (await fetch(path)).json()).publish;
    return {
      kept: [await ask(kept), await ask(kept), await ask(kept)],
      named: [await ask(named), await ask(named), await ask(named)]
    };
  }, { kept: KEPT_NAME, named: NAMED_BY_CONTENTS }), 2 * MINUTE, 'asking for the files');

  check(asked.kept[0] < asked.kept[1] && asked.kept[1] < asked.kept[2],
    'a file whose name stays is asked of the server each time, whatever the browser may keep', asked);
  check(asked.named[0] === asked.named[1] && asked.named[1] === asked.named[2],
    'a file named by its contents is asked once', asked);
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${e.message.split('\n')[0]}`);
  if (hung(e)) await diagnose();
}
console.log(`[${engineName}] ${failures} failed`);
await guarded(browser.close(), MINUTE, 'closing the browser').catch((e) => console.log(e.message));
server.close();
process.exit(failures ? 1 : 0);
