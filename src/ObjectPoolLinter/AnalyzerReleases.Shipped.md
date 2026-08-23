## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPL001 | Performance | Warning | Detects `new` expressions and Unity `Instantiate` calls inside frequently-invoked methods (e.g. `Update`).