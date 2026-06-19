using System;

namespace Shumway.Native;

// ADR-022 item 2 (stage C) — the conventionally-named interop class the build
// engine auto-discovers when inlining native blocks into a persisted-IL bundle
// (NativeBuildInlineTests). A real program would ship this in its own assembly and
// reference it via shumway-link --foreign-dll; here it lives in the test assembly
// so it is loaded at both build and run.
public static class Interop
{
    public static int strcmp(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
    public static long sum(long a, long b) => a + b;
}
