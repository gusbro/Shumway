// The Prolog variant (ADR-066 D11): what makes the kernel Shumway Prolog.

import type { KernelSpec } from '@jupyterlab/services';

import type { IVariant } from '../core/kernel';

// The neck of a clause on the colour of a terminal's prompt.
const LOGO = 'data:image/svg+xml;base64,' + btoa(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">'
  + '<rect width="64" height="64" rx="12" fill="#1f2937"/>'
  + '<g fill="#34d399"><circle cx="20" cy="24" r="6"/><circle cx="20" cy="42" r="6"/>'
  + '<rect x="32" y="29" width="22" height="8" rx="4"/></g></svg>');

/** The kernelspec name is the implementation's; the language is Prolog, as
 *  the other Prolog kernels declare it, so their notebooks are recognised. */
export const spec: KernelSpec.ISpecModel = {
  name: 'shumway_prolog',
  display_name: 'Prolog (Shumway)',
  language: 'prolog',
  argv: [],
  env: {},
  resources: { 'logo-32x32': LOGO, 'logo-64x64': LOGO }
};

export const variant: IVariant = {
  bundle: 'prolog',
  protocol: 1,
  completionQuery: 'jupyter:update_completion_data.',
  implementation: 'shumway',
  languageInfo: {
    name: 'Prolog',
    version: '',
    mimetype: 'text/x-prolog',
    file_extension: '.pl',
    codemirror_mode: 'prolog'
  },
  texts: {
    success: 'true',
    failure: 'false',
    errorPrefix: '% ',
    noticePrefix: '% '
  }
};
