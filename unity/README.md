# ObjectPoolLinter (Unity package)

A Roslyn analyzer that flags allocations inside Unity hot paths (`Update`, `FixedUpdate`,
`OnTriggerStay`, and 15 other per-frame messages):

- **OPL001** (warning): `new` allocations and `Instantiate`, with code fixes that route the allocation
  through an object pool.
- **OPL002** (info): string concatenation and interpolation, `string.Concat`, `string.Format`,
  `StringBuilder.ToString()`, capturing lambdas, method-group delegates,
  implicit `params` arrays, LINQ (including LINQ-style extension methods on `IEnumerable<T>`),
  boxing, calls to iterator and `async` methods, and `foreach` over an interface-typed collection
  (which boxes the enumerator), with code fixes that cache lambdas and delegates in
  `Awake()`, build interpolated strings with a reused `StringBuilder`, and turn simple LINQ chains into
  a loop.
- **OPL003** (warning): Unity APIs that return a new array, such as `GetComponentsInChildren<T>()`,
  `Physics.RaycastAll`, `Camera.allCameras` and `Renderer.sharedMaterials`, plus `name`, `tag`,
  `Application.dataPath`, `Scene.name`, `NavMeshAgent.path` and a few others, with code fixes that turn
  `tag ==` into `CompareTag()`, fill a reused list with `GetComponents*<T>(List<T>)`, and read
  `Input.touches` through `Input.touchCount` and `Input.GetTouch(i)`.
- **OPL005** (warning): a class marked `[ObjectPool]` that the source generator cannot write a pool
  for - an abstract or static class, a `UnityEngine.Object`, or one with no reachable constructor.
- **OPL006** (warning): a field of managed type (a class, array, `string`, or a struct holding one) in
  a job struct (`IJob`, `IJobFor`, `IJobParallelFor`, ...), which makes `Schedule()` throw
  `InvalidOperationException`.
- **OPL007** (warning): a `NativeArray<T>`, `NativeList<T>` or other native container allocated with
  `Allocator.TempJob` or `Allocator.Persistent` that is not disposed on every path out of its method,
  or, kept in a field, never disposed by its type. Native memory is never garbage-collected.
- **OPL008** (info): `Resources.Load`, `Resources.LoadAsync`, `Addressables.LoadAssetAsync` and the
  other Addressables and `AssetReference` loads inside a hot path. Load once in `Awake` or `Start` and
  keep the result in a field.
- **OPL009** (info): `GetComponent<T>()`, `TryGetComponent`, `GetComponentInChildren` and
  `GetComponentInParent` on the same object, and `GameObject.Find`, `FindWithTag` and
  `FindObjectOfType`, inside a hot path. Look it up once in `Awake` or `Start` and keep it in a field.

Nothing is reported inside code Burst compiles (a `[BurstCompile]` job or static method): Burst
rejects managed allocations itself.

OPL001's code fixes also write the pool class itself when none exists, and rewrite a local array
allocation into `ArrayPool<T>.Shared.Rent` with a `try`/`finally` that returns it.

Allocations that are known not to run every frame are suppressed automatically: behind
`if (Time.frameCount == 0)`, inside `#if UNITY_EDITOR`, behind a static `bool` latch the guarded
branch sets, or assigned straight into a field. Set `object_pool_linter.suppressions` in
`.editorconfig` to narrow that to the patterns you want, or to `none`. Full guide:
`docs/suppressions.md` in the repository.

## Generating pools

Mark a plain C# class `[ObjectPool]` and `{TypeName}Pool` is generated for you, in the shape OPL001's
code fix rewrites allocations into:

```csharp
using ObjectPoolLinter;

[ObjectPool]
public class Bullet
{
    public float Speed;

    public Bullet(float speed) { Speed = speed; }
}

// elsewhere
var bullet = BulletPool.Get(12f);
BulletPool.Return(bullet);
```

The attribute is generated too, once per assembly, so nothing else has to be imported. Resetting a
recycled instance is yours to do, through a `Reinitialize` partial method the generator declares.
`MonoBehaviour` and other `UnityEngine.Object` types are refused (OPL005): those are created with
`Instantiate`, not `new`. Full guide: `docs/source-generator.md` in the repository.

## Scope

Both DLLs live in `RoslynAnalyzers/` and carry the `RoslynAnalyzer` asset label with every platform
disabled, so Unity hands them to the C# compiler instead of building them into a player.

Because this package contains no assembly definition file, the analyzer applies to Unity's
predefined assemblies (`Assembly-CSharp` and friends). To run it against your own assembly
definitions as well, see "Scoping the analyzer" in the repository README.

`ObjectPoolLinter.CodeFixes.dll` is only used by IDEs (Rider, Visual Studio, VS Code). The compiler
ignores it; Unity's compile pipeline never loads `Microsoft.CodeAnalysis.CSharp.Workspaces`.

## Configuring hot methods

List extra hot methods or excluded types in a `.editorconfig` at your project root, next to
`Assets/`. The options apply to OPL001, OPL002, OPL003, OPL008 and OPL009:

```ini
[*.cs]
object_pool_linter.additional_hot_methods = Tick(float), OnPreCull
object_pool_linter.excluded_types = LoadingScreen
object_pool_linter.excluded_types_regex = ^Game\.Debug\.
```

IDEs honour these options. Unity's own editor compile has not been verified to pass them to
analyzers. A misspelled option is reported as OPL004. Details: `docs/configuration.md` in the
repository.

## Changing a rule's severity

The Unity Console shows warnings and errors only, so OPL002, OPL008 and OPL009 appear there only
after they are raised.
Add lines like these to a `.editorconfig` at your project root, or use `#pragma warning disable <rule>`
around a specific allocation:

```ini
[*.cs]
dotnet_diagnostic.OPL001.severity = none      # turn OPL001 off
dotnet_diagnostic.OPL002.severity = warning   # show OPL002 in the Console
dotnet_diagnostic.OPL008.severity = warning   # show OPL008 in the Console
dotnet_diagnostic.OPL009.severity = warning   # show OPL009 in the Console
```

Or raise only some kinds of OPL002 (leave `dotnet_diagnostic.OPL002.severity` unset for these to
apply):

```ini
[*.cs]
object_pool_linter.linq_severity = warning
object_pool_linter.boxing_severity = warning
object_pool_linter.iterator_severity = warning   # coroutines started every frame
object_pool_linter.enumerator_severity = warning # foreach over IList<T>, IEnumerable<T>, Transform
```

Installing, upgrading and pinning this package through the Package Manager, and why its asset GUIDs
stay stable across versions: `docs/unity-package-manager.md` in the repository.

Documentation and issues: https://github.com/joezhuo2/ObjectPoolLinter
