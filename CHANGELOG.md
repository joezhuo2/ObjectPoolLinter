# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v0.3.1]

### Added
- **`.gitattributes`** - Without it, files saved with CRLF while the committed blobs are LF show up as whole-file modifications and pollute real diffs. Normalize text files to LF in the repository, native on checkout, and mark common binary types explicitly.

## [v0.3.0]

### Fixed
- Allocations inside a lambda or anonymous method declared in a hot-path Unity message
  are no longer attributed to that message. How often the delegate runs is decided by
  whoever holds it, so a lambda registered as a callback in `Update()` produced a false
  positive. A lambda that is invoked on the spot is still reported.
- Allocations inside a local function declared in a hot-path Unity message are reported
  only when the declaring body actually calls that local function. A local function that
  is only converted to a delegate escapes the same way a lambda does.

## [v0.2.0]

### Fixed
- Code fix emitted invalid C# for generic types: `new List<int>()` produced
  `List<int>Pool.Get()` instead of `ListPool<int>.Get()`. The `Pool` suffix was appended
  to the whole rendered type name, so it landed after the type arguments.
- Code fix emitted the wrong pool name for qualified types: `new Foo.Bar()` produced
  `Foo.BarPool.Get()` instead of `BarPool.Get()`.
- Code fix was never offered for target-typed `new()`, although the analyzer reports it.
  `List<int> x = new();` now offers `ListPool<int>.Get()`.

### Changed
- `ReplaceWithPoolGetAsync` builds the replacement from the type **symbol**
  (`INamedTypeSymbol.Name` plus its type arguments) rather than from
  `objectCreation.Type.ToString()`, and composes it from `SyntaxFactory` nodes rather
  than `SyntaxFactory.ParseExpression` on an interpolated string. Type arguments are
  reused from the user's own syntax where it exists, and printed from the symbol via
  `ToMinimalDisplayString` for target-typed `new()`, where there is no type syntax.
- `RegisterCodeFixesAsync` matches `BaseObjectCreationExpressionSyntax`, covering both
  `new T()` and `new()`.
- The fix now returns the document unchanged when the type cannot be resolved, or when a
  type argument printed from the symbol does not round-trip through `ParseTypeName`,
  instead of emitting a guess.

## [v0.1.0]

### Added
- Detection of array allocations in hot paths: `SyntaxKind.ArrayCreationExpression`
  (`new int[10]`) and `SyntaxKind.ImplicitArrayCreationExpression` (`new[] { 1, 2 }`).
- Detection of target-typed `new()` allocations:
  `SyntaxKind.ImplicitObjectCreationExpression` (`List<int> x = new();`).

### Changed
- `AnalyzeObjectCreation` generalised to `AnalyzeAllocation`, handling every allocating
  syntax kind through a single registration.
- Allocated type name in the diagnostic message is now resolved per syntax kind, falling
  back to the semantic type in minimally-qualified form for implicit forms.
- Value-type filtering no longer suppresses arrays of value types, so `new int[10]` is
  reported.

### Removed
- Empty placeholder test `tests/ObjectPoolLinter.Tests/UnitTest1.cs`.
