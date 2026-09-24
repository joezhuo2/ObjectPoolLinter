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

## Release 1.5.2

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL004 | Configuration | Warning | Reports `object_pool_linter.*` options in `.editorconfig` whose name is not recognized or whose value cannot be used.

## Release 1.5.4

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL005 | Usage | Warning | Reports a class marked `[ObjectPool]` for which the source generator cannot write a pool.

## Release 1.5.6

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL006 | Usage | Warning | Reports a field of managed type in a struct implementing a Unity job interface, which makes `Schedule()` throw.

## Release 1.6.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL007 | Reliability | Warning | Reports a native container allocated with `Allocator.TempJob` or `Persistent` that is not disposed on every path out of its method, or, kept in a field, never disposed by its type.
OPL008 | Performance | Info | Reports `Resources.Load` and Addressables `Load*` calls inside frequently-invoked methods.

## Release 1.6.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL009 | Performance | Info | Reports `GetComponent`, `TryGetComponent`, `GetComponentInChildren`/`InParent` on the same object, and `GameObject.Find`, `FindWithTag` and `FindObjectOfType` inside frequently-invoked methods.
