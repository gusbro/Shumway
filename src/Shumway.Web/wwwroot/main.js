import { dotnet } from './_framework/dotnet.js'

const out = document.getElementById('out');
out.textContent = '';

const line = (text) => { out.textContent += text + '\n'; };

// Time the runtime boot separately from the engine boot: the first is .NET
// starting up in the browser, the second is Shumway consulting its prelude.
const t0 = performance.now();

const { setModuleImports, runMain } = await dotnet.create();

setModuleImports('main.js', { ui: { line } });

line(`runtime boot     : ${(performance.now() - t0).toFixed(0)} ms`);

const t1 = performance.now();
await runMain();
line('');
line(`total in-managed : ${(performance.now() - t1).toFixed(0)} ms`);

// Payload actually fetched, straight from the Resource Timing API — no guessing.
const bytes = performance.getEntriesByType('resource')
  .filter(r => r.name.includes('/_framework/'))
  .reduce((sum, r) => sum + (r.encodedBodySize || 0), 0);
line(`payload (_framework, encoded): ${(bytes / 1024 / 1024).toFixed(2)} MB`);
