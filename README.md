# ObjectPoolLinter

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Roslyn analyzer for Unity C# that detects object allocations in hot paths (like `Update`, `FixedUpdate`, etc.) and suggests using object pools to avoid garbage collection pressure and frame hitches.

## Features

- **Detects allocations in Unity hot paths**: Flags `new` object allocations and `Object.Instantiate()` calls inside frequently-called Unity methods
- **Covers 18 Unity message methods**: `Update`, `FixedUpdate`, `LateUpdate`, `OnGUI`, `OnTriggerStay`, `OnTriggerStay2D`, `OnCollisionStay`, `OnCollisionStay2D`, `OnMouseOver`, `OnMouseDrag`, `OnAnimatorMove`, `OnAnimatorIK`, `OnRenderObject`, `OnWillRenderObject`, `OnPreRender`, `OnPostRender`, `OnDrawGizmos`, `OnDrawGizmosSelected`
- **Code fixes**: Provides quick actions to replace allocations with object pool `Get()` calls or add TODO comments
- **Works with any object pool implementation**: The fix assumes a `{TypeName}Pool.Get()` pattern (e.g., `ListPool<int>.Get()`)

## Requirements

The analyzer and code fix are `netstandard2.0` assemblies built against **Roslyn 3.8**
(`Microsoft.CodeAnalysis.CSharp` 3.8.0), the minimum supported version. They load in any compiler or
IDE that ships Roslyn 3.8 or later.

| Host | Minimum supported | Notes |
| --- | --- | --- |
| Unity | 2021.3 | Unity requires Roslyn plugins built against 3.8 on 2021.3 and 2022.3, and against 4.3 or lower on Unity 6, so one 3.8 build covers all three. Verified on 6000.4.6f1; 2021.3 and 2022.3 are declared by the UPM package but untested here. |
| Visual Studio | 2019 16.8 | First release carrying Roslyn 3.8. |
| .NET SDK | 5.0 | Needed only to compile a project that references the analyzer, not by Unity. |
| Rider | Current releases | Rider ships its own Roslyn; no version older than the Roslyn 3.8 era has been tested. |
| VS Code | Current releases with the C# extension (or C# Dev Kit) | Analyzer diagnostics need background analysis enabled (`dotnet.backgroundAnalysis.analyzerDiagnosticsScope`). |

The NuGet package has no dependencies of its own and adds nothing to your build output: it is a
development-time analyzer reference only.

Hosts older than Roslyn 3.8 are not supported: the analyzer recognizes C# 9 target-typed `new()`,
which Roslyn 3.8 introduced.

## Installation

### Unity

Unity does not read NuGet packages, so the analyzer ships as Unity artifacts on the
[releases page](https://github.com/joezhuo2/ObjectPoolLinter/releases): a `.unitypackage` and a UPM
tarball (`com.joezhuo.objectpoollinter-<version>.tgz`). Both contain the same two DLLs with the
`RoslynAnalyzer` asset label and every platform disabled. Pick one:

**`.unitypackage`** — download it, then `Assets > Import Package > Custom Package...` and import.
The files land in `Assets/Plugins/ObjectPoolLinter/`.

**UPM tarball** — download it, then `Window > Package Manager > + > Install package from tarball...`.
Keep the `.tgz` inside your project (a folder such as `Packages/tarballs/`) or somewhere every
machine on the team can reach, because Unity records the path to the file, not a copy of it.

**Manual drop-in** — if you would rather not use either artifact, copy `ObjectPoolLinter.dll` and
`ObjectPoolLinter.CodeFixes.dll` (from the NuGet package's `analyzers/dotnet/cs/`, or from
`src/*/bin/Release/netstandard2.0/` after a local build) into a folder under `Assets/`, then for
each DLL in the Inspector:

1. Under `Select platforms for plugin`, clear **Any Platform** and leave every individual platform,
   including **Editor**, unchecked. Unity has to hand the DLL to the compiler rather than build it
   into a player or load it in the Editor.
2. Clear **Validate References**.
3. At the bottom of the Inspector, open the label picker and add the label `RoslynAnalyzer`, spelled
   exactly that way.
4. Click **Apply**.

Unity recompiles and OPL001 appears in the Console.

**Tested on Unity 6000.4.6f1** (Unity 6), where both artifacts import cleanly and OPL001 is reported
during a batch-mode compile. The analyzer targets Roslyn 3.8, which Unity's documentation names as
the required version for Roslyn plugins on 2021.3 and 2022.3, so the UPM package declares
`"unity": "2021.3"` as its minimum — but 2021.3 and 2022.3 have not been verified here.

#### Scoping the analyzer

A Roslyn analyzer applies to the assembly definition in its own folder or in the closest folder
above it. Imported at `Assets/Plugins/ObjectPoolLinter/` with no `.asmdef` alongside it, it applies
to Unity's predefined assemblies (`Assembly-CSharp` and friends). To cover your own assembly
definitions, either copy the DLLs into the folder of each `.asmdef` you want analyzed, or reference
the analyzer DLLs from those assembly definitions.

The UPM package contains no `.asmdef` either, so the packaged form also covers the predefined
assemblies.

#### Turning the rule off

Add `dotnet_diagnostic.OPL001.severity = none` to a `.editorconfig` at the project root, or wrap a
single allocation in `#pragma warning disable OPL001`.

### Unity code compiled outside the editor

Projects that reference the UnityEngine assemblies but build with `dotnet` — CI compile checks,
test harnesses, generated `.csproj` files — can take the analyzer from NuGet:

```
dotnet add package ObjectPoolLinter
```

This adds it as an analyzer reference, so the rule runs on every build with no extra wiring. The
package is not yet on nuget.org; until the first tagged release, reference the projects directly
(see `samples/SampleUnityCode/SampleUnityCode.csproj`) or use the Unity artifacts above.

OPL001 only fires on types deriving from `UnityEngine.MonoBehaviour`, so a project with no
UnityEngine reference gets no diagnostics.

### Building the Unity artifacts yourself

```
pwsh build/pack-unity.ps1
```

Writes `ObjectPoolLinter-<version>.unitypackage` and `com.joezhuo.objectpoollinter-<version>.tgz`
to `artifacts/unity/`. The version comes from
`src/ObjectPoolLinter.Package/ObjectPoolLinter.Package.csproj`.

## Usage

The analyzer runs automatically during build and in IDEs that support Roslyn analyzers (Visual Studio, VS Code with C# Dev Kit, Rider).

The rule it reports is documented in [docs/rules/OPL001.md](docs/rules/OPL001.md), which also covers
how to change its severity or suppress it.

### Code Fixes

When a diagnostic is reported, you can apply one of these quick fixes:

1. **Replace with object pool Get()** - Replaces `new Type(args)` with `TypePool.Get(args)`
2. **Add pooling TODO comment** - Adds a comment reminding you to use pooling (array allocations get
   an array-specific comment pointing at `ArrayPool<T>.Shared`)

Constructor arguments are forwarded to `Get()` unchanged, so `new Enemy(hp)` becomes
`EnemyPool.Get(hp)`. The fix assumes your pool exposes a `Get` overload matching the constructor
signature; if it does not, the result will not compile and the missing overload is the thing to add.

The fix is **not** offered when the allocation carries an object or collection initializer
(`new Enemy { Hp = 5 }`, `new List<int> { 1, 2 }`), because an initializer cannot be attached to a
method call and dropping it would silently lose code. Use the TODO-comment fix there and rewrite the
initializer by hand.

It is also **not** offered for array allocations (`new int[4]`, `new[] { 1, 2 }`). Those are still
reported, but there is no safe mechanical replacement: `ArrayPool<T>.Shared.Rent(4)` returns an array
of length *at least* 4 rather than exactly 4, and the buffer has to be returned on every exit path.
Only the TODO-comment fix is offered, and
[docs/rules/OPL001.md](docs/rules/OPL001.md#arrays) covers the ways to fix an array allocation by
hand.

## License

MIT - see [LICENSE](LICENSE).
