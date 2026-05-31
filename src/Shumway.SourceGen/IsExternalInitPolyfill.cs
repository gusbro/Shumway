// netstandard2.0 doesn't ship System.Runtime.CompilerServices.IsExternalInit,
// which C# 9+ records and `init` setters require. This polyfill is the
// standard workaround for analyzer / source-generator projects pinned to
// netstandard2.0 by Roslyn.

namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
