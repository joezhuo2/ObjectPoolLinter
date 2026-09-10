# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v0.6.2]

### Added
- NuGet package metadata on the analyzer project: `PackageId`, an explicit `Version`, `Authors`,
  `Copyright`, `Description`, `PackageTags`, `PackageProjectUrl`, `RepositoryUrl`, `RepositoryType`,
  `PackageReadmeFile` (the README is now packed at the package root) and
  `PackageLicenseExpression` (`MIT`).
- `DevelopmentDependency=true` and `PackageType=Analyzer`, so consumers no longer pick up the
  analyzer as a transitive runtime dependency.

### Changed
- The package version is now stated in the project file instead of being left unset, where NuGet
  silently stamped `1.0.0`. The `Release 1.0.0` heading in `AnalyzerReleases.Shipped.md` is therefore
  no longer accidentally consistent with the package version and still needs to be reconciled.
- `SuppressDependenciesWhenPacking=true`: the package carries no `lib/`, so the otherwise-empty
  `netstandard2.0` dependency group tripped NU5128 on every `dotnet pack`.

## [v0.6.1]

### Fixed
- building the package with `dotnet pack -c Release` creating an empty package (no analyzer)

## [v0.6.0]

### Fixed
- The "Replace with object pool Get()" code fix invented a pool type out of the allocated type's
  name and emitted a call to it without checking that anything by that name existed. `new Enemy()`
  became `EnemyPool.Get()` even when no `EnemyPool` was in scope, replacing a working line with an
  unresolved reference. The fix now resolves the candidate `{Type}Pool` name from the allocation site
  before offering itself: a type with that name must be visible there, its generic arity must match
  the name being generated (so `ListPool<T>` matches `new List<int>()` while a non-generic `ListPool`
  does not), and it must expose a static `Get` that is accessible from the call site and can accept
  the number of constructor arguments being forwarded (accounting for optional and `params`
  parameters). When no such type is found the fix is not registered and only the TODO-comment fix is
  offered, leaving the user's code intact.

### Changed
- Pool name construction and validation moved into a single `TryGetPoolName` helper used both when
  registering the fix and when applying it, so the offer and the resulting edit cannot disagree about
  which pool type they mean.

### Added
- Code fix tests asserting the replace fix is withheld when no pool type exists, when the pool's
  `Get` is not static, when it is inaccessible (`private`), when the pool's generic arity differs
  from the allocated type's, and when `Get` cannot take the constructor arguments being forwarded.

## [v0.5.0]

### Fixed
- The "Replace with object pool Get()" code fix dropped constructor arguments: `new Enemy(hp)`
  became `EnemyPool.Get()`, silently losing the argument. The fix now forwards the original argument
  list to `Get()`, so `new Enemy(hp)` becomes `EnemyPool.Get(hp)` and target-typed `new(hp)` becomes
  `EnemyPool.Get(hp)`. Arguments are copied as written, keeping named arguments, `ref`/`out`
  modifiers and inner formatting.

### Changed
- The "Replace with object pool Get()" fix is no longer offered when the allocation has an object or
  collection initializer (`new List<int> { 1, 2 }`). An initializer cannot be carried onto a method
  call, so the only alternatives were to drop it or to rewrite the surrounding statement; withholding
  the fix leaves the user with the TODO-comment fix and their code intact. Documented in the README.

### Added
- Code fix tests covering argument forwarding (single argument, named/`out` arguments, target-typed
  `new`) and one asserting the replace fix is not registered when an initializer is present.

## [v0.4.0]

### Fixed
- The "Add pooling TODO comment" code fix produced code that does not compile. The comment was
  attached as leading trivia of the *allocation expression*, which normally starts mid-line, so
  `var list = new List<int>();` became
  `var list =// TODO: use an object pool to avoid per-frame allocation new List<int>();` - a line
  comment swallows the rest of its line, taking the initializer and the semicolon with it. The
  comment is now attached to the enclosing `StatementSyntax`, followed by an end-of-line trivia and
  the statement's own indentation, so it sits on its own line above the statement it describes.

### Added
- Code fix tests (`tests/ObjectPoolLinter.Tests/ObjectPoolCodeFixProviderTests.cs`), covering the
  TODO-comment fix on a local declaration, on an expression statement (`Object.Instantiate(prefab);`),
  and on a nested statement where the indentation differs from the method body's. One test applies
  the fix and reparses the resulting text into a fresh compilation, asserting it has no compiler
  errors - reparsing is what makes the assertion meaningful, since a misplaced comment stays
  structurally valid as trivia in the already-parsed tree.
- `Microsoft.CodeAnalysis.CSharp.CodeFix.Testing.XUnit` test dependency.

### Changed
- The inserted end-of-line trivia matches the line ending the file already uses (CRLF or LF) rather
  than a fixed `ElasticCarriageReturnLineFeed`. Elastic trivia invites the post-fix formatting pass
  to rewrite neighbouring lines to the workspace's own newline, which would churn line endings the
  user never touched.

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
