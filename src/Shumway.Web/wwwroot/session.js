// The async seam.
//
// The runtime does not live on this thread — the app is built with threads, so
// .NET runs on its own worker and every call to it is a message and a reply.
// That is what keeps the page drawable while a search runs. This module is the
// facade the UI talks to; nothing above it needs to know where the engine is.

// The runtime is imported when it is STARTED, not when this module is loaded.
// A static import is evaluated before the body of everything that imports it,
// which put the runtime ahead of the page's own startup: on a page that is not
// yet cross-origin isolated it asserted about SharedArrayBuffer before a single
// line of main.js had run — no message, no theme, nothing to explain it.

/** Reply tags from queryNext, matching WebShumwayApp. */
export const SOLUTION = 's';
export const LAST = 'l';
export const FAILED = 'f';
export const ERROR = 'e';
export const ABORTED = 'a';

let engine = null;
let wasmModule = null;
let poolSize = 0;

/** The size of the runtime's linear memory, for the measurement hooks. */
export const wasmMemoryBytes = () => wasmModule?.HEAPU8?.byteLength ?? 0;

/** How many threads the runtime was asked to make at boot. */
export const threadPoolSize = () => poolSize;

let sessionAskForInput = null;

/** Who is told that a notebook session wants a line: (kernelId) => void. */
export const onSessionAskForInput = (handler) => { sessionAskForInput = handler; };

let sessionImportLibrary = null;

/** Who imports a collection for a notebook session's cell:
 *  (kernelId, request) => void, answering through SessionSupplyImport. */
export const onSessionImportLibrary = (handler) => { sessionImportLibrary = handler; };

/**
 * Starts the runtime, with no engine in it, and resolves to its exports. A
 * notebook page stops here: its engines are its kernels' sessions (ADR-068).
 *
 * @param {object} [options]
 * @param {string} [options.entry] the URL of the runtime's `dotnet.js`. A page
 *        of this site leaves it out. A page of another directory has no import
 *        map that knows the published name, and reads it from `runtime.json`.
 * @param {number} [options.threads] how many threads the runtime makes at
 *        boot, when the page will hold more sessions than the default allows
 *        to start at once.
 * @param {object} [options.ui] what the page's own session calls: `write`,
 *        `writeError`, `writeNote`, `askForInput`, `debugStopped`. One left
 *        out does nothing: an undefined import would throw at its first call.
 */
export async function startRuntime({ entry = './_framework/dotnet.js', threads = 0, ui = {} } = {}) {
  // The runtime asserts about SharedArrayBuffer and says nothing a reader of
  // the page can act on.
  if (!globalThis.crossOriginIsolated)
    throw new Error('the page is not cross-origin isolated');
  const { dotnet } = await import(entry);
  const runtime = await (threads > 0
    ? dotnet.withConfig({ pthreadPoolInitialSize: threads }) : dotnet).create();
  const { setModuleImports, getConfig, getAssemblyExports, runMain } = runtime;
  wasmModule = runtime.Module;
  poolSize = getConfig().pthreadPoolInitialSize ?? 0;
  const nothing = () => { };
  setModuleImports('main.js', {
    ui: {
      write: ui.write ?? nothing,
      writeError: ui.writeError ?? nothing,
      writeNote: ui.writeNote ?? ui.writeError ?? nothing,
      askForInput: ui.askForInput ?? nothing,
      sessionAskForInput: (kernelId) => sessionAskForInput?.(kernelId),
      // A page with no one to import must still answer: the session waits.
      sessionImportLibrary: (kernelId, request) => (sessionImportLibrary
        ? sessionImportLibrary(kernelId, request)
        : engine?.SessionSupplyImport(kernelId, JSON.stringify({ error: 'this page imports no libraries' }))),
      debugStopped: (json) => ui.debugStopped?.(JSON.parse(json)),
    },
  });
  engine = (await getAssemblyExports(getConfig().mainAssemblyName)).Shumway.Web.WebShumwayApp;
  await runMain();
  return engine;
}

/**
 * Starts the runtime and the page's own engine.
 * @param {(text: string) => void} onOutput receives everything Prolog writes,
 *        as it is written — a program that prints while it searches should be
 *        watchable while it runs.
 */
export async function boot(onOutput, onAskForInput, onDiagnostic, onDebugStop, onNote) {
  await startRuntime({
    // A hook that opens more sessions than the page's own asks for the
    // threads it needs: &threads=<n>.
    threads: parseInt(/[#&]threads=(\d+)/.exec(location.hash)?.[1] ?? '0', 10),
    ui: {
      write: (t) => { if (typeof t === 'string' && t.startsWith('[tier]')) { try { fetch('/collect', { method: 'POST', body: t }); } catch {} } return onOutput(t); },
      writeError: onDiagnostic, writeNote: onNote ?? onDiagnostic,
      askForInput: onAskForInput,
      debugStopped: onDebugStop,
    },
  });
  return engine.Boot();
}

/** The raw exports, for the workspace module — which speaks to the engine's
 *  filesystem rather than to its solver, so it gets its own facade. */
export const exports = () => engine;

/** Loads the editor's buffer, replacing what it defines. Resolves to null on
 *  success, or the diagnostic. */
export const consult = async (source, dialect = '') => engine.ConsultBuffer(source, dialect);

/** Begins a query. Resolves to null when it started, or the diagnostic —
 *  a syntax error surfaces here, because the engine parses before it runs. */
export const start = async (queryText) => engine.QueryStart(queryText);

/** Takes the next solution: `{ tag, text }`. The engine runs this on a pool
 *  thread, so awaiting it does not block the page. */
export async function next(width = 80) {
  const reply = await engine.QueryNext(width);
  return { tag: reply[0], text: reply.slice(1) };
}

/** Abandons the running query. Returns immediately — it sets the cancellation
 *  token, which the engine observes at its next safe point, so the pending
 *  `next()` resolves shortly afterwards. */
export const cancel = async () => engine.QueryCancel();

/** Throws the engine away and starts another — a workspace is its own program.
 *  Resolves to null, or the error text. */
export const resetEngine = async () => engine.EngineReset();
/** What runs the user's goals now: the wasm tier or the interpreter. */
export const tierName = async () => engine.TierName();

/** Hands a waiting `read/1` a line of input. */
export const supplyInput = async (text) => engine.SupplyInput(text);

/** Ends the input stream: a waiting read gets `end_of_file`. */
export const supplyEndOfFile = async () => engine.SupplyEndOfFile();

/** Every documented predicate, as JSON — the engine's own metadata. */
export const predicateReference = async () => engine.PredicateReference();

/** Predicate names starting with `prefix`. */
export async function complete(prefix) {
  const names = await engine.Complete(prefix);
  return names.length === 0 ? [] : names.split('\n');
}

/** Packs one file and a query into a fragment-safe string. */
export const shareFile = (name, program, query) =>
  engine.ShareEncodeFile(name, program, query);

/** Packs the whole active workspace and a query. */
export const shareWorkspace = (query) => engine.ShareEncodeWorkspace(query);

/** Unpacks a share — `{kind, label, query, files:[{name, text}]}` — or null if
 *  the text is not a valid one. */
export async function shareDecode(encoded) {
  const json = await engine.ShareDecode(encoded);
  if (json === null) return null;
  try { return JSON.parse(json); } catch { return null; }
}

// --- debug (spike) --------------------------------------------------------
// The stop itself arrives through boot()'s onDebugStop: it is an EVENT from a
// search that is mid-flight — its queryNext promise stays pending while
// stopped, and resolves after debugResume lets the search go on.

/** Restarts the engine debug-compiled with a debug session attached. Consult
 *  again afterwards — debuggability is decided when a clause compiles. */
export const debugEnable = async () => engine.DebugEnable();

/** Sets (or removes) a breakpoint, optionally with a condition goal.
 *  Breakpoints bind by file BASE NAME — pass the workspace file's name.
 *  Resolves to null, or why it could not bind. */
export const debugBreakpoint = async (file, line, set = true, condition = '') =>
  engine.DebugBreakpoint(file, line, set, condition);

/** Wakes a stopped search: 'continue' | 'into' | 'over' | 'out'.
 *  Resolves false when nothing was stopped. */
export const debugResume = async (mode = 'continue') => engine.DebugResume(mode);

/** Asks the RUNNING search to pause at its next goal (Break All). */
export const debugBreakNow = async () => engine.DebugBreakNow();

/** Set Next Statement: move where the suspended query resumes to `line` on
 *  `frame`, running nothing. Resolves to the re-captured stop (the arrow has
 *  moved), or `{ error }`. */
export async function debugSetNext(frame, line) {
  const reply = await engine.DebugSetNextStatement(frame, line);
  if (reply.startsWith('error:')) return { error: reply.slice(6).trim() };
  try { return JSON.parse(reply); } catch { return { error: reply }; }
}

/** Evaluates a goal against a frame of the SUSPENDED query — the Immediate
 *  window. `!goal` runs on the real frame; a bare `;` asks the parked
 *  evaluation for its next solution. Resolves to the result text. */
export const debugEvaluate = async (frameIndex, goal) =>
  engine.DebugEvaluate(frameIndex, goal);

/** The suspended query's frames as they are NOW (same JSON as the stop
 *  event), for refreshing after `!` changed the frame — or null. */
export async function debugFrames() {
  const json = await engine.DebugFramesNow();
  if (!json || json.startsWith('error')) return null;
  try { return JSON.parse(json); } catch { return null; }
}

/** Flat [start, length, kind, …] spans covering `source`, from the engine's lexer. */
export async function highlight(source) {
  const packed = await engine.Highlight(source);
  return packed.length === 0 ? [] : packed.split(',').map(Number);
}

/** CSS-class names indexed by span kind. */
export const highlightKinds = async () => (await engine.HighlightKinds()).split(',');
