/* The crossing the wasm Tier-1 arc needs (docs/design/wasm-tier1-plan.md).
 *
 * A predicate compiled at run time has to be callable from the threads the
 * engine runs on. The subtlety that sank the first attempt: with threads,
 * every worker instantiates its OWN WebAssembly.Table (only the Memory is
 * shared), so a module registered from the page's UI thread exists in the UI
 * thread's table and nowhere else -- and a call_indirect through that index
 * from the runtime's worker traps on a slot that does not exist there,
 * taking the worker down without an exception.
 *
 * So registration happens HERE, in native code, through EM_JS: JavaScript
 * executed in the calling thread's own realm, against the calling thread's
 * own table. A thread registers the module bytes once and gets an index that
 * is valid where it will be used; the shim below then calls through it with
 * a single call_indirect. The shim and this file never change; the bytes and
 * the index are run-time values, which is what keeps the JIT story alive.
 */

#include <emscripten/em_js.h>

/* This thread's table size. Diagnostic: an index at or past this length is
 * an index some other thread's addFunction handed out. */
EM_JS(int, shumway_wasm_table_length, (void), {
    return wasmTable.length;
});

/* Instantiates the module (bytes in the shared linear memory) against this
 * realm's memory and registers its `run` export in THIS thread's table.
 * Returns the table index, or -1 with the reason on the console. */
EM_JS(int, shumway_wasm_register, (int bytesPtr, int len), {
    try {
        /* slice, not subarray: a view over shared memory cannot be compiled
         * from directly, and the copy detaches the bytes from the heap. */
        var bytes = HEAPU8.slice(bytesPtr, bytesPtr + len);
        var mod = new WebAssembly.Module(bytes);
        var inst = new WebAssembly.Instance(mod, { env: { memory: wasmMemory } });
        return addFunction(inst.exports.run, 'iii');
    } catch (e) {
        console.error('shumway_wasm_register: ' + e);
        return -1;
    }
});

/* What THIS thread's table says about an index, without calling it: -2 when
 * the slot does not exist here (past this table's length), 1 when it exists
 * and is occupied, 0 when it exists and is empty. The safe way to ask
 * whether an index registered on another thread means anything on this one:
 * calling it would trap the worker, and a trap here has no catch. */
EM_JS(int, shumway_wasm_probe_index, (int index), {
    if (index >= wasmTable.length) return -2;
    try { return wasmTable.get(index) ? 1 : 0; } catch (e) { return -3; }
});

int shumway_wasm_call(int index, int mailbox, int cursor)
{
    return ((int (*)(int, int))index)(mailbox, cursor);
}
