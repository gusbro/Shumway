// The JupyterLab extension: registers the kernel with JupyterLite.

import type { JupyterFrontEnd, JupyterFrontEndPlugin } from '@jupyterlab/application';
import { IEditorLanguageRegistry } from '@jupyterlab/codemirror';
import { IKernelClient, IKernelSpecs, type IKernel } from '@jupyterlite/services';

import { ServerKernel } from './core/kernel';
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

export default [kernel, language];
