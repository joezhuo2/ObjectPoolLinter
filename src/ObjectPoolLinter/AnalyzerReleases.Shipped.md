; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL001 | Performance | Warning | Detects `new` expressions and Unity `Instantiate` calls inside frequently-invoked methods (e.g. `Update`).

## Release 1.3.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL002 | Performance | Info | Detects string concatenation and interpolation, capturing lambdas, method-group delegates, implicit `params` arrays, LINQ and boxing inside frequently-invoked methods.
OPL003 | Performance | Warning | Detects Unity engine APIs that return a new array (and `Object.name` / `tag`) inside frequently-invoked methods.
