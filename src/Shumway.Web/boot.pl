% The linker needs a reachability root to produce a bundle; this is it.
% Nothing in the browser calls it — the payload we actually want is the BAKED
% PRELUDE that `--stdlib` puts alongside it, so the engine boots without
% compiling the ~780-line standard library (measured ~500 ms -> ~345 ms cold
% under browser-wasm).
web_boot.
