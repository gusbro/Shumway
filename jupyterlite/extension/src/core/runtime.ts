// The page's one runtime (ADR-068): the demo's wasm app, started with no
// engine of the page's own, and the mirror of files between its file system
// and JupyterLab's contents (ADR-070). The runtime's JavaScript is the
// demo's, copied into the site by its build (ADR-073).

import { PageConfig } from '@jupyterlab/coreutils';
import type { Contents } from '@jupyterlab/services';

/** The app's exports a kernel calls: NotebookSessions.cs in Shumway.Web. */
export interface ISessionApi {
  SessionLimit(limit: number): Promise<number>;
  SessionCreate(kernelId: string, bundle: string, directory: string): Promise<string>;
  SessionRequest(kernelId: string, line: string): Promise<string>;
  SessionCancel(kernelId: string): Promise<boolean>;
  SessionSupplyInput(kernelId: string, line: string): Promise<void>;
  SessionSupplyEndOfFile(kernelId: string): Promise<void>;
  SessionDispose(kernelId: string): Promise<void>;
  SessionTier(kernelId: string): Promise<string>;
}

export interface IMirror {
  /** Contents to the engine's files. */
  pull(): Promise<{ copied: number; removed: number }>;
  /** The engine's files to the contents. */
  push(): Promise<{ copied: number; removed: number }>;
}

export interface IRuntime {
  api: ISessionApi;
  mirror: IMirror;
  /** Who is told that a session wants a line; null takes the session off. */
  onInput(kernelId: string, handler: (() => void) | null): void;
}

/** Where the engine sees the contents. */
export const DRIVE = '/drive';

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

  // Not for the bundler to follow: these are files of the site, not of the
  // extension, and they import the runtime by an address read at run time.
  const session = await import(/* webpackIgnore: true */ new URL('session.js', scripts).href);
  const files = await import(/* webpackIgnore: true */ new URL('mirror.js', scripts).href);

  // Asked for again each time: the name of the runtime's entry changes with
  // every publish of the demo.
  const manifest = await (await fetch(new URL('runtime.json', demo), { cache: 'no-cache' })).json();
  const api: ISessionApi = await session.startRuntime({ entry: new URL(manifest.entry, demo).href });

  // How many kernels the page may hold (ADR-068 D7); the app's own number
  // unless the site names one.
  const limit = parseInt(PageConfig.getOption('shumwaySessionLimit'), 10);
  if (limit > 0) await api.SessionLimit(limit);

  const waiting = new Map<string, () => void>();
  session.onSessionAskForInput((kernelId: string) => waiting.get(kernelId)?.());

  return {
    api,
    mirror: files.createMirror(files.memoryFiles(api, DRIVE), files.contentsStore(contents), {
      oneAtATime: true
    }),
    onInput(kernelId, handler) {
      if (handler) waiting.set(kernelId, handler);
      else waiting.delete(kernelId);
    }
  };
}
