// Starts the runtime and lets the page call into it: window.ping() answers
// a promise of the empty string, from .NET.
import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
await runtime.runMain();
const app = exports.Shumway.WebKitProbe.ThreadedPingApp;
window.ping = () => app.Ping();
window.ready = true;
