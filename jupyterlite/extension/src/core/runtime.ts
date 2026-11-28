// The page's one runtime (ADR-068): the demo's wasm app, started with no
// engine of the page's own, the mirror of files between its file system and
// JupyterLab's contents (ADR-070), and the library collections it shares with
// the demo (ADR-070, O3). The runtime's JavaScript is the demo's, copied into
// the site by its build (ADR-073).

import { PageConfig } from '@jupyterlab/coreutils';
import type { Contents } from '@jupyterlab/services';

/** The app's exports a kernel calls: NotebookSessions.cs in Shumway.Web. */
export interface ISessionApi {
  SessionLimit(limit: number): Promise<number>;
  SessionCreate(kernelId: string, bundle: string, directory: string): Promise<string>;
  SessionRequest(kernelId: string, line: string): Promise<string>;
  /** A cell's request, the files under filesRoot as FilesList lists them,
   *  and the libraries compiled on demand: {answer, files, compiled}. */
  SessionCall(kernelId: string, line: string, filesRoot: string): Promise<string>;
  SessionAlive(): Promise<boolean>;
  SessionCancel(kernelId: string): Promise<boolean>;
  SessionSupplyInput(kernelId: string, line: string): Promise<void>;
  SessionSupplyEndOfFile(kernelId: string): Promise<void>;
  SessionSupplyImport(kernelId: string, answer: string): Promise<void>;
  SessionDispose(kernelId: string): Promise<void>;
  SessionTier(kernelId: string): Promise<string>;
}

export interface IMirror {
  /** Contents to the engine's files. */
  pull(): Promise<{ copied: number; removed: number }>;
  /** The engine's files to the contents: those `given` lists, when no copy
   *  touched the engine's files since `given.since`. */
  push(given?: IListing): Promise<{ copied: number; removed: number }>;
  /** Where the engine's side is, for an IListing taken from now on. */
  since(): number;
}

/** The engine's files, as FilesList lists them, taken with another call
 *  begun when the mirror's since() was `since`. */
export interface IListing {
  since: number;
  files: unknown;
}

/** The collections of libraries, kept in the browser's storage with the
 *  demo's: a session finds them before each cell. */
export interface ILibraries {
  /** Brings in those another tab stored since the last cell. */
  sync(): Promise<unknown>;
  /** Stores the libraries a cell compiled because it used them: those
   *  `taken` names, one `name<TAB>library` a line, or those the engine is
   *  asked for. */
  keep(taken?: string): Promise<unknown>;
}

export interface IRuntime {
  api: ISessionApi;
  mirror: IMirror;
  libraries: ILibraries;
  /** Who is told that a session wants a line; null takes the session off. */
  onInput(kernelId: string, handler: (() => void) | null): void;
  /** What a step that waits for the runtime resolves with, or a
   *  RuntimeStopped once the runtime stops answering. A step may take long:
   *  a query does. What tells a stopped runtime is a trivial call that does
   *  not come back either. */
  watched<T>(step: Promise<T>): Promise<T>;
  /** Why the runtime is taken for stopped, or null while it answers. */
  readonly stopped: string | null;
}

/** The runtime stopped answering: every kernel of the page is done with it. */
export class RuntimeStopped extends Error {}

/** What a cell says once the runtime has stopped answering. Seen in WebKit:
 *  .NET's threads there now and then stop taking calls from the page. */
export const STOPPED = "The page's runtime stopped answering, and only reloading the page starts "
  + 'it again. Save the notebook first: its files stay, and the sessions start again with nothing '
  + 'loaded.';

/** What a cell says when the runtime did not start in time. */
export const NOT_STARTED = "The page's runtime did not start in time. Reload the page to try again: "
  + "a notebook's files stay.";

/** Where the engine sees the contents. */
export const DRIVE = '/drive';

/** A number of seconds the site's options name, or `otherwise`. */
function seconds(option: string, otherwise: number): number {
  const value = parseFloat(PageConfig.getOption(option));
  return Number.isFinite(value) && value >= 0 ? value : otherwise;
}

/** Null after `s` seconds; at once for 0, which nothing ends in. */
const after = (s: number) => (s > 0
  ? new Promise<null>(resolve => setTimeout(() => resolve(null), s * 1000))
  : Promise.resolve(null));

let started: Promise<IRuntime> | null = null;

/** The runtime, started by the first kernel that asks. */
export function runtime(contents: Contents.IManager): Promise<IRuntime> {
  return (started ??= start(contents));
}

async function start(contents: Contents.IManager): Promise<IRuntime> {
  if (!globalThis.crossOriginIsolated) {
    throw new Error(
      'The page is not cross-origin isolated, which the runtime needs for its threads. '
      + 'Reloading the page usually fixes it.');
  }
  const site = new URL(PageConfig.getBaseUrl(), location.href);
  // The site's build puts the runtime's JavaScript here, and says where the
  // demo is: the notebook site is a directory of it.
  const scripts = new URL('shumway/', site);
  const demo = new URL(PageConfig.getOption('shumwayRuntimeUrl') || '../', site);

  // Downloading and booting the runtime has shumwayRuntimeStartTimeout
  // seconds: nothing can be asked before it answers.
  const boot = (async () => {
    // Not for the bundler to follow: these are files of the site, not of the
    // extension, and they import the runtime by an address read at run time.
    const session = await import(/* webpackIgnore: true */ new URL('session.js', scripts).href);
    const files = await import(/* webpackIgnore: true */ new URL('mirror.js', scripts).href);
    const libraries = await import(/* webpackIgnore: true */ new URL('libraries.js', scripts).href);
    // Asked for again each time: the name of the runtime's entry changes with
    // every publish of the demo.
    const manifest = await (await fetch(new URL('runtime.json', demo), { cache: 'no-cache' })).json();
    const api: ISessionApi = await session.startRuntime({ entry: new URL(manifest.entry, demo).href });
    return { session, files, libraries, api };
  })();
  boot.catch(() => {});
  const booted = await Promise.race([boot, after(seconds('shumwayRuntimeStartTimeout', 180))]);
  if (!booted) throw new Error(NOT_STARTED);
  const { session, files, libraries, api } = booted;

  // From here on, every step that waits for the runtime is watched: after
  // `patience` seconds the runtime is asked a trivial call (SessionAlive),
  // again every `patience` seconds; one that does not
  // answer in `timeout` seconds means it stopped.
  const patience = seconds('shumwayRuntimePatience', 10);
  const timeout = seconds('shumwayRuntimeTimeout', 30);
  let stopped: string | null = null;
  const watched = async <T>(step: Promise<T>): Promise<T> => {
    const done = step.then(value => ({ value }));
    // A step left behind when the runtime stopped may still fail.
    done.catch(() => {});
    for (;;) {
      if (stopped) throw new RuntimeStopped(stopped);
      const ended = await Promise.race([done, after(patience)]);
      if (ended) return ended.value;
      if (stopped) throw new RuntimeStopped(stopped);
      const answered = await Promise.race([api.SessionAlive().then(() => true), after(timeout)]);
      if (!answered) stopped = STOPPED;
    }
  };

  // How many kernels the page may hold (ADR-068 D7); the app's own number
  // unless the site names one.
  const limit = parseInt(PageConfig.getOption('shumwaySessionLimit'), 10);
  if (limit > 0) await watched(api.SessionLimit(limit));

  const waiting = new Map<string, () => void>();
  session.onSessionAskForInput((kernelId: string) => waiting.get(kernelId)?.());

  // What the demo imported is here too. The bundles come back while the
  // first kernel starts; its first cell waits for them.
  libraries.init(api);
  libraries.restoreBundles(await watched(libraries.restoreSources()));
  session.onSessionImportLibrary(async (kernelId: string, request: string) => {
    let answer: object;
    try {
      answer = { note: await libraries.importFor(JSON.parse(request)) };
    } catch (reason) {
      answer = { error: reason instanceof Error ? reason.message : String(reason) };
    }
    await api.SessionSupplyImport(kernelId, JSON.stringify(answer));
  });

  const memory = files.memoryFiles(api, DRIVE);
  return {
    api,
    mirror: {
      ...files.createMirror(memory, files.contentsStore(contents), { oneAtATime: true }),
      since: () => memory.since()
    },
    libraries: {
      sync: () => libraries.syncFromStorage(),
      keep: (taken?: string) => libraries.keepCompiledOnDemand(taken ?? null)
    },
    onInput(kernelId, handler) {
      if (handler) waiting.set(kernelId, handler);
      else waiting.delete(kernelId);
    },
    watched,
    get stopped() {
      return stopped;
    }
  };
}
