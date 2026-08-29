// Collections run in parallel, like the other engine suites. This assembly
// once carried the serial attribute as a stopgap for a real symptom — the
// Trealla validation failed beside the other validations and passed alone —
// that was blamed on the process-global AtomTable/FunctorTable. The actual
// cause was the tests themselves: each validation swapped the process-global
// Console.Error to capture consult warnings AND derived its load verdict from
// the capture, so concurrent validations contaminated each other's verdicts
// (and could restore each other's writer on the way out). The captures now go
// through the engine's own per-engine Warnings writer, which removes the
// shared stream entirely.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 3)]
