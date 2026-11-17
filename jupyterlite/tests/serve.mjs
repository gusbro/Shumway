// A static server that sends no isolation headers, as GitHub Pages sends
// none: what isolates the pages has to be the service worker.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.wasm': 'application/wasm', '.json': 'application/json; charset=utf-8',
  '.ipynb': 'application/json; charset=utf-8', '.pl': 'text/plain; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.webmanifest': 'application/manifest+json'
};

/**
 * Serves `directory` on a free port of this machine.
 * @param {object} [siteOptions] options of the notebook site to serve it
 *        with, over those of its jupyter-lite.json
 * @returns {Promise<{ url: string, close(): void }>} the address of its root
 */
export async function serve(directory, siteOptions = null) {
  const root = path.resolve(directory);
  if (!fs.existsSync(path.join(root, 'lite', 'lab', 'index.html')))
    throw new Error(`${root} is not a deployment: it has no lite/lab/index.html`);
  const server = http.createServer((request, response) => {
    let name = decodeURIComponent(new URL(request.url, 'http://x').pathname);
    if (name.endsWith('/')) name += 'index.html';
    const file = path.join(root, name);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      response.writeHead(404, { 'Content-Type': 'text/plain' });
      response.end('not found');
      return;
    }
    response.writeHead(200, {
      'Content-Type': types[path.extname(file)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store'
    });
    if (siteOptions && name === '/lite/jupyter-lite.json') {
      const settings = JSON.parse(fs.readFileSync(file, 'utf8'));
      Object.assign(settings['jupyter-config-data'], siteOptions);
      response.end(JSON.stringify(settings));
      return;
    }
    fs.createReadStream(file).pipe(response);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return { url: `http://localhost:${server.address().port}/`, close: () => server.close() };
}
