# Safari probe

Whether Safari keeps taking calls into .NET's threads.

In the WebKit that Playwright drives, on Windows and Linux, the page's calls
into a threaded .NET runtime now and then stop coming back: the page stays
alive, the runtime answers nothing more, and nothing says why. A threaded
.NET 10 app with an empty `[JSExport]`, and nothing of Shumway in it, does
it after a few thousand calls. The notebook site tells the user when it
happens (`jupyterlite/README.md`, Configuration), but whether Safari does it
too, on a Mac or an iPhone, those runs cannot say.

- `ThreadedPing/` is that app: `window.ping()` calls into .NET and comes
  back with the empty string. It is not part of the solution.
- `probe.mjs` serves what it is given with the headers that give
  cross-origin isolation and drives a browser over WebDriver, speaking the
  protocol over HTTP with no package. With `ping` it calls the app's
  `window.ping()` in rounds of a thousand; with `cells` it runs cells in a
  notebook of a built notebook site, in rounds of 250. Either way it reports
  the time of each and whether every one came back.

`.github/workflows/safari.yml` runs both on a macOS runner, by hand or on a
pull request that changes this directory: `ping` in Safari and in Safari on
an iOS simulator, `cells` in Safari on the notebook site the branch builds.
On a Mac:

```sh
dotnet workload install wasm-tools
dotnet publish tests/webkit-probe/ThreadedPing -c Release -o probe-site
sudo safaridriver --enable
safaridriver -p 4444 &
node tests/webkit-probe/probe.mjs probe-site/wwwroot macos ping 30000
```
