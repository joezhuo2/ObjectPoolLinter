; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL005 | Usage | Warning | Reports a class marked `[ObjectPool]` for which the source generator cannot write a pool.
OPL006 | Usage | Warning | Reports a field of managed type in a struct implementing a Unity job interface, which makes `Schedule()` throw.
