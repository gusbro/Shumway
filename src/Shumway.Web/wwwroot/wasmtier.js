// The wasm Tier-1 spike's one piece of JavaScript: put a compiled predicate
// into the runtime's own world.
//
// Two things have to be true for the whole arc to work, and this is where they
// are decided (the plan's D1 and D2):
//
//   1. The module can import the RUNTIME's memory. Then a base written into
//      the mailbox is an address the wasm can dereference, and the engine's
//      heap needs no copying, no marshalling and no view of its own.
//   2. The export can be registered in the runtime's function TABLE. A wasm
//      function pointer IS a table index, so the index that comes back is what
//      C# calls through, with no JavaScript on the hot path at all.
//
// If either fails the arc has no product: a per-call trip through a JS thunk
// is thread-affine and marshals, which is what the whole design avoids.

function runtime() {
    // The runtime exposes itself once the module is up; index 0 is this one.
    const get = globalThis.getDotnetRuntime;
    if (typeof get !== "function")
        throw new Error("getDotnetRuntime is not there: no runtime to join.");
    const rt = get(0);
    if (!rt || !rt.Module) throw new Error("the runtime exposes no Module.");
    return rt;
}

/// Instantiates a module (base64) against the runtime's memory and returns the
/// table index of its `run` export.
export function instantiate(base64) {
    const rt = runtime();
    const mod = rt.Module;
    const memory = mod.wasmMemory;
    if (!memory) throw new Error("the runtime exposes no wasmMemory.");

    const binary = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const instance = new WebAssembly.Instance(
        new WebAssembly.Module(binary), { env: { memory } });

    if (typeof mod.addFunction !== "function")
        throw new Error("the runtime's Module has no addFunction: nothing can "
                        + "put a foreign export into its table.");
    // "iii": returns i32, takes two i32 — the ABI's (mailbox, cursor).
    return mod.addFunction(instance.exports.run, "iii");
}

// The instances, by handle. The table index is what a function POINTER
// needs; a handle is what a JavaScript thunk needs, and keeping both lets the
// two paths be compared with the same module.
const instances = [];

/// Instantiates and returns a handle, without touching the runtime's table.
export function instantiateForThunk(base64) {
    const memory = runtime().Module.wasmMemory;
    const binary = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const instance = new WebAssembly.Instance(
        new WebAssembly.Module(binary), { env: { memory } });
    instances.push(instance.exports.run);
    return instances.length - 1;
}

/// Calls one, by handle. This is the hop the design wanted to avoid: it is
/// JavaScript, so it is affine to the thread that owns it.
export function callHandle(handle, a, b) {
    return instances[handle](a, b);
}

/// What the memory looks like from here, for the report: pages and bytes, and
/// whether it is the shared kind (which is what threads make it).
export function memoryFacts() {
    const memory = runtime().Module.wasmMemory;
    const shared = typeof SharedArrayBuffer !== "undefined"
                   && memory.buffer instanceof SharedArrayBuffer;
    return `${memory.buffer.byteLength} bytes, ${shared ? "shared" : "not shared"}`;
}
