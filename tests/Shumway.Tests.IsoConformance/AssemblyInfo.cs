// This assembly never had the serial attribute the sister projects carried —
// its collections have ALWAYS run in parallel on xUnit defaults, green, which
// quietly proved the multi-engine thread-agile claim before anyone made it a
// goal. Made explicit here, capped at the same width the other projects use.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 3)]
