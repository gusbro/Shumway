// The JupyterLab extension: registers the kernel with JupyterLite, and draws
// the answer of a query that can give another solution.

import type { JupyterFrontEnd, JupyterFrontEndPlugin } from '@jupyterlab/application';
import { IEditorLanguageRegistry } from '@jupyterlab/codemirror';
import { IRenderMimeRegistry } from '@jupyterlab/rendermime';
import { IKernelClient, IKernelSpecs, type IKernel } from '@jupyterlite/services';

import { addStyles, Answer, nextSolution } from './answers';
import { ServerKernel } from './core/kernel';
import { ANSWER } from './core/queries';
import { prolog } from './prolog/mode';
import { spec, variant } from './prolog/variant';

const kernel: JupyterFrontEndPlugin<void> = {
  id: '@shumway/jupyterlite-kernel:prolog',
  description: 'A kernel for Prolog on the WebShumway runtime',
  autoStart: true,
  requires: [IKernelSpecs],
  optional: [IKernelClient],
  activate: (app: JupyterFrontEnd, kernelspecs: IKernelSpecs, client: IKernelClient | null) => {
    const running = new Map<string, ServerKernel>();

    kernelspecs.register({
      spec,
      create: async (options: IKernel.IOptions): Promise<IKernel> => {
        const made = new ServerKernel(options, variant, app.serviceManager.contents, app.serviceManager.sessions);
        running.set(options.id, made);
        made.disposed.connect(() => {
          if (running.get(options.id) === made) running.delete(options.id);
        });
        return made;
      }
    });

    // JupyterLite's interrupt drops the cells that wait and tells the kernel
    // nothing, so a query that runs would run on. This one can be stopped.
    if (client) {
      const interrupt = client.interrupt.bind(client);
      client.interrupt = async (kernelId: string) => {
        await running.get(kernelId)?.interrupt();
        return interrupt(kernelId);
      };
    }
  }
};

const language: JupyterFrontEndPlugin<void> = {
  id: '@shumway/jupyterlite-kernel:prolog-language',
  description: 'Prolog in the cell editor',
  autoStart: true,
  requires: [IEditorLanguageRegistry],
  activate: (_app: JupyterFrontEnd, languages: IEditorLanguageRegistry) => {
    // JupyterLab gives `pl` to Perl, and a file's language is the first in
    // its list that claims the extension. In this site a .pl file is Prolog.
    // The registry has no way to take an extension back, so the entry it
    // hands out is changed in place.
    const perl = languages.findByName('Perl') as { extensions?: readonly string[] } | null;
    if (perl?.extensions) perl.extensions = perl.extensions.filter(extension => extension !== 'pl');
    languages.addLanguage({
      name: 'Prolog',
      mime: 'text/x-prolog',
      extensions: ['pl', 'pro', 'prolog'],
      load: async () => {
        const { LanguageSupport, StreamLanguage } = await import('@codemirror/language');
        return new LanguageSupport(StreamLanguage.define(prolog));
      }
    });
  }
};

const answers: JupyterFrontEndPlugin<void> = {
  id: '@shumway/jupyterlite-kernel:answers',
  description: 'More solutions of a query, asked for from its answer',
  autoStart: true,
  requires: [IRenderMimeRegistry],
  activate: (app: JupyterFrontEnd, rendermime: IRenderMimeRegistry) => {
    addStyles();
    // Ahead of plain text, which every answer has beside it.
    rendermime.addFactory({
      safe: true,
      mimeTypes: [ANSWER],
      defaultRank: 40,
      createRenderer: () => new Answer(app)
    });
    // A top level's key for the next solution, where a notebook takes
    // commands and not text.
    const command = 'shumway:next-solution';
    app.commands.addCommand(command, {
      label: 'Next Solution',
      caption: 'The next solution of the query in the active cell',
      execute: () => { nextSolution(app); }
    });
    app.commands.addKeyBinding({
      command,
      keys: [';'],
      selector: '.jp-Notebook.jp-mod-commandMode:not(.jp-mod-readWrite) :focus:not(:read-write)'
    });
  }
};

export default [kernel, language, answers];
