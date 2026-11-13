// The boxes the page types goals into (the query, the debugger's watch and
// Immediate) are textareas of one row, not inputs: Chrome on Android offers
// its autofill bar (passwords, cards, addresses) on a text input of a form
// whatever its autocomplete attribute says, and not on a textarea. What an
// input did on its own is done here.

/** Makes a one-row textarea behave like a one-line input: Enter submits its
 *  form, and a line pasted or dropped in with breaks has them turned into
 *  spaces. `submits` says whether this Enter is the form's: the query box
 *  keeps Enter for its own keys while answers are pending. Register it before
 *  the box's own keydown listener; an Enter it submits goes no further. */
export function oneLine(box, submits = () => true) {
  box.addEventListener('input', () => {
    const v = box.value;
    if (!/[\r\n]/.test(v)) return;
    const caret = box.selectionStart;
    box.value = v.replace(/\r\n|[\r\n]/g, ' ');
    box.setSelectionRange(caret, caret);
  });
  box.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || e.isComposing || !submits()) return;
    e.preventDefault();
    // A submit that starts a query must not reach the box's own listener as
    // a key of the answers it is now waiting for.
    e.stopImmediatePropagation();
    box.form.requestSubmit();
  });
}
