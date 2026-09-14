# ObjectPoolLinter (Unity package)

A Roslyn analyzer that flags object allocations inside Unity hot paths (`Update`, `FixedUpdate`,
`OnTriggerStay`, and 15 other per-frame messages) and offers code fixes that route the allocation
through an object pool.

## Scope

Both DLLs live in `RoslynAnalyzers/` and carry the `RoslynAnalyzer` asset label with every platform
disabled, so Unity hands them to the C# compiler instead of building them into a player.

Because this package contains no assembly definition file, the analyzer applies to Unity's
predefined assemblies (`Assembly-CSharp` and friends). To run it against your own assembly
definitions as well, see "Scoping the analyzer" in the repository README.

`ObjectPoolLinter.CodeFixes.dll` is only used by IDEs (Rider, Visual Studio, VS Code). The compiler
ignores it; Unity's compile pipeline never loads `Microsoft.CodeAnalysis.CSharp.Workspaces`.

## Configuring hot methods

List extra hot methods or excluded types in `.editorconfig`:

```ini
[*.cs]
object_pool_linter.additional_hot_methods = Tick, OnPreCull
object_pool_linter.excluded_types = LoadingScreen
```

IDEs honour these options. Unity's own editor compile has not been verified to pass them to
analyzers. Details: `docs/rules/OPL001.md#configuration` in the repository.

## Suppressing the rule

Add `dotnet_diagnostic.OPL001.severity = none` to a `.editorconfig` at your project root, or use
`#pragma warning disable OPL001` around a specific allocation.

Documentation and issues: https://github.com/joezhuo2/ObjectPoolLinter
