# Generating pools with `[ObjectPool]`

OPL001 tells you an allocation is in a hot path, and its code fix rewrites `new Enemy(hp)` into
`EnemyPool.Get(hp)` - but only when a pool matching
[the contract](../README.md#the-pool-contract) already exists. Writing that pool is the boring part.

Since 1.5.3 the analyzer package also carries a source generator: mark a class `[ObjectPool]` and the
`{TypeName}Pool` class is written for you, in the same shape the code fix looks for.

Since 1.5.4 a second code fix writes a pool too, straight into your own file, for the cases this
generator cannot serve: a type you do not own, or a pool you want to edit by hand. It is a one-shot
starting point rather than a pool kept in step with the constructors - see
[docs/rules/OPL001.md](rules/OPL001.md#generating-the-pool-from-the-fix).

## Quick start

```csharp
using ObjectPoolLinter;

[ObjectPool]
public class Enemy
{
    public int Hp;

    public Enemy(int hp)
    {
        Hp = hp;
    }
}
```

That is all. `EnemyPool` now exists in `Enemy`'s namespace:

```csharp
Enemy enemy = EnemyPool.Get(50);   // reuses a pooled instance, or constructs one
// ...
EnemyPool.Return(enemy);           // hands it back
```

The attribute itself is generated too, so nothing needs to be referenced or installed beyond the
analyzer package. It is `internal` to the assembly that uses it, which means each Unity assembly
definition gets its own copy and no two collide.

## What is generated

For the `Enemy` above:

```csharp
public static partial class EnemyPool
{
    private static readonly Stack<Enemy> s_free = new Stack<Enemy>();

    public static int CountInactive { get; }         // how many are waiting

    public static Enemy Get(int hp);                 // one overload per constructor
    static partial void Reinitialize(Enemy instance, int hp);

    public static void Return(Enemy instance);
    static partial void OnReturn(Enemy instance);

    public static void Clear();                      // drops everything waiting
}
```

Details worth knowing:

- **One `Get` per constructor.** `public` and `internal` constructors each get an overload that
  forwards its arguments unchanged, including `params`, `ref`, `in` and default values. `private` and
  `protected` constructors are skipped, and so is any constructor with an `out` parameter.
- **The pool goes in the type's namespace**, never nested inside an outer type, so a pool for
  `Game.Spawner.Slot` is `Game.SlotPool`.
- **Generic types work.** `[ObjectPool] class Box<T> where T : class, new()` generates `BoxPool<T>`
  with the same constraints, and `new Box<Payload>(v)` becomes `BoxPool<Payload>.Get(v)`. Each closed
  type gets its own pool, because a static field in a generic class is per-construction.
- **The generated code stays inside C# 7.3** and fully qualifies every type it names, so it compiles
  on every Unity version the analyzer supports and no `using` in your file can change its meaning.
- **It is not thread-safe.** That is enough for the main-thread Unity messages these rules watch; do
  not call it from a job or a worker thread.

## Resetting recycled instances

`Get` hands back an instance that was used before. Putting it into the state the constructor would
have produced is *your* job, and the generator leaves two hooks for it. Declare your own partial and
implement them:

```csharp
static partial class EnemyPool
{
    static partial void Reinitialize(Enemy instance, int hp)
    {
        instance.Hp = hp;
        instance.Target = null;
    }

    static partial void OnReturn(Enemy instance)
    {
        instance.Target = null;   // drop references so the pool does not keep them alive
    }
}
```

`Reinitialize` has one overload per `Get` overload, taking the instance followed by that
constructor's parameters. `OnReturn` runs inside `Return`, before the instance goes back on the
stack.

Both are partial methods: **left unimplemented, the call is erased and nothing resets the instance.**
It keeps whatever state it had when it was returned. This is the one way a generated pool introduces
a bug the allocation never had, so implement `Reinitialize` for any type whose constructor does more
than store its arguments.

Your partial declaration has to be `static partial class` with the same name, in the same namespace
and assembly. It may carry an accessibility modifier; the generated half adopts whichever one you
write.

## Options

```csharp
[ObjectPool(PoolName = "Bullets", InitialCapacity = 64)]
public class Bullet { /* ... */ }
```

| Property | Default | Effect |
|---|---|---|
| `PoolName` | `{TypeName}Pool` | Name of the generated class. **OPL001's code fix only ever looks for `{TypeName}Pool`**, so renaming the pool opts out of that fix. |
| `InitialCapacity` | `0` | Capacity the backing `Stack<T>` starts with. It does not pre-create instances. |

## Returning is still yours to do

Nothing hands an instance back on its own, and `Return` does not check whether an instance is already
in the pool: return each one exactly once, from wherever its lifetime ends.

```csharp
void Update()
{
    var bullet = BulletPool.Get(speed);
    // ...
    BulletPool.Return(bullet);
}
```

A pool nothing is returned to is a leak with extra steps; an instance returned twice is handed to two
callers at once.

## What it will not do

- **`UnityEngine.Object` subclasses** - a `MonoBehaviour`, `ScriptableObject` or prefab is created
  with `Instantiate`, not `new`. Marking one is reported as [OPL005](rules/OPL005.md).
- **Abstract or static classes**, and classes with no reachable constructor: also OPL005.
- **Structs and records** - `[ObjectPool]` targets classes only, so the compiler rejects the rest.
- **Anything about lifetime.** No `IDisposable` handling, no leak detection, no maximum size.

## Where the generated code lives

Nowhere on disk, unless you ask. Roslyn holds generated sources in memory; "Go to definition" on
`EnemyPool.Get` opens them in Visual Studio and Rider. To read the files directly:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

They then land in
`obj/<Configuration>/<TFM>/generated/ObjectPoolLinter/ObjectPoolLinter.ObjectPoolGenerator/`.
Do not check them in and do not edit them: every build rewrites them.

In Unity there is no such switch. The generator runs inside Unity's own compile, and both the pool and
the attribute are visible to your code and to the IDE, but no files are written into `Assets/`.

## Requirements

Unity 2021.3 or newer (Unity's minimum for Roslyn source generators), or any .NET SDK that loads the
analyzer. The generator ships in `ObjectPoolLinter.dll` alongside the analyzers, so installing the
package - NuGet, `.unitypackage`, or [UPM](unity-package-manager.md) - is all that is needed.
