// Builds the notebook site (ADR-073): the kernel extension, then
// `jupyter lite build`, then the runtime's JavaScript from the demo's
// sources and the isolation script in every page.
//
//   node jupyterlite/build.mjs --out <dir> [--with-demo <publish wwwroot>]
//
// Needs Node, and a Python with the packages of requirements.txt, found as
// `python` or named in the PYTHON environment variable. Nothing of the .NET
// solution runs this, and nothing here runs .NET: the demo is published
// apart (dotnet publish src/Shumway.Web -c Release).
//
// --with-demo makes <dir> a whole deployment, the demo's publish at its
// root and the notebook site under jupyter/, which is how the two are served
// and how they can be tried locally.

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repository = path.resolve(here, '..');
const extension = path.join(here, 'extension');
const site = path.join(here, 'site');
const runtimeScripts = path.join(repository, 'src', 'Shumway.Web', 'wwwroot');

const option = (name) => {
  const at = process.argv.indexOf(name);
  return at < 0 ? null : process.argv[at + 1];
};
const out = option('--out');
const demo = option('--with-demo');
if (!out) {
  console.error('usage: node jupyterlite/build.mjs --out <dir> [--with-demo <publish wwwroot>]');
  process.exit(2);
}
const python = process.env.PYTHON ?? 'python';

function run(command, args, cwd) {
  console.log(`> ${command} ${args.join(' ')}`);
  // npm and npx are command files on Windows, which only a shell runs.
  const viaShell = process.platform === 'win32' && /^np[mx]$/.test(command);
  const done = viaShell
    ? spawnSync([command, ...args].join(' '), { cwd, stdio: 'inherit', shell: true })
    : spawnSync(command, args, { cwd, stdio: 'inherit' });
  if (done.status !== 0) {
    console.error(`${command} failed (${done.status ?? done.error})`);
    process.exit(1);
  }
}

// --- the extension -------------------------------------------------------
if (!fs.existsSync(path.join(extension, 'node_modules'))) run('npm', ['ci', '--no-audit', '--no-fund'], extension);
run('npx', ['tsc'], extension);
run(python, ['-m', 'jupyter', 'labextension', 'build', '.'], extension);

// --- the site ------------------------------------------------------------
const deployment = path.resolve(out);
const notebooks = demo ? path.join(deployment, 'jupyter') : deployment;
fs.rmSync(deployment, { recursive: true, force: true });
if (demo) {
  fs.cpSync(path.resolve(demo), deployment, { recursive: true });
  // The demo's name for the notebook site, which its worker knows.
  if (!fs.existsSync(path.join(deployment, 'runtime.json')))
    throw new Error(`${demo} is not a publish of the demo: it has no runtime.json`);
}
// Without the task database a rebuild into a new directory copies nothing.
fs.rmSync(path.join(site, '.jupyterlite.doit.db'), { force: true });
run(python, ['-m', 'jupyter', 'lite', 'build',
  '--lite-dir', '.', '--output-dir', notebooks,
  // Only what this repository names, not what the environment has installed.
  '--ignore-sys-prefix',
  '--LiteBuildConfig.federated_extensions=../extension/labextension'], site);

// --- the runtime's JavaScript, from where the demo has it ----------------
const scripts = path.join(notebooks, 'shumway');
fs.mkdirSync(scripts, { recursive: true });
for (const file of ['isolate.js', 'session.js', 'threads.js', 'mirror.js', 'libraries.js'])
  fs.copyFileSync(path.join(runtimeScripts, file), path.join(scripts, file));

// --- isolation, before anything else in every page -----------------------
// The worker is the demo's, two directories up from the script (ADR-069).
const tag = '<script src="../shumway/isolate.js" data-worker="../../sw.js"></script>';
let pages = 0;
for (const entry of fs.readdirSync(notebooks, { withFileTypes: true })) {
  const page = path.join(notebooks, entry.name, 'index.html');
  if (!entry.isDirectory() || !fs.existsSync(page)) continue;
  const html = fs.readFileSync(page, 'utf8');
  if (!html.includes('<head>')) throw new Error(`${page} has no <head> to put the isolation script in`);
  fs.writeFileSync(page, html.replace('<head>', '<head>\n' + tag));
  pages++;
}
if (pages === 0) throw new Error('the site has no page: nothing would be isolated');
console.log(`notebook site in ${notebooks}: ${pages} pages isolated`);
