# ObjectPoolLinter

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Roslyn analyzer for Unity C# that detects allocations in hot paths (like `Update`, `FixedUpdate`, etc.) and suggests using object pools, cached results or non-allocating APIs to avoid garbage collection pressure and frame hitches.

## Features

- **Three rules for Unity hot paths**:

  | Rule | Reports | Default |
  | --- | --- | --- |
  | [OPL001](docs/rules/OPL001.md) | `new` allocations, structs boxed on creation (`object o = new MyStruct();`), and `Object.Instantiate()` | Warning |
  | [OPL002](docs/rules/OPL002.md) | Allocations with no `new` in the source: string concatenation and interpolation, `string.Concat`, `string.Format`, `StringBuilder.ToString()`, capturing lambdas, method-group delegates, implicit `params` arrays, LINQ, and boxing | Info |
  | [OPL003](docs/rules/OPL003.md) | Unity APIs that return a new array (`GetComponentsInChildren<T>()`, `Physics.RaycastAll`, `Camera.allCameras`, `Input.touches`) and `name` / `tag` | Warning |

- **Covers 18 Unity message methods**: `Update`, `FixedUpdate`, `LateUpdate`, `OnGUI`, `OnTriggerStay`, `OnTriggerStay2D`, `OnCollisionStay`, `OnCollisionStay2D`, `OnMouseOver`, `OnMouseDrag`, `OnAnimatorMove`, `OnAnimatorIK`, `OnRenderObject`, `OnWillRenderObject`, `OnPreRender`, `OnPostRender`, `OnDrawGizmos`, `OnDrawGizmosSelected`
- **Configurable**: add your own hot methods (`Tick`, `OnPreCull`, custom update loops) or exclude types from `.editorconfig` - see [Configuration](#configuration)
- **Code fixes** (OPL001): Provides quick actions to replace allocations with object pool `Get()` calls or add TODO comments
- **Targets a specific pool shape**: the replacement fix rewrites `new Enemy(hp)` to `EnemyPool.Get(hp)`, so it needs a type named `{TypeName}Pool` with a static `Get`, already in scope. It does not create the pool - see [The pool contract](#the-pool-contract)

What the rules deliberately do not cover — call-graph analysis, allocations the analyzer cannot see
statically, and the allocation shapes with no replacement fix — is listed under
[Known limitations](#known-limitations).

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

Unity recompiles and OPL001 and OPL003 appear in the Console. OPL002 is Info by default, which the
Console does not show; raise it to a warning (below) to see it there.

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

#### Changing a rule's severity

Add `dotnet_diagnostic.<rule>.severity = <level>` to a `.editorconfig` at the project root, or wrap a
single allocation in `#pragma warning disable <rule>`:

```ini
[*.cs]
dotnet_diagnostic.OPL001.severity = none      # turn OPL001 off
dotnet_diagnostic.OPL002.severity = warning   # show OPL002 in the Unity Console
```

### Unity code compiled outside the editor

Projects that reference the UnityEngine assemblies but build with `dotnet` — CI compile checks,
test harnesses, generated `.csproj` files — can take the analyzer from NuGet:

```
dotnet add package ObjectPoolLinter
```

This adds it as an analyzer reference, so the rule runs on every build with no extra wiring. The
package is not yet on nuget.org; until the first tagged release, reference the projects directly
(see `samples/SampleUnityCode/SampleUnityCode.csproj`) or use the Unity artifacts above.

The rules only run when the compilation references `UnityEngine.MonoBehaviour`, so a project with no
UnityEngine reference gets no diagnostics. The built-in messages fire only on types deriving from
`MonoBehaviour`; methods added through [Configuration](#configuration) fire on any type.

### Building from source

The repository builds with the .NET 10 SDK. `global.json` pins `10.0.100` with
`rollForward: latestFeature`, so any installed `10.0.1xx` or later feature band is used; an older SDK
fails with an error naming the required version. CI installs the SDK from the same file.

```
dotnet build ObjectPoolLinter.slnx -c Release
dotnet test ObjectPoolLinter.slnx -c Release
```

The solution includes `samples/SampleUnityCode`, a small MonoBehaviour compiled against Unity stubs
with the analyzer attached. Building the solution prints its OPL001, OPL002 and OPL003 warnings; those
are expected. To
check that the sample reports exactly the warnings it should, as CI does:

```
pwsh build/verify-sample.ps1
```

This SDK requirement applies only to building this repository. Projects that consume the analyzer
need only the hosts listed under [Requirements](#requirements).

### Building the Unity artifacts yourself

```
pwsh build/pack-unity.ps1
```

Writes `ObjectPoolLinter-<version>.unitypackage` and `com.joezhuo.objectpoollinter-<version>.tgz`
to `artifacts/unity/`. The version comes from
`src/ObjectPoolLinter.Package/ObjectPoolLinter.Package.csproj` unless you pass `-Version <version>`.
Add `-SkipBuild` to package the DLLs already in `bin/Release` instead of building first.

### Releases

Pushing a `v*` tag runs `.github/workflows/release.yml`. The version comes from the tag, the release
notes come from the `## [<tag>]` section of `CHANGELOG.md`, and the run builds, tests, and packs the
NuGet package and both Unity artifacts. From 1.0.0 on it pushes the package to nuget.org and creates
the GitHub release with the artifacts attached. `0.x` tags are dry runs that only upload the files
as a workflow artifact.

Publishing uses nuget.org [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so the repository stores no API key. It needs two things set up once: a Trusted Publishing policy on
nuget.org for this repository and the `release.yml` workflow, and a `NUGET_USER` repository secret
holding the nuget.org profile name (not the email address) that owns the policy.

## Usage

The analyzer runs automatically during build and in IDEs that support Roslyn analyzers (Visual Studio, VS Code with C# Dev Kit, Rider).

Each rule is documented in its own page, which also covers how to change its severity or suppress it:
[OPL001](docs/rules/OPL001.md), [OPL002](docs/rules/OPL002.md), [OPL003](docs/rules/OPL003.md).

Their boundaries are listed under [Known limitations](#known-limitations); a clean run is not a claim
that a method allocates nothing.

### Configuration

The 18 built-in Unity messages can be extended, and types excluded, from `.editorconfig`. The options
apply to all three rules:

```ini
[*.cs]
# Also treat these methods as hot paths: any type, any signature. Type.Method limits one to a type.
object_pool_linter.additional_hot_methods = Tick, Simulate, OnPreCull, EnemyBrain.Think

# Never report inside methods declared on these types (simple or namespace-qualified names).
object_pool_linter.excluded_types = LoadingScreen, Game.Editor.GizmoDrawer
```

Names are case-sensitive, and an exclusion does not extend to derived types. The matching rules, and
the caveat that Unity's own editor compile has not been verified to pass these options through, are
in [docs/rules/OPL001.md](docs/rules/OPL001.md#configuration).

### Code Fixes

When an OPL001 diagnostic is reported, you can apply one of these quick fixes (OPL002 and OPL003 have
none; their pages list the manual fix for each construct):

1. **Replace with object pool Get()** - Replaces `new Type(args)` with `TypePool.Get(args)`
2. **Add pooling TODO comment** - Adds a comment reminding you to use pooling (array allocations get
   an array-specific comment pointing at `ArrayPool<T>.Shared`; boxed structs get one about avoiding
   the box)

Constructor arguments are forwarded to `Get()` unchanged, so `new Enemy(hp)` becomes
`EnemyPool.Get(hp)`. The pool itself is yours to write: the fix is only offered when a matching pool
type is already in scope, and it never creates one. [The pool contract](#the-pool-contract) below
spells out exactly what "matching" means and includes a minimal pool you can copy.

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

## The pool contract

The **Replace with object pool `Get()`** fix does not write a pool for you. It rewrites the
allocation and nothing else, and it is only offered when a pool matching the contract below is
already in scope. When no such type exists, the fix is not offered at all and only the TODO-comment
fix appears.

A pool satisfies the contract when all of these hold at the allocation site:

| Requirement | Detail |
| --- | --- |
| Name | Exactly `{TypeName}Pool`, where `TypeName` is the *unqualified* name of the allocated type. `new Enemy()` looks for `EnemyPool`; `new System.Collections.Generic.List<int>()` looks for `ListPool`, never `System.Collections.Generic.ListPool`. |
| Visibility | Resolvable by simple name at the allocation site: the same namespace, an enclosing type, or a namespace already imported by a `using`. The fix never adds a `using` and never qualifies the name it writes. |
| Generic arity | Must match the allocated type's type-argument count, and the type arguments are copied over. `new List<int>()` becomes `ListPool<int>.Get()`, so `ListPool` has to be declared as `ListPool<T>`. |
| `Get` member | A **static** method named exactly `Get`, accessible from the allocation site. An instance `Get` on a pool field, a singleton or an `ObjectPool<T>` instance does not qualify. |
| `Get` arity | Must accept the constructor's argument count, because arguments are forwarded unchanged: `new Enemy(hp)` needs a `Get` overload taking one argument. Optional parameters and `params` are honoured. |

Two things the fix does not check:

- **The return type.** A `Get` that returns something other than the allocated type still satisfies
  the lookup, and the rewritten call will not compile. That is a loud failure, not a silent one.
- **The lifetime.** Nothing is inserted to hand the object back. Writing the release call is yours to
  do; a pool nothing is returned to is a leak with extra steps.

Target-typed `new()` is handled the same way: the pool name comes from the type the expression is
converted to, so `Enemy e = new();` looks for `EnemyPool` just as `new Enemy()` does.

### A minimal pool

Copy-pasteable, C# 7.3, nothing beyond `System.Collections.Generic`, so it compiles in Unity 2021.3
and later as-is. It is not thread-safe, which is enough for the main-thread Unity messages this rule
watches.

```csharp
using System.Collections.Generic;

public static class EnemyPool
{
    private static readonly Stack<Enemy> Free = new Stack<Enemy>();

    // Matches new Enemy(hp): one argument, forwarded unchanged.
    public static Enemy Get(int hp)
    {
        if (Free.Count == 0) return new Enemy(hp);

        var enemy = Free.Pop();
        enemy.Hp = hp; // reset whatever the constructor would have set
        return enemy;
    }

    public static void Release(Enemy enemy)
    {
        Free.Push(enemy);
    }
}
```

Resetting a recycled instance is the pool's job, not the analyzer's. `Get` has to return an object in
the state the constructor would have produced, or pooling introduces bugs the allocation never had.

The generic form, which turns `new List<int>()` into `ListPool<int>.Get()`:

```csharp
using System.Collections.Generic;

public static class ListPool<T>
{
    private static readonly Stack<List<T>> Free = new Stack<List<T>>();

    public static List<T> Get()
    {
        return Free.Count > 0 ? Free.Pop() : new List<T>();
    }

    public static void Release(List<T> list)
    {
        list.Clear();
        Free.Push(list);
    }
}
```

### Pools you may already have

Unity 2021.1 and later ship the `UnityEngine.Pool` namespace, whose `ListPool<T>`, `HashSetPool<T>`
and `DictionaryPool<TKey, TValue>` each expose a static `Get()` and so match the contract by name and
arity. With `using UnityEngine.Pool;` in the file, the fix rewrites `new List<int>()` to
`ListPool<int>.Get()` against Unity's own pool; release with `ListPool<int>.Release(list)`.

`UnityEngine.Pool.ObjectPool<T>` does not match: its `Get` is an instance method, and the type is not
named `{TypeName}Pool` for any pooled type. Use it by hand, or wrap it in a static class named for
the type you are pooling.

## Known limitations

The rules are deliberately narrow: they report allocations they can see in a hot-path body, and OPL001
offers a fix only when that fix cannot change behaviour. The gaps below are known and intentional for
this version, not bugs.

**Only allocations written directly in the message body are reported.** There is no call-graph
analysis, so `void Update() { Spawn(); }` is silent no matter what `Spawn()` allocates. An allocation
inside a lambda or an anonymous method is attributed to whoever invokes the delegate, not to the
message, so it is reported only when the lambda is invoked in place; a local function is reported
only when the declaring body actually calls it. Converting either to a delegate — registering it as a
callback — escapes the rules on purpose, because how often it runs is no longer decided by the frame.
(Creating that delegate in the hot path is itself an allocation, which OPL002 reports when the lambda
captures state.)

**Each rule covers one family of allocation.** OPL001 matches `new` expressions and
`UnityEngine.Object.Instantiate`. OPL002 matches string concatenation and interpolation,
`string.Concat`, `string.Format`, `StringBuilder.ToString()`, capturing lambdas, method-group delegates, implicit `params` arrays, LINQ and boxing, and is Info by default, so
the Unity Console does not show it until it is raised to a warning. OPL003 matches `UnityEngine`
members that return an array, plus `name` and `tag`. Still not reported by any rule:

- iterator and `async` state machines, `foreach` over an interface-typed collection (which boxes the
  enumerator), and allocations inside base class library or Unity methods other than the ones above;
- `new int?()` (which boxes to null) and boxing of an unconstrained generic `T`, which depends on the
  type argument at run time;
- Unity APIs that cost CPU time without allocating, such as `GameObject.Find` and `GetComponent<T>()`;
- Unity string properties other than `name` and `tag`, and array-returning members of managed packages
  outside the core `UnityEngine` namespace (`UnityEngine.UI` and others).

A clean run is not a claim that a method is allocation-free; a profiler is the final word.

**Array allocations get no replacement fix.** `new int[4]` and `new[] { 1, 2 }` are reported, but only
the TODO-comment fix is offered, because `ArrayPool<T>.Shared.Rent(4)` returns an array of length *at
least* 4 and the buffer must be returned on every exit path. See
[docs/rules/OPL001.md](docs/rules/OPL001.md#arrays) for the three by-hand fixes.

**Allocations with an initializer get no replacement fix.** `new Enemy { Hp = 5 }` and
`new List<int> { 1, 2 }` are reported, but an initializer cannot be carried onto a method call, so the
fix would have to drop it. Rewrite the initializer by hand after taking the object from the pool.

**The replacement fix never writes the pool and never releases the object.** It is offered only when a
pool matching [the pool contract](#the-pool-contract) is already in scope, and it inserts no release
call — returning the object is yours to do.

## License

MIT - see [LICENSE](LICENSE).
