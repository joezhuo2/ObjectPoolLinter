# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [v0.9.1] - 2026-09-12

### Tests
- Analyzer tests no longer hardcode diagnostic spans. The seven `.WithSpan(line, col, line, col)`
  expectations are replaced by markup in the test source (`{|#0:...|}`) with `.WithLocation(0)`, so
  reformatting a test source does not break its expectation. Message arguments are still checked.
  Closes T3.

## [v0.9.0] - 2026-09-12

### Changed
- `AnalyzerReleases.Shipped.md` no longer claims a `Release 1.0.0` that was never published. OPL001
  moves to `AnalyzerReleases.Unshipped.md`, which is where a rule lives until the release that ships
  it; the 1.0.0 release commit moves it back under a real `## Release 1.0.0` heading. Until then the
  shipped file is empty, which is the truth: no version of this analyzer has been released.
- This changelog now carries an `[Unreleased]` section, a release date on every version heading, and
  compare links against the `v*` tags. Changes land under `[Unreleased]` and are renamed to the
  version heading when that version is tagged, instead of every change inventing a version of its
  own.

### Notes
- `v0.7.1` was never a release. Its SourceLink and deterministic-build work landed in the same commit
  as `v0.8.0`, and the package version went straight from `0.7.0` to `0.8.0`, so the entry below has
  no tag and no compare link.

### Tests
- Analyzer coverage now includes the allocation shapes added after the first release: array creation
  (`new int[10]`), implicit array creation (`new[] { 1, 2 }`) and target-typed `new()`, each pinning
  the type name in the diagnostic message; allocations outside a hot path, including `Instantiate` in
  `Start`; a MonoBehaviour two inheritance levels deep; a MonoBehaviour nested in another
  MonoBehaviour; and a plain class nested inside a MonoBehaviour, which must stay silent. Two
  theories sweep the message table: all 18 hot-path messages report, and five cold-path messages do
  not. 84 tests, up from 53. Closes T2.

## [v0.8.8] - 2026-09-12

### Documentation
- The README has a `Known limitations` section stating what OPL001 does not do, so a clean run is not
  read as a claim that a method allocates nothing: no call-graph analysis and delegates that escape
  the frame (lambdas and local functions only converted, never invoked in place); the allocation
  shapes that go undetected (boxing, string concatenation and interpolation, closure capture, implicit
  `params` arrays, LINQ, and the allocating Unity APIs such as `GetComponentsInChildren`,
  `Physics.RaycastAll`, `GameObject.Find`, `Camera.allCameras` and `Input.touches`), scoped as future
  OPL002+ rules rather than a widening of OPL001; the two allocation shapes that get no replacement
  fix (arrays, and allocations carrying an object or collection initializer); and the fact that the
  replacement fix neither writes the pool nor releases the object. Linked from the `Features` and
  `Usage` sections, and `docs/rules/OPL001.md` gains a `What the rule does not cover` section pointing
  at it. Closes D3.

## [v0.8.7] - 2026-09-12

### Documentation
- The README now has a `The pool contract` section specifying exactly what the "Replace with object
  pool `Get()`" fix looks for: the `{TypeName}Pool` name derived from the unqualified type name,
  simple-name visibility at the allocation site, matching generic arity, and a static accessible
  `Get` whose parameter list accepts the forwarded constructor arguments. It states plainly that the
  fix never creates the pool and never inserts the release call, names the two things it does not
  check (the `Get` return type and the object's lifetime), and carries copy-pasteable non-generic and
  generic pool implementations plus notes on `UnityEngine.Pool`. The `Features` bullet that claimed
  the analyzer "works with any object pool implementation" is corrected, and `docs/rules/OPL001.md`
  links to the new section. Closes D2.

## [v0.8.6] - 2026-09-12

### Documentation
- The README `Requirements` section now carries a supported-version table listing Unity, Visual Studio,
  the .NET SDK, Rider and VS Code with their minimum supported versions in one place, replacing the
  prose bullets that mixed Unity's Roslyn-plugin rules with IDE versions. It also states that the
  package has no dependencies of its own and contributes nothing to build output. Closes D1.

## [v0.8.5] - 2026-09-12

### Changed
- The "Add pooling TODO comment" fix now uses array-specific wording on `new int[4]` / `new[] { 1, 2 }`,
  pointing at `ArrayPool<T>.Shared` and naming its two gotchas instead of suggesting an object pool
  that does not apply to arrays.

### Documentation
- Documented why array allocations get no automatic replacement fix: `ArrayPool<T>.Shared.Rent(n)`
  returns an array of length *at least* `n`, so substituting it changes the behaviour of any code that
  reads `Length`, and the rented buffer has to be returned on every exit path. `docs/rules/OPL001.md`
  gains an "Arrays" section listing the three by-hand fixes (hoist to a field, use a
  buffer-filling Unity API, rent and return explicitly); the README states the gap alongside the
  existing initializer gap. Pinned by `AddPoolingComment_OnArrayCreation_ProducesCompilableCode`,
  `AddPoolingComment_OnImplicitArrayCreation_UsesTheArrayWording`, and
  `AddPoolingComment_OnObjectCreation_KeepsTheObjectPoolWording` (53 tests total, up from 51).

## [v0.8.4] - 2026-09-12

### Added
- `docs/rules/OPL001.md`: what the rule flags (including the signature and `MonoBehaviour` conditions,
  and the lambda / local-function and call-graph boundaries), why per-frame allocation hurts under
  Unity's collector, the pool contract the replacement fix requires, and how to suppress the rule with
  `#pragma warning disable`, `[SuppressMessage]`, or `dotnet_diagnostic.OPL001.severity`.
- The OPL001 descriptor now carries a `helpLinkUri` pointing at that document, so the IDE lightbulb and
  the error list have somewhere to link and RS1015 has nothing to report. Pinned by
  `Descriptor_HasHelpLinkToTheRuleDoc` (51 tests total, up from 50).

## [v0.8.3] - 2026-09-12

### Changed
- The analyzer resolves `UnityEngine.MonoBehaviour` and `UnityEngine.Object` once per compilation from
  a `RegisterCompilationStartAction`, and registers its syntax node actions only when the compilation
  actually has `UnityEngine.MonoBehaviour`. A project without Unity no longer pays a semantic lookup
  per allocation: on a synthetic 60-file, 24000-allocation compilation, the analyzer's contribution to
  `GetAnalyzerDiagnosticsAsync` drops from a 574 ms median to 4 ms. The Unity path is unchanged within
  the noise of that harness (medians 614/616/641 ms before, 687/664/643 ms after, spread ~±100 ms).
- Hot path detection now compares symbols against the compilation's own `MonoBehaviour` and `Object`
  instead of matching type and namespace names, which is how the v0.8.1 look-alike rejection
  (`Game.UnityEngine.MonoBehaviour`, `UnityEngine.Outer.MonoBehaviour`) is now enforced.
- `UnityEngine.Object` is optional: a compilation that declares `MonoBehaviour` without it still gets
  allocation diagnostics, and no call is treated as `Object.Instantiate`.

### Added
- `AllocationInUpdateWithoutUnityEngine_DoesNotReport` and
  `AllocationInUpdateWithoutUnityObject_ReportsDiagnostic` pin the bail-out and the partial-stub
  behaviour (50 tests total, up from 48).

## [v0.8.2] - 2026-09-12

### Added
- Code-fix test coverage for the paths that were previously only verified by inspection, 10 tests
  (48 total, up from 38):
  - `ReplaceWithPoolGet` on an unqualified generic type (`new List<int>(16)` with `ListPool<T>`), and
    on a target-typed `new()` whose type arguments have to be recovered from the converted type
    (`List<int> list = new();`), which is the `ToMinimalDisplayString` path in `TryGetPoolName`.
  - `ReplaceWithPoolGet` on a qualified type name (`new Game.Enemy()`), which emits the pool name
    unqualified, plus the negative case where the pool lives in another namespace and is therefore
    out of scope at the allocation - the fix must not be offered there, because the rewritten code
    would not compile.
  - The array gap (F4): neither `new int[4]` nor `new[] { 1, 2 }` offers the replacement fix, only the
    TODO comment; a verifier test also pins the comment fix on an array creation.
  - An unresolvable allocated type (`new Missing()`), where `TryGetPoolName` bails out on
    `TypeKind.Error` and only the TODO comment is offered.
  - Fix-all (F5), which `WellKnownFixAllProviders.BatchFixer` provided but nothing exercised: one test
    rewrites three allocations across two hot-path methods in a single fix-all pass, and one applies
    the TODO comment to every allocation. The comment fix does not remove the diagnostic, so that test
    stops the incremental pass after the first fix (`CodeFixTestBehaviors.FixOne`) and compares the
    fix-all result against a separate `BatchFixedCode`.

## [v0.8.1] - 2026-09-12

### Fixed
- The `UnityEngine` namespace check compared `ContainingNamespace.Name`, which is only the innermost
  namespace segment. A user's own `Game.UnityEngine.MonoBehaviour` or `Game.UnityEngine.Object`
  therefore matched, so allocations in an `Update` on an unrelated base class produced false OPL001
  warnings, and a look-alike static `Instantiate` was reported as a Unity instantiation. This closes
  the gap noted in the v0.8.0 entry below.

  Both sites (`IsUnityMessage` and `IsInstantiateCall`) now go through a shared `IsUnityEngineType`
  helper that compares the full namespace via `ContainingNamespace.ToDisplayString()` and also
  requires the type to be top-level, so a type nested inside another `UnityEngine` type no longer
  matches either.

- 3 analyzer tests added for the look-alike namespace and nested-type cases (38 total, up from 35).

## [v0.8.0] - 2026-09-12

### Fixed
- `IsUnityMessage` matched on method name alone, so any method named `Update`,
  `OnTriggerStay`, `OnAnimatorIK` and so on was treated as a hot path even when Unity could never
  call it. A user-defined `void Update(float deltaTime)`, a `static void Update()` or a
  `void Update<T>()` on a `MonoBehaviour` all produced false OPL001 warnings.

  `HotPathMethodNames` is replaced by `HotPathMessageSignatures`, which maps each message to its
  Unity-declared parameter list: `OnTriggerStay(Collider)`, `OnTriggerStay2D(Collider2D)`,
  `OnCollisionStay(Collision)`, `OnCollisionStay2D(Collision2D)`, `OnAnimatorIK(int)`, and every
  other supported message parameterless. A candidate must now match arity and parameter type
  exactly, and `static`, generic and `ref`/`out`-parameter methods are rejected — Unity's
  reflection-based dispatch invokes none of them.

  Parameter types are matched by full display string (`UnityEngine.Collider`), except `int`, which
  is matched by `SpecialType.System_Int32`. Note that the namespace check itself is still the
  last-segment comparison tracked as A3; this change does not address that.

- 10 analyzer tests added for the new signature rules (35 total, up from 25); the Unity stub in the
  test project gained `Collider`, `Collider2D`, `Collision` and `Collision2D`.

## v0.7.1 - 2026-09-12 (never tagged; shipped inside v0.8.0)

### Added
- SourceLink, debug symbols and deterministic builds, none of which the package had. A consumer who
  stepped into the analyzer from a debugger got no source, and nothing tied a shipped DLL back to the
  commit it was built from.
  - `Directory.Build.props` (new, repo-wide): `PublishRepositoryUrl`, `EmbedUntrackedSources`,
    `DebugType=portable` and `Deterministic`, plus `ContinuousIntegrationBuild` gated on `CI=true`.
    The gate matters: path normalization to the `/_/` prefix is correct for a published build and
    wrong for a local one, where it breaks source resolution against the working tree.
  - `IncludeSymbols` + `SymbolPackageFormat=snupkg` on the package project, so `dotnet pack` now
    emits `ObjectPoolLinter.<version>.snupkg` alongside the `.nupkg` for publication to the NuGet
    symbol server.
  - The `.nuspec` now carries `<repository>` with the branch and commit SHA, and the analyzer PDBs
    carry a SourceLink document map pointing at `raw.githubusercontent.com` at that SHA.
- SourceLink is not referenced as a package. The .NET 8+ SDK imports `Microsoft.SourceLink.GitHub`
  in-box, and adding the 8.0.0 `PackageReference` on top of it only pulled in a
  `Microsoft.Build.Tasks.Git` with a known advisory (NU1902) — a problem once CI builds with
  `-warnaserror` (C1).
- Because the package project sets `IncludeBuildOutput=false`, NuGet skips symbol collection
  entirely (`_GetDebugSymbolsWithTfm` is gated on it, which also rules out
  `TfmSpecificDebugSymbolsFile`). The analyzer PDBs are listed as ordinary package files instead;
  they reach the `.snupkg`, and as a side effect also stay in the `.nupkg`.
- Verified: two `CI=true` builds of the analyzer produce byte-identical `.dll` and `.pdb`; the CI
  PDB normalizes source paths to `/_/` while the local one does not; 25/25 tests pass.

## [v0.7.0] - 2026-09-12

### Added
- A Unity install path, which the project did not have: Unity does not consume NuGet analyzers, so
  the NuGet package alone left Unity users with nothing to install.
  - `build/pack-unity.ps1` builds two artifacts into `artifacts/unity/`: a `.unitypackage` that
    imports the analyzer into `Assets/Plugins/ObjectPoolLinter/`, and a UPM tarball
    (`com.joezhuo.objectpoollinter-<version>.tgz`) installable through
    `Package Manager > Install package from tarball`. Both carry `ObjectPoolLinter.dll` and
    `ObjectPoolLinter.CodeFixes.dll` with generated `.meta` files that apply the `RoslynAnalyzer`
    label, disable every platform including Editor, and clear `validateReferences`. Asset GUIDs are
    derived from the asset path, so reimporting an upgrade replaces the previous assets in place.
  - `unity/package.json.in` and `unity/README.md`: the UPM manifest template (the version is stamped
    in from the package project at pack time) and the package description Unity shows in the Package
    Manager. The package deliberately contains no `.asmdef`, so it applies to Unity's predefined
    assemblies.
  - README: an `Installation` section covering both artifacts, the manual drop-in procedure with the
    exact importer settings, how analyzer scoping interacts with assembly definitions, how to
    silence OPL001, and how to build the artifacts locally.
- Verified on **Unity 6000.4.6f1**: both artifacts import without errors and a batch-mode compile
  reports OPL001 for an allocation in `Update` and not for one in `Start`. The UPM manifest declares
  `"unity": "2021.3"`, matching Unity's documented Roslyn 3.8 requirement for 2021.3 and 2022.3, but
  those versions are untested and the README says so.

## [v0.6.5] - 2026-09-11

### Changed
- Lowered the Roslyn reference from `Microsoft.CodeAnalysis.* 4.8.0` to `3.8.0` in the analyzer and
  code fix projects. 4.8.0 kept the analyzer from loading in any host older than Roslyn 4.8,
  including Unity 2021.3 and 2022.3, whose documentation requires Roslyn plugins built against 3.8.
  3.8 is the lowest version the code compiles against, because the analyzer handles C# 9 target-typed `new()`.
  The tests still run on Roslyn 4.8.0, so they exercise the analyzer in a newer host.
- README: a `Requirements` section that states the Roslyn 3.8 floor and which hosts it covers.

## [v0.6.4] - 2026-09-11

### Changed
- The analyzer and the code fix are now separate assemblies. `ObjectPoolLinter.dll` holds only the
  analyzer and references `Microsoft.CodeAnalysis.CSharp`; `ObjectPoolLinter.CodeFixes.dll`
  (`src/ObjectPoolLinter.CodeFixes`) holds the code fix and is the only one that references
  `Microsoft.CodeAnalysis.CSharp.Workspaces`. Workspaces is IDE-only and absent from compiler hosts
  such as Unity's, so an analyzer that hard-referenced it risked failing to load outside an IDE.
- Packing moved to a new `src/ObjectPoolLinter.Package` project, which ships both DLLs under
  `analyzers/dotnet/cs` along with the README and LICENSE. Build the package with
  `dotnet pack src/ObjectPoolLinter.Package -c Release`. The package ID stays `ObjectPoolLinter`;
  the analyzer project's own (unpacked) package ID is now `ObjectPoolLinter.Analyzer` so NuGet
  restore does not see two projects with the same ID.
- The sample project and the tests reference the code fix project alongside the analyzer.

## [v0.6.3] - 2026-09-10

### Added
- `LICENSE` at the repository root: the MIT License text. The project file already declared
  `PackageLicenseExpression=MIT`, but the repository itself carried no license text, so by default it was all rights reserved and nobody could legally use it. The license file is also packed at the package root.
- README: an MIT license badge under the title and a `License` section.

## [v0.6.2] - 2026-09-10

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

## [v0.6.1] - 2026-09-10

### Fixed
- building the package with `dotnet pack -c Release` creating an empty package (no analyzer)

## [v0.6.0] - 2026-09-08

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

## [v0.5.0] - 2026-09-06

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

## [v0.4.0] - 2026-09-04

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

## [v0.3.1] - 2026-09-04

### Added
- **`.gitattributes`** - Without it, files saved with CRLF while the committed blobs are LF show up as whole-file modifications and pollute real diffs. Normalize text files to LF in the repository, native on checkout, and mark common binary types explicitly.

## [v0.3.0] - 2026-09-04

### Fixed
- Allocations inside a lambda or anonymous method declared in a hot-path Unity message
  are no longer attributed to that message. How often the delegate runs is decided by
  whoever holds it, so a lambda registered as a callback in `Update()` produced a false
  positive. A lambda that is invoked on the spot is still reported.
- Allocations inside a local function declared in a hot-path Unity message are reported
  only when the declaring body actually calls that local function. A local function that
  is only converted to a delegate escapes the same way a lambda does.

## [v0.2.0] - 2026-09-02

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

## [v0.1.0] - 2026-09-02

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

[Unreleased]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.1...HEAD
[v0.9.1]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.0...v0.9.1
[v0.9.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.8...v0.9.0
[v0.8.8]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.7...v0.8.8
[v0.8.7]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.6...v0.8.7
[v0.8.6]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.5...v0.8.6
[v0.8.5]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.4...v0.8.5
[v0.8.4]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.3...v0.8.4
[v0.8.3]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.2...v0.8.3
[v0.8.2]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.1...v0.8.2
[v0.8.1]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.8.0...v0.8.1
[v0.8.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.7.0...v0.8.0
[v0.7.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.6.5...v0.7.0
[v0.6.5]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.6.4...v0.6.5
[v0.6.4]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.6.3...v0.6.4
[v0.6.3]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.6.2...v0.6.3
[v0.6.2]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.6.1...v0.6.2
[v0.6.1]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.6.0...v0.6.1
[v0.6.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.5.0...v0.6.0
[v0.5.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.4.0...v0.5.0
[v0.4.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.3.1...v0.4.0
[v0.3.1]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.3.0...v0.3.1
[v0.3.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.2.0...v0.3.0
[v0.2.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.1.0...v0.2.0
[v0.1.0]: https://github.com/joezhuo2/ObjectPoolLinter/releases/tag/v0.1.0
