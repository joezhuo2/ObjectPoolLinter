# ObjectPoolLinter (Unity package)

A Roslyn analyzer that flags allocations inside Unity hot paths (`Update`, `FixedUpdate`,
`OnTriggerStay`, and 15 other per-frame messages):

- **OPL001** (warning): `new` allocations and `Instantiate`, with code fixes that route the allocation
  through an object pool.
- **OPL002** (info): string concatenation and interpolation, `string.Concat`, `string.Format`,
  `StringBuilder.ToString()`, capturing lambdas, method-group delegates,
  implicit `params` arrays, LINQ and boxing, with code fixes that cache lambdas and delegates in
  `Awake()`, build interpolated strings with a reused `StringBuilder`, and turn simple LINQ chains into
  a loop.
- **OPL003** (warning): Unity APIs that return a new array, such as `GetComponentsInChildren<T>()`,
  `Physics.RaycastAll` and `Camera.allCameras`, plus `name` and `tag`.

## Scope

Both DLLs live in `RoslynAnalyzers/` and carry the `RoslynAnalyzer` asset label with every platform
disabled, so Unity hands them to the C# compiler instead of building them into a player.

Because this package contains no assembly definition file, the analyzer applies to Unity's
predefined assemblies (`Assembly-CSharp` and friends). To run it against your own assembly
definitions as well, see "Scoping the analyzer" in the repository README.

`ObjectPoolLinter.CodeFixes.dll` is only used by IDEs (Rider, Visual Studio, VS Code). The compiler
ignores it; Unity's compile pipeline never loads `Microsoft.CodeAnalysis.CSharp.Workspaces`.

## Configuring hot methods

List extra hot methods or excluded types in `.editorconfig`. The options apply to all three rules:

```ini
[*.cs]
object_pool_linter.additional_hot_methods = Tick, OnPreCull
object_pool_linter.excluded_types = LoadingScreen
```

IDEs honour these options. Unity's own editor compile has not been verified to pass them to
analyzers. Details: `docs/rules/OPL001.md#configuration` in the repository.

## Changing a rule's severity

The Unity Console shows warnings and errors only, so OPL002 appears there only after it is raised.
Add lines like these to a `.editorconfig` at your project root, or use `#pragma warning disable <rule>`
around a specific allocation:

```ini
[*.cs]
dotnet_diagnostic.OPL001.severity = none      # turn OPL001 off
dotnet_diagnostic.OPL002.severity = warning   # show OPL002 in the Console
```

Documentation and issues: https://github.com/joezhuo2/ObjectPoolLinter
