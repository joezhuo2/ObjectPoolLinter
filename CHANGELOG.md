# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [v1.6.1] - 2026-09-23

### Added
- **OPL009, component or object lookup in hot path** (Info, category Performance). Inside a hot path,
  `GetComponent<T>()`, `GetComponent(Type)`, `GetComponent(string)`, `TryGetComponent`,
  `GetComponentInChildren` and `GetComponentInParent` (on `Component` or `GameObject`) are reported
  when called on this object or on one reached through fields and properties (`gameObject`,
  `transform.parent`, `_target`, `Player.Instance`), with the advice to get it once in `Awake` or
  `Start` and keep it in a field. `GameObject.Find`, `FindWithTag`, `FindGameObjectWithTag`,
  `Object.FindObjectOfType`, `FindFirstObjectByType` and `FindAnyObjectByType` are reported as scene
  searches. None of these allocates, so OPL001 to OPL003 never covered them. A lookup on a parameter,
  a local, a method result, an array element or a struct member (`other.GetComponent<T>()` in
  `OnTriggerStay`, `hit.collider.GetComponent<T>()`) is not reported, since the object changes from
  call to call. The array-returning lookups stay with OPL003. The rule uses the same hot-path list,
  `.editorconfig` options and Burst exemption as OPL001 to OPL003, and the automatic suppressions
  cover it, so `if (_body == null) _body = GetComponent<Rigidbody>();` is suppressed as `OPLS004`.
- **OPL003 covers more allocating Unity APIs.** Array-returning members of `UnityEngine.SceneManagement`
  and `UnityEngine.AI` are now matched (`Scene.GetRootGameObjects()`, `NavMeshPath.corners`), along
  with members that build a new string or object on every call: `Application.dataPath`,
  `persistentDataPath`, `streamingAssetsPath` and `temporaryCachePath`; `Scene.name` and `Scene.path`;
  `Animator.GetParameter(int)` (which reads the `parameters` array internally) and
  `Animator.GetLayerName(int)`; `NavMeshAgent.path`; `JsonUtility.ToJson` and `JsonUtility.FromJson<T>`.
  `Animator.parameters` was already reported as an array property; `Animator.layerCount` and
  `parameterCount` return `int` and are deliberately not reported.
- 15 tests, for 315 in total. `samples/SampleUnityCode` gains a `Wheel` behaviour with a
  `GetComponent` in `Update`, a cached lookup the suppressor silences, a lookup on the collider passed
  to `OnTriggerStay` that stays quiet, and a `Renderer.sharedMaterials` read. Its `.editorconfig`
  raises OPL009 to a warning, and `build/verify-sample.ps1` now expects 17 warnings and recognizes the
  OPL009 messages.

### Changed
- New page [docs/rules/OPL009.md](docs/rules/OPL009.md). [README.md](README.md),
  [docs/configuration.md](docs/configuration.md), [docs/suppressions.md](docs/suppressions.md),
  [docs/rules/OPL001.md](docs/rules/OPL001.md), [docs/rules/OPL003.md](docs/rules/OPL003.md),
  [docs/rules/OPL008.md](docs/rules/OPL008.md) and the Unity package README describe OPL009 and the
  wider OPL003 coverage. The Known limitations section gains an entry for how OPL009 picks receivers.
- OPL009 is recorded under 1.6.1 in `AnalyzerReleases.Shipped.md`. No code fix is offered for it.

### Fixed
- [docs/rules/OPL003.md](docs/rules/OPL003.md) recommended `Renderer.sharedMaterials` as the
  non-allocating replacement for `Renderer.materials`. It is not: the `sharedMaterials` getter returns a
  new copy of the array on every read, though it does not instantiate materials. OPL003 keeps
  reporting it (a test now pins that), and the page recommends `GetMaterials(List<Material>)`,
  `GetSharedMaterials(List<Material>)` or the singular `sharedMaterial` instead.

## [v1.6.0] - 2026-09-23

### Added
- **OPL007, native container not disposed** (Warning, category Reliability). A `NativeArray<T>`,
  `NativeList<T>`, `NativeHashMap<TKey, TValue>` or any other struct marked `[NativeContainer]`,
  created with `new` and a constant allocator other than `Allocator.Temp`, `None` or `Invalid`, is
  reported when:
  - held in a local, it is not disposed on every path out of the method, local function or accessor
    that allocates it. The rule follows the method's control flow graph: each `return`, the end of
    the method, and a loop turning back to the allocation must pass a `Dispose()` or
    `Dispose(JobHandle)` call, a `finally` that disposes it, or a point where the container is handed
    on. A `using` declaration or statement satisfies it outright, and a path ending in `throw` does
    not count. The message says whether the container is never disposed or only missed on some paths;
  - stored in a field or auto-property of its own type, nothing in that type (across every partial
    declaration) disposes it or passes it to the project's own code;
  - thrown away as soon as it is made.

  A local that is returned, assigned anywhere, passed by `ref` or `out`, passed to a method or
  constructor declared in source, or captured by a lambda or local function is treated as handed on.
  A field marked `[DeallocateOnJobCompletion]`, a container created straight into another object
  (`new MoveJob { Data = new NativeArray<int>(...) }`), and allocations inside lambdas are not
  followed. The rule does not depend on hot paths and runs only when the compilation references
  `NativeContainerAttribute` and `Allocator`.
- **OPL008, asset loaded in hot path** (Info, category Performance). `Resources.Load`,
  `Resources.Load<T>`, `Resources.LoadAsync`, `Resources.LoadAsync<T>`, every `Addressables.Load*`
  method (`LoadAssetAsync`, `LoadAssetsAsync`, `LoadSceneAsync`, ...) and the `Load*` methods of an
  `AssetReference` and its subclasses are reported inside a hot path, with the advice to load once in
  `Awake` or `Start` and keep the result, or the handle, in a field. `Resources.LoadAll` stays with
  OPL003, which already reports it for returning an array; `InstantiateAsync` is not reported. The
  rule uses the same hot-path list, `.editorconfig` options and Burst exemption as OPL001 to OPL003.
- The automatic suppressions now cover OPL008 as well, so a load cached in a field
  (`if (_prefab == null) _prefab = Resources.Load<GameObject>("Enemy");`) is suppressed as `OPLS004`.
- 22 tests, for 300 in total. `samples/SampleUnityCode` gains a `Radar` behaviour with a
  `Resources.Load` in `Update`, a cached load the suppressor silences, a `NativeArray<int>` that an
  early return leaks, and a `using` and a `Temp` container that stay quiet. Its `.editorconfig` raises
  OPL008 to a warning, and `build/verify-sample.ps1` now expects 15 warnings and recognizes the OPL007
  and OPL008 messages.

### Changed
- New pages [docs/rules/OPL007.md](docs/rules/OPL007.md) and [docs/rules/OPL008.md](docs/rules/OPL008.md).
  [README.md](README.md), [docs/configuration.md](docs/configuration.md),
  [docs/suppressions.md](docs/suppressions.md), [docs/rules/OPL001.md](docs/rules/OPL001.md),
  [docs/rules/OPL003.md](docs/rules/OPL003.md), [docs/rules/OPL006.md](docs/rules/OPL006.md) and the
  Unity package README describe the two rules. The Known limitations section gains an entry for what
  OPL007 does not follow.
- OPL005 and OPL006 move from `AnalyzerReleases.Unshipped.md` to `AnalyzerReleases.Shipped.md` under
  the releases that introduced them (1.5.4 and 1.5.6), and OPL007 and OPL008 are recorded under 1.6.0.
- No code fix is offered for OPL007 or OPL008; the rule pages list the by-hand fixes.

### Fixed
- [docs/suppressions.md](docs/suppressions.md) no longer says Burst-compiled code is unhandled; the
  rules have skipped it since 1.5.6.

## [v1.5.6] - 2026-09-22

### Added
- **OPL002 reports `foreach` loops that box their enumerator.** A `foreach` whose `GetEnumerator`
  returns `IEnumerator<T>` or `IEnumerator` rather than a concrete enumerator allocates on every loop:
  over an `IList<T>`, `IReadOnlyList<T>`, `IEnumerable<T>` or `IDictionary<TKey, TValue>` the
  collection's struct enumerator is boxed, and a `ReadOnlyCollection<T>` or Unity `Transform` hands
  back a class enumerator. The loop header is reported as `enumerator for foreach over IList<int>`.
  `foreach` over `List<T>`, arrays, `string`, `Span<T>`, `Dictionary` and `HashSet<T>` stays quiet, as
  does a `foreach` over a LINQ call or an iterator method (the call is reported already) and
  `await foreach`. A struct collection that implements `IEnumerable<T>` only explicitly is also boxed
  itself, which the existing boxing check reports separately.
- **`object_pool_linter.enumerator_severity`**, the per-kind severity for the new kind. Diagnostics
  carry `AllocationKind` = `enumerator`.
- **Burst-compiled code is never reported.** OPL001, OPL002 and OPL003 stay quiet inside a method
  marked `[BurstCompile]`, or declared in a type marked `[BurstCompile]`, when the method is static or
  belongs to a struct: a job's `Execute`, an `ISystem`'s `OnUpdate`, a Burst static method. Burst
  rejects managed allocations when it compiles, so they cannot reach the player. An instance method of
  a class (a MonoBehaviour's `Update`) is never Burst-compiled and is still reported whatever it is
  marked with, and so is a `[BurstDiscard]` method.
- **OPL006, managed field in job struct** (Warning). A struct implementing `IJob`, `IJobFor`,
  `IJobParallelFor`, `IJobParallelForTransform`, a Collections or Entities job interface, or any
  interface marked `[JobProducerType]`, is reported for each instance field or auto-property of a
  reference type, or of a struct holding one at any depth (`has the type 'Payload', which holds
  'Data.Name' of reference type 'string'`). Unity throws `InvalidOperationException` when such a job is
  scheduled. Native containers (`[NativeContainer]`) and fields marked
  `[NativeSetClassTypeToNullOnSchedule]` are allowed. The rule does not depend on hot paths and runs
  only when the compilation references a job interface.
- 21 tests, for 278 in total. `samples/SampleUnityCode` gains a `Squad` behaviour with a `foreach` over
  an `IReadOnlyList<int>`, a `[BurstCompile]` job whose allocation stays unreported, and a job struct
  with a `string` field; `build/verify-sample.ps1` now expects 13 warnings and recognizes OPL006's
  message.

### Changed
- New page [docs/rules/OPL006.md](docs/rules/OPL006.md). [README.md](README.md),
  [docs/rules/OPL001.md](docs/rules/OPL001.md) (new *Burst-compiled code* section),
  [docs/rules/OPL002.md](docs/rules/OPL002.md), [docs/rules/OPL003.md](docs/rules/OPL003.md),
  [docs/configuration.md](docs/configuration.md) and the Unity package README describe the new
  detection, the Burst exemption and OPL006. The Known limitations section no longer lists `foreach`
  over an interface-typed collection as unreported.
- No code fix is offered for the enumerator or OPL006 diagnostics; the rule pages list the by-hand
  fixes.

## [v1.5.5] - 2026-09-22

### Added
- **OPL002 reports LINQ-style extension methods.** An extension method from any class whose `this`
  parameter is `IEnumerable<T>` or `IEnumerable` (MoreLINQ, a project's own `WhereAlive()`) is
  reported like `System.Linq.Enumerable`: it enumerates through the interface, boxing a `List<T>` or
  array enumerator. Such calls join an `Enumerable` chain, so `hp.Where(...).TakeEvery(2).ToList()`
  is reported once, as `LINQ Where().TakeEvery().ToList()`, and they fall under
  `object_pool_linter.linq_severity`. Extensions on `List<T>`, arrays, `IReadOnlyList<T>` or a
  generic `TSource` constrained to `IEnumerable<T>` are not LINQ-style and stay quiet.
- **OPL002 reports iterator state machines.** Calling a method that uses `yield return` or
  `yield break` in a hot path creates its state machine on every call, and is reported at the call as
  `iterator state machine for Spawn()`: a coroutine started every frame, a `foreach` over an iterator
  method, a local iterator function. A hot method that is itself an iterator (an
  `additional_hot_methods` entry that yields) is reported once, on its name. A `yield` inside a
  nested lambda or local function belongs to that function, not to the method around it.
- **OPL002 reports async state machines.** Calling an `async` method that returns `void`, `Task`,
  `Task<T>`, `ValueTask` or `ValueTask<T>` in a hot path is reported as
  `async state machine for SaveAsync()`, and `async void Update()` is reported on its name. Methods
  returning another task-like type (UniTask, Unity's `Awaitable`) or marked
  `[AsyncMethodBuilder(...)]` bring a pooling builder and are not reported.
- Methods compiled into other assemblies are recognized by the `[IteratorStateMachine]`,
  `[AsyncIteratorStateMachine]` and `[AsyncStateMachine]` attributes the compiler emits; methods in
  source by their syntax.
- **`object_pool_linter.iterator_severity` and `object_pool_linter.async_severity`**, per-kind
  severities for the two new kinds. Diagnostics carry `AllocationKind` = `iterator` or `async`.
- 14 tests, for 257 in total, including one that compiles a library to metadata to prove the
  attribute path. `samples/SampleUnityCode` gains a `Wave` behaviour with an iterator call, an async
  call and a LINQ-style extension in `Update`; `build/verify-sample.ps1` now expects 11 warnings.

### Changed
- [README.md](README.md), [docs/rules/OPL002.md](docs/rules/OPL002.md),
  [docs/configuration.md](docs/configuration.md) and the Unity package README describe the three new
  detections, the two new options, and what stays unreported (iterator property getters, reference
  assemblies that drop the state-machine attributes, pooled task-like types). The Known limitations
  section no longer lists iterator and `async` state machines as invisible.
- No code fix is offered for the new detections; the OPL002 rule page lists the by-hand fixes.

## [v1.5.4] - 2026-09-21

### Added
- **Generate the pool from the OPL001 code fix.** When nothing answers to `{TypeName}Pool` at the
  allocation site, **Generate `{TypeName}Pool` and use it here** writes the pool beside the class the
  allocation is in and rewrites the allocation to call it. The generated type is ordinary source in
  your own file: a `Stack<T>` of instances, `Get` forwarding to the constructor that was used,
  `Return`, `Clear`, `CountInactive`, and a `TODO` each for resetting a recycled instance and
  releasing what it holds. A generic type produces a generic pool carrying the same constraints, so
  `new List<int>()` writes `ListPool<T>` and calls `ListPool<int>.Get()`. Where `Return` is called
  stays the developer's decision. Not offered when the name is taken, for an abstract or static
  class, a `UnityEngine.Object`, a type nested in a generic type, a type or constructor out of reach
  from the file's namespace level, or a call site that omits an optional argument or expands a
  `params` list.
- **`ArrayPool<T>.Shared` code fix for OPL001 array allocations.** **Rent the array from
  `ArrayPool<T>.Shared`** rewrites `var buffer = new int[4]` into a `Rent` call wrapped in a
  `try`/`finally` that returns the buffer. The length asked for moves into its own local and every
  `buffer.Length` in the block is redirected to it, so the extra capacity a rented array may carry is
  never read as the logical length; the size expression is evaluated once, and a buffer of reference
  elements is returned with `clearArray: true`. Offered only where the buffer cannot escape its
  block: a local that is only indexed and read for `Length`, never passed on, assigned, returned,
  enumerated or captured.
- **`ObjectPoolSuppressionAnalyzer`**, a `DiagnosticSuppressor` that suppresses OPL001, OPL002 and
  OPL003 for four known-safe shapes: an allocation guarded by `Time.frameCount == 0` (`OPLS001`),
  inside `#if UNITY_EDITOR` (`OPLS002`), guarded by a static `bool` latch the guarded branch assigns
  (`OPLS003`), or assigned straight into a field (`OPLS004`). A suppressed diagnostic keeps its
  justification, so the IDE greys it out and the build drops it from the warning count. See
  [docs/suppressions.md](docs/suppressions.md).
- **`object_pool_linter.suppressions`**, which narrows that set: `all` (the default), `none`, or any
  of `first_frame`, `editor_only`, `static_latch` and `cached_field`. An unknown name is reported as
  [OPL004](docs/rules/OPL004.md) and leaves every pattern on.
- 29 tests, for 243 in total. `samples/SampleUnityCode` now carries a behaviour whose four
  allocations are each suppressed by one of the patterns, so `build/verify-sample.ps1` proves the
  suppressor over a real compilation: the sample still produces exactly its 8 expected warnings.

### Changed
- [README.md](README.md), [docs/rules/OPL001.md](docs/rules/OPL001.md),
  [docs/configuration.md](docs/configuration.md), [docs/rules/OPL004.md](docs/rules/OPL004.md) and
  the Unity package README document the two new fixes and the suppressions; OPL002's and OPL003's
  **When to suppress** sections point at the suppressor.

## [v1.5.3] - 2026-09-21

### Added
- **`[ObjectPool]` source generator.** Marking a class `[ObjectPool]` generates
  `{TypeName}Pool` in the class's namespace: one static `Get` overload per reachable constructor,
  forwarding its arguments unchanged (`params`, `ref`, `in` and default values included), plus
  `Return`, `Clear` and `CountInactive`. The generated type is exactly the shape OPL001's
  "Replace with object pool `Get()`" fix looks for, so that fix becomes available for any marked
  class. Generic types generate a generic pool with the same constraints, and the attribute itself is
  generated per assembly, so nothing extra has to be referenced. The generated source stays inside
  C# 7.3 and fully qualifies every type it names. Options: `PoolName` and `InitialCapacity`.
- **Reset hooks.** Each pool declares `static partial void Reinitialize(...)` per `Get` overload and
  `static partial void OnReturn(...)`, implemented in a `static partial class {TypeName}Pool` of your
  own. Left unimplemented the calls are erased, and a recycled instance keeps the state it was
  returned with.
- **OPL005: No object pool can be generated for this type** (Usage, Warning). Reported by the
  generator when `[ObjectPool]` cannot produce a pool: a static or abstract class, a type not visible
  from its own namespace, a `UnityEngine.Object` (those need `Instantiate`, not `new`), no reachable
  constructor, an invalid `PoolName` or negative `InitialCapacity`, a pool name already taken by a
  non-partial type, or two types competing for one pool name. See
  [docs/rules/OPL005.md](docs/rules/OPL005.md).
- [docs/source-generator.md](docs/source-generator.md): the generator guide - quick start, what is
  generated, the reset hooks, options, generics, what it refuses, and how to read the generated files.
- [docs/unity-package-manager.md](docs/unity-package-manager.md): UPM installation - package layout,
  installing from the tarball, the `Packages/manifest.json` entry, where to keep the `.tgz`, embedded
  packages, upgrading and pinning, GUID stability across versions, verifying the install, and
  troubleshooting.
- 21 tests, for 214 in total. Each one that expects a pool compiles the generated source together
  with code calling it.
- `samples/SampleUnityCode` now carries an `[ObjectPool]` class, a hand-written `Reinitialize` hook
  and a hot path that goes through the generated pool, so `build/verify-sample.ps1` covers the
  generator end to end.

### Changed
- The README gained a Features bullet for the generator, a "Generating the pool" section under
  [The pool contract](README.md#the-pool-contract), and links to both new guides;
  [OPL001](docs/rules/OPL001.md#code-fixes), [docs/configuration.md](docs/configuration.md) and the
  Unity package README point at them too.
- `AnalyzerReleases.Shipped.md` records OPL004 under 1.5.2, where it shipped; OPL005 is unshipped.

### Notes
- Source generators need Unity 2021.3 or newer, the same minimum the package already declared.
- The attribute is emitted during generation rather than at post-initialization, because Roslyn 3.8 -
  the version this analyzer targets, and the one Unity 2021.3 and 2022.3 require - has no
  post-initialization step. Candidate classes are therefore matched on the attribute's written name;
  an `ObjectPool` attribute that resolves to some other type is left alone.
- Pooling is still not free: nothing returns an instance for you, `Return` does not detect a double
  return, and a pool nothing is returned to is a leak with extra steps.

## [v1.5.2] - 2026-09-18

### Added
- **Parameter lists in `object_pool_linter.additional_hot_methods`.** `Tick(float)`,
  `EnemyBrain.Think()` and `Step(ref Vector3, float)` match only the overload with exactly those
  parameters. Types can be written as C# keywords (`float`), simple names (`Vector3`, `Single`) or
  namespace-qualified names (`UnityEngine.Vector3`, `System.Single`); `ref`, `out` and `in` must match,
  and `params` is ignored. Commas inside parentheses and angle brackets no longer split the list, so
  `Apply(Dictionary<int, string>, float)` is one entry. Entries without parentheses keep matching any
  parameter list.
- **`object_pool_linter.excluded_types_regex`.** One .NET regular expression, matched case-sensitively
  and unanchored against each type's namespace-qualified name (`Game.Debug.Overlay`), so
  `^Game\.Debug\.` excludes a namespace. Compiled once per project, with a 250 ms match timeout. Works
  alongside `excluded_types`.
- **Per-kind severity for OPL002.** `object_pool_linter.string_severity`, `delegate_severity`,
  `params_severity`, `linq_severity` and `boxing_severity` take `none`, `silent`, `suggestion`,
  `warning`, `error` or `default`. Each OPL002 diagnostic also carries its kind in the `AllocationKind`
  property. `dotnet_diagnostic.OPL002.severity`, when set, still wins, except that a kind set to `none`
  is never reported.
- **OPL004: Invalid ObjectPoolLinter option** (Configuration, Warning). Reports an
  `object_pool_linter.*` option whose name is not recognized, suggesting the closest known one
  (`'object_pool_linter.exlude_types' ... Did you mean 'object_pool_linter.excluded_types'?`), and one
  whose value cannot be used: a malformed hot-method entry, an unknown severity, an invalid regex.
  Reported once per project with no source location. See [docs/rules/OPL004.md](docs/rules/OPL004.md).
- [docs/configuration.md](docs/configuration.md): a setup guide covering where the `.editorconfig`
  goes, every option, per-kind severities, OPL004, and how nested and global config files combine.
- 22 tests, for 193 in total.

### Changed
- Options parsing moved out of `HotPathDetector` into a new `LinterOptions` class, shared by OPL002's
  severity lookup and OPL004. No behaviour change for existing options.
- The README, the Unity package README and the Configuration sections of
  [OPL001](docs/rules/OPL001.md#configuration) and [OPL002](docs/rules/OPL002.md#per-kind-severity)
  describe the new options and link to the configuration guide.

### Notes
- Finding misspelled option *names* needs a compiler that can list its options
  (`AnalyzerConfigOptions.Keys`). The analyzer builds against Roslyn 3.8, which cannot, so it looks the
  property up at run time; the .NET 10 SDK provides it. Elsewhere invalid values are still reported.
- OPL004 is a compilation-end diagnostic: IDEs show it only with full-solution analysis on.

## [v1.5.1] - 2026-09-17

### Added
- Code fixes for OPL003, in a new `UnityApiAllocationCodeFixProvider`. As with OPL002, each is offered
  only for the shapes it can rewrite without changing behaviour; the others are still reported, with
  no fix.
  - **Use CompareTag()**: `other.tag == "Player"` becomes `other.CompareTag("Player")`, and `!=` becomes
    `!other.CompareTag("Player")`, for `Component.tag` and `GameObject.tag`. Not offered when the other
    side is `null` or not a `string`, or when the string is on the left and both sides have side
    effects, since the call would evaluate them in the other order.
  - **Fill a reused List&lt;T&gt; with the non-allocating overload**:
    `var colliders = GetComponentsInChildren<Collider>();` becomes
    `GetComponentsInChildren<Collider>(_collidersBuffer);` then `var colliders = _collidersBuffer;`, with
    a `readonly List<T>` field and `using System.Collections.Generic;` added when missing. Uses of
    `colliders.Length` become `colliders.Count`. Works for `GetComponents<T>`,
    `GetComponentsInChildren<T>` and `GetComponentsInParent<T>` (which gets `false` for
    `includeInactive`, the array overload's default), in a local declaration or as a `foreach` source.
    Not offered when the local is used as anything but an element access, a `foreach` source or
    `.Length`.
  - **Use Input.touchCount and Input.GetTouch()**: `foreach (var touch in Input.touches)` becomes a
    `for` loop over `Input.touchCount` that starts with `var touch = Input.GetTouch(i);`,
    `Input.touches.Length` becomes `Input.touchCount`, and `Input.touches[i]` becomes
    `Input.GetTouch(i)`. Not offered when the element is written to, or when the array is stored.
- 17 tests, for 171 in total.

### Changed
- The syntax helpers and the `HotMethod` and `Rewrite` types shared by the code fixes moved out of
  `HiddenAllocationCodeFixProvider` into `CodeFixSupport.cs`. No behaviour change.
- [docs/rules/OPL003.md](docs/rules/OPL003.md) has a *Code fixes* section with before-and-after code and
  the exact conditions for each fix. The README, the Unity package README and the NuGet description
  mention the new fixes.

### Notes
- `CompareTag` logs an error for a tag that is not defined in the Tag Manager, where `==` quietly
  returned `false`.
- The buffer fix hands back the same list on every run, so a result kept past the frame must be copied.
- None of the OPL003 fixes has fix-all support: the buffer fix picks a free field name from the
  document as it stands, as the OPL002 fixes do.

## [v1.5.0] - 2026-09-16

### Added
- Code fixes for OPL002, in a new `HiddenAllocationCodeFixProvider`. Each is offered only for the shapes
  it can rewrite without changing behaviour; the others are still reported, with no fix.
  - **Cache the lambda in a field assigned in Awake()**: `Run(() => count + 1)` becomes `Run(_next)`,
    the lambda is assigned to `_next` in `Awake()` (added when the class has none), and each captured
    local becomes a field whose declaration turns into an assignment. Offered on a `MonoBehaviour` for
    lambdas that capture no parameter, call no local function and are not nested in another lambda,
    when every captured local is declared alone, with an initializer, outside a loop.
  - **Cache the delegate in a field assigned in Awake()**: `Action callback = Spawn;` becomes
    `Action callback = _spawn;` with `_spawn = Spawn;` in `Awake()`. Offered for methods of the class
    itself and static methods, not for `other.Method` or local functions.
  - **Build the string with a reused StringBuilder**: `var text = $"hp: {hp}";` becomes
    `_textBuilder.Clear().Append("hp: ").Append(hp);` and `var text = _textBuilder.ToString();`, with a
    `readonly StringBuilder` field and `using System.Text;` added when missing. Struct, enum and array
    holes are appended as `(object)value` so they format as interpolation does. Not offered for
    alignment or format clauses, conditionally evaluated strings, or statements that have side effects
    before the string.
  - **Replace LINQ with a loop filling a reused List&lt;T&gt;**: `Where(...).ToList()`,
    `Select(...).ToList()` and `Where(...).Select(...).ToList()` over a `List<T>` or an array, with
    single-parameter expression lambdas, become a `for` loop that clears and fills a `readonly List<T>`
    field, and the result refers to that list.
- 15 tests, for 154 in total.

### Changed
- [docs/rules/OPL002.md](docs/rules/OPL002.md) has a *Code fixes* section with before-and-after code and
  the exact conditions for each fix. The README, the Unity package README and the NuGet description
  mention the new fixes.

### Notes
- The new fixes have no fix-all support: each picks a free field name from the document as it stands,
  so fixes applied in one batch could pick the same name.
- The `StringBuilder` fix still allocates the final string, which OPL002 keeps reporting as
  `StringBuilder.ToString()`. The LINQ fix returns the same list on every run, so a result kept past the
  frame must be copied.

## [v1.4.0] - 2026-09-16

### Added
- OPL002 reports explicit string building in hot paths:
  - `string.Concat(...)`, every overload (two or more strings or objects, the `params` form, and
    `IEnumerable<string>`), as `string.Concat()`;
  - `string.Format(...)`, every overload including those taking an `IFormatProvider`, as
    `string.Format()`;
  - `StringBuilder.ToString()` and `StringBuilder.ToString(int, int)`, as `StringBuilder.ToString()`.
    Reusing a `StringBuilder` avoids intermediate strings, but each `ToString()` still allocates the
    result.
- The implicit `params` array built for a `string.Concat` or `string.Format` call, and the boxing of
  value-type arguments passed to one, are part of the call's allocation and are not reported again.
  `a + b`, which compiles to `string.Concat`, is still reported once as `string concatenation`.
- 5 tests, for 139 in total.

### Changed
- The OPL002 descriptor description, [docs/rules/OPL002.md](docs/rules/OPL002.md), the README and the
  Unity package README list the new constructs.

## [v1.3.0] - 2026-09-13

### Added
- OPL002, *Hidden allocation in hot path*, Info by default. It reports allocations with no `new` in
  the source, in the same hot paths OPL001 watches: string concatenation (reported once per `+` chain,
  constants excluded) and interpolation, lambdas that capture a local, a parameter or `this`,
  method groups converted to delegates (static ones only below C# 11, which caches them), implicit
  `params` arrays with at least one element, LINQ method chains (reported once, on the outermost call)
  and query expressions, boxing of an existing value (`object o = count;`), and struct calls to
  `object`, `ValueType` or `Enum` methods the struct does not override. It stays out of OPL001's way:
  `new` expressions, including `new Action(Spawn)` and a struct boxed as it is created, are not
  reported twice. It is Info rather than Warning so it does not bury OPL001; the Unity Console shows it
  only after `dotnet_diagnostic.OPL002.severity = warning`. Documented in
  [docs/rules/OPL002.md](docs/rules/OPL002.md).
- OPL003, *Allocating Unity API in hot path*, Warning by default. It reports any method or property
  getter declared in the core `UnityEngine` namespace that returns an array
  (`GetComponentsInChildren<T>()`, `Physics.RaycastAll`, `Camera.allCameras`, `Input.touches`,
  `Mesh.vertices`), plus reads of `Object.name`, `Component.tag` and `GameObject.tag`. Buffer-filling
  overloads, property writes, and managed packages such as `UnityEngine.UI` are not matched.
  `GameObject.Find` is not reported, because it allocates nothing. The message names the returned
  type: `'Physics.RaycastAll' returns a new 'RaycastHit[]' on every call inside the frequently-called
  method 'Update'.` Documented, with a non-allocating replacement for each API, in
  [docs/rules/OPL003.md](docs/rules/OPL003.md).
- Both rules honour `object_pool_linter.additional_hot_methods` and `object_pool_linter.excluded_types`.
- The sample interpolates a string and reads `Camera.allCameras` in `Update`, and its `.editorconfig`
  raises OPL002 to a warning. `build/verify-sample.ps1` now matches all three rules and identifies each
  warning by rule ID as well as allocation and method.
- 31 tests for the new rules, for 134 in total.

### Changed
- The hot-path detection and `.editorconfig` parsing moved out of `ObjectPoolAnalyzer` into a shared
  `HotPathDetector`, so the three rules agree on which methods are hot. No behavior change for OPL001.
- The README's `Known limitations`, the OPL001 page and the Unity package README describe the three
  rules and the gaps that remain, replacing the note that these allocations were planned as OPL002+.

## [v1.2.0] - 2026-09-13

### Added
- OPL001 reads two `.editorconfig` options.
  - `object_pool_linter.additional_hot_methods`: comma-separated method names treated as hot paths in
    addition to the 18 built-in Unity messages, for custom update loops (`Tick`, `Simulate`) and
    messages the list leaves out (`OnPreCull`). A bare name matches on any type with any signature;
    `Type.Method` limits the entry to one type.
  - `object_pool_linter.excluded_types`: comma-separated type names, simple or namespace-qualified,
    whose methods are never reported. It applies to methods declared on the listed type, not to
    derived types, and wins over `additional_hot_methods`.
  - Names are case-sensitive and options are read per file. Documented under
    [Configuration](docs/rules/OPL001.md#configuration), including the caveat that Unity's own editor
    compile has not been verified to pass the options to analyzers.
- The sample has an `.editorconfig` that adds `Tick` and excludes `LoadingScreen`;
  `build/verify-sample.ps1` expects the new `Tick` warning and none from `LoadingScreen`.

## [v1.1.0] - 2026-09-13

### Added
- OPL001 reports a struct that is boxed as it is created in a hot path: `object o = new MyStruct();`,
  a cast to `object` or an interface, or a struct passed or returned as one. The message names the
  target type: `'new MyStruct boxed to object' allocates inside the frequently-called method 'Update'.`
  Previously the value-type filter dropped these before the conversion was considered. Boxing an
  existing value (`object o = count;`), `new int?()` (which boxes to null) and struct calls to
  non-overridden `object` methods are still not reported.
- On a boxed struct, the TODO-comment fix suggests keeping the value typed as the struct or reusing a
  single box. The object-pool `Get()` fix is not offered there, because a pooled struct is boxed
  again at the same conversion.
- The sample boxes a `Vector3` in `Update`; `build/verify-sample.ps1` expects the new warning.

### Changed
- Removed a dead clause from the value-type check in the OPL001 analyzer:
  `type.IsValueType && type is not IArrayTypeSymbol` is now `type.IsValueType`. Array types are
  never value types, so the second clause could not change the result. No behavior change.

## [v1.0.0] - 2026-09-12

First published release: the `ObjectPoolLinter` package on nuget.org, plus the Unity
`.unitypackage` and UPM `.tgz` attached to the GitHub release. The 0.x versions below were never
published.

### Changed
- The OPL001 message names the allocation consistently and puts it first:
  `'new List<int>' allocates inside the frequently-called method 'Update'.` Allocations are named
  from the resolved type rather than the source text, so `new System.Collections.Generic.List<int>()`,
  `new List<int>()` and `new()` all read `new List<int>`, and arrays read as their type
  (`new int[]`) instead of echoing the size (`int[10]`). `Instantiate` calls read
  `'Instantiate' allocates inside ...` rather than `'Instantiate' is allocated inside ...`.
  Anything that parses the message text needs the new wording; `build/verify-sample.ps1` is updated.
- The message format arguments are in reading order: `{0}` is the allocation, `{1}` the method.
- OPL001 moved from `AnalyzerReleases.Unshipped.md` to a `Release 1.0.0` section in
  `AnalyzerReleases.Shipped.md`.

### Build
- The release workflow publishes to nuget.org with Trusted Publishing instead of the static
  `NUGET_API_KEY` secret. `NuGet/login@v1` exchanges the job's GitHub OIDC token for a one-hour API
  key, so no long-lived key is stored. The workflow now requests `id-token: write` and reads the
  nuget.org profile name from the `NUGET_USER` secret.

## [v0.9.5] - 2026-09-12

### Build
- `samples/SampleUnityCode` is part of `ObjectPoolLinter.slnx`, so every solution build and every CI
  run compiles it. Previously the sample was outside the solution and nothing built it.
- New `build/verify-sample.ps1` rebuilds the sample and asserts it produces exactly the expected
  OPL001 warnings: the two `List` allocations and the `Instantiate` call in `Update`, and the array
  in `FixedUpdate`. Warnings are matched by allocation and method name, not by line, and the match is
  exact, so both a lost warning and a new false positive (in `Start`, in a struct allocation, or in a
  class that is not a `MonoBehaviour`) fail the check. The build workflow runs it after the tests.
- The sample keeps OPL001 as a warning under `-warnaserror` (`WarningsNotAsErrors` in its project
  file) so CI can assert the warnings instead of failing on them.

### Fixed
- The sample no longer produces compiler warnings of its own under `Nullable` and `-warnaserror`:
  the Unity stubs return `null!`, `PlayerBehaviour.prefab` is initialized, and the unused `Vector3`
  local in `Update2` is discarded.

## [v0.9.4] - 2026-09-12

### Build
- The repository pins its .NET SDK. `global.json` requires SDK `10.0.100` with
  `rollForward: latestFeature`, so any .NET 10 feature band (`10.0.1xx` and later) builds the repo, but
  an older SDK fails fast with an error naming the required version instead of building with a
  different compiler. Prerelease SDKs are not picked up. The build and release workflows now install
  the SDK from `global.json` (`global-json-file`) instead of a hardcoded `10.0.x`, so CI and
  contributors resolve the SDK from one place.

## [v0.9.3] - 2026-09-12

### Build
- The repository has a release workflow. `.github/workflows/release.yml` runs when a `v*` tag is pushed.
  It takes the version from the tag (`v1.2.3` packs as `1.2.3`, `v1.2.3-rc.1` as a prerelease) and
  fails on a tag that is not SemVer. It also fails on a tag with no matching `## [<tag>]` section in
  this changelog, because that section becomes the release notes. Then it builds with `-warnaserror`,
  runs the tests, packs the `.nupkg` and `.snupkg`, and runs `build/pack-unity.ps1` to produce the
  `.unitypackage` and UPM `.tgz`. Every file is uploaded as a `release-<version>` workflow artifact.
  For 1.0.0 and later, the workflow pushes the package to nuget.org with the `NUGET_API_KEY` secret
  (`dotnet nuget push` sends the `.snupkg` next to it to the symbol server) and creates the GitHub
  release with the changelog section as its notes and the two Unity artifacts and both NuGet packages
  attached. A `0.x` tag is a dry run: it builds and uploads everything but publishes nothing, since
  1.0.0 is the first published release.
- `build/pack-unity.ps1` takes a `-Version` parameter that overrides the `<Version>` read from the
  package project, so the release workflow stamps the Unity artifacts with the tag's version.

## [v0.9.2] - 2026-09-12

### Build
- The repository has CI. `.github/workflows/build.yml` runs on every push to `main`, every pull request
  and on manual dispatch: it restores, builds the solution in Release with `-warnaserror`, runs the
  tests, and packs `ObjectPoolLinter.Package`. The `.nupkg` and `.snupkg` are uploaded as the `nuget`
  artifact (the job fails if pack produced neither), and the TRX test results are uploaded even when
  tests fail. Actions sets `CI=true`, so these builds get `ContinuousIntegrationBuild` from
  `Directory.Build.props`.

## [v0.9.1] - 2026-09-12

### Tests
- Analyzer tests no longer hardcode diagnostic spans. The seven `.WithSpan(line, col, line, col)`
  expectations are replaced by markup in the test source (`{|#0:...|}`) with `.WithLocation(0)`, so
  reformatting a test source does not break its expectation. Message arguments are still checked.

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
  not. 84 tests, up from 53.

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
  at it.

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
  links to the new section.

## [v0.8.6] - 2026-09-12

### Documentation
- The README `Requirements` section now carries a supported-version table listing Unity, Visual Studio,
  the .NET SDK, Rider and VS Code with their minimum supported versions in one place, replacing the
  prose bullets that mixed Unity's Roslyn-plugin rules with IDE versions. It also states that the
  package has no dependencies of its own and contributes nothing to build output.

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
  warnings, and a look-alike static `Instantiate` was reported as a Unity instantiation. 

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

[Unreleased]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.6.1...HEAD
[v1.6.1]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.6.0...v1.6.1
[v1.6.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.6...v1.6.0
[v1.5.6]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.5...v1.5.6
[v1.5.5]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.4...v1.5.5
[v1.5.4]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.3...v1.5.4
[v1.5.3]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.2...v1.5.3
[v1.5.2]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.1...v1.5.2
[v1.5.1]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.5.0...v1.5.1
[v1.5.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.4.0...v1.5.0
[v1.4.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.3.0...v1.4.0
[v1.3.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.2.0...v1.3.0
[v1.2.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.1.0...v1.2.0
[v1.1.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v1.0.0...v1.1.0
[v1.0.0]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.5...v1.0.0
[v0.9.5]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.4...v0.9.5
[v0.9.4]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.3...v0.9.4
[v0.9.3]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.2...v0.9.3
[v0.9.2]: https://github.com/joezhuo2/ObjectPoolLinter/compare/v0.9.1...v0.9.2
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
