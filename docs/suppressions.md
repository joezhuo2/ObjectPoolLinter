# Automatic suppressions

`ObjectPoolSuppressionAnalyzer` is a Roslyn `DiagnosticSuppressor` that ships with the analyzer. It
reads every OPL001, OPL002, OPL003, OPL008 and OPL009 diagnostic the rules report and suppresses the
ones sitting in a shape that already answers the rule: code that runs once rather than every frame, code
that is not in a player build at all, or an object that is being cached rather than thrown away.

A suppressed diagnostic is not deleted. It stays in the compilation carrying its suppression, so the
IDE greys it out with the justification, `-warnaserror` ignores it, and the build's warning count
drops. Nothing else about the rules changes: the same code with the guard removed is reported again.

Every pattern can be switched off individually from `.editorconfig`, see
[Turning patterns off](#turning-patterns-off).

## The four patterns

| Suppression | Fires when | Justification shown |
| --- | --- | --- |
| `OPLS001` | The allocation is in the taken branch of an `if` testing `Time.frameCount == 0` | Runs on the first frame only |
| `OPLS002` | The allocation is inside `#if UNITY_EDITOR` | Not in a player build |
| `OPLS003` | The allocation is in the taken branch of an `if` testing a static `bool` field that the same branch assigns | Runs once |
| `OPLS004` | The allocated object is assigned straight into a field | That is the caching the rule asks for |

Each suppression id covers all five rules: `OPLS004` suppresses the OPL001 on
`_buffer = new List<int>();`, the OPL002 on `_label = first + second;` and, since 1.6.0, the OPL008 on
`_prefab = Resources.Load<GameObject>("Enemy");` alike. A lazy load,
`if (_prefab == null) _prefab = Resources.Load<GameObject>("Enemy");`, is exactly the caching
[OPL008](rules/OPL008.md) asks for. Since 1.6.1 the same goes for [OPL009](rules/OPL009.md):
`_body = GetComponent<Rigidbody>();`, lazily or not, is suppressed.

### OPLS001: the first-frame guard

```csharp
void Update()
{
    if (Time.frameCount == 0)
    {
        _warmup = new List<int>(64); // no OPL001
    }
}
```

`Time` is matched by symbol, so it has to be `UnityEngine.Time`, and the comparison has to be
`== 0` with the literal `0` on either side. The guard may be one operand of an `&&` chain
(`if (enabled && Time.frameCount == 0)`), and it may sit any number of statements up, as long as the
allocation is inside the branch the condition takes. An `else` branch is not covered: it is what runs
on every other frame. `Time.frameCount == 60`, `< 2` and the rest are not covered either — only the
first frame is provably once.

### OPLS002: editor-only code

```csharp
void Update()
{
#if UNITY_EDITOR
    var report = new StringBuilder(); // no OPL001
#endif
}
```

The `#if` condition has to require the symbol positively: the bare identifier, or one operand of an
`&&`. Any symbol whose name starts with `UNITY_EDITOR` counts, which covers `UNITY_EDITOR_WIN`,
`UNITY_EDITOR_OSX` and `UNITY_EDITOR_LINUX`. `#if !UNITY_EDITOR`, the `#else` of an
`#if UNITY_EDITOR`, and `#if UNITY_EDITOR || SOMETHING_ELSE` are not covered, because the branch is
reachable in a player build.

Code inside a region the compiler did not take is not analyzed in the first place, so this
suppression only matters where `UNITY_EDITOR` is actually defined — that is, the editor compile
itself.

### OPLS003: the static boolean latch

```csharp
private static bool s_spawned;

void Update()
{
    if (!s_spawned)
    {
        _pool = new ObjectPool();  // no OPL001
        s_spawned = true;
    }
}
```

The condition has to read a **static** `bool` field, and the same branch has to assign that field.
Both halves are required: without the assignment, the branch can run on every frame, and the
diagnostic stands. `!field`, `field`, `field == false`, `field != true` and any of them inside an
`&&` chain are all recognized. An instance field is not: each instance latches separately, so a
scene full of them still allocates once per object per load.

### OPLS004: the result assigned to a field

```csharp
private List<int> _buffer;

void Update()
{
    _buffer = new List<int>();        // no OPL001
    _other ??= new List<int>();       // no OPL001
    _third = _third ?? new List<int>(); // no OPL001
}
```

The allocation has to be the right-hand side of an assignment whose left-hand side is a field, with
only parentheses, casts, a conditional or a `??` in between. A local, a property, an array element or
an argument does not count.

This is the loosest of the four: assigning a **new** object to a field on every frame still
allocates on every frame, and the suppressor cannot tell that from a genuine one-time cache. It is
suppressed anyway because the shape is what the rules' own advice produces — hoist the object into a
field — and because the field almost always gets its value under one of the other three guards. If
you would rather see those diagnostics, turn this pattern off:

```ini
[*.cs]
object_pool_linter.suppressions = first_frame, editor_only, static_latch
```

## Turning patterns off

```ini
[*.cs]
# Every pattern (the default).
object_pool_linter.suppressions = all

# No automatic suppression at all: the rules report everything they find.
object_pool_linter.suppressions = none

# Only the named patterns; the others report as usual.
object_pool_linter.suppressions = first_frame, editor_only
```

The names are `first_frame`, `editor_only`, `static_latch` and `cached_field`, plus `all` and `none`.
They are matched case-insensitively. A name the linter does not know is reported as
[OPL004](rules/OPL004.md) and leaves every pattern on, rather than silently narrowing the set.

The option is read per file, like every other `object_pool_linter.*` option, so an `.editorconfig` in
a subfolder can widen or narrow the set for the scripts underneath it. See
[Configuring ObjectPoolLinter](configuration.md).

## Suppressing by hand

The suppressor covers four mechanical shapes. Everything else — an allocation that escapes the frame
on purpose, a one-shot in `OnDrawGizmos`, a case where pooling would cost more than the garbage —
stays a judgement call, and the ordinary tools apply: `#pragma warning disable OPL001`, a
`[SuppressMessage]` attribute on the member, or `dotnet_diagnostic.OPL001.severity`. Each rule page
spells this out under **When to suppress**:
[OPL001](rules/OPL001.md#when-to-suppress), [OPL002](rules/OPL002.md#when-to-suppress),
[OPL003](rules/OPL003.md#when-to-suppress).

A hand-written suppression and an automatic one do not conflict: a diagnostic that is already
suppressed by a pragma stays suppressed.

## What is deliberately not suppressed

- **A guard the suppressor cannot see through.** `if (ShouldWarmUp())`, a latch behind a property, a
  `frameCount` check in a helper method: the suppressor is syntactic and local, it does no call-graph
  analysis.
- **`Start`, `Awake` and friends.** Nothing is needed there - the rules never look at them.
- **An allocation on a cold branch inside a hot method** (`if (hp <= 0)`), which is rare per frame
  but not provably once.
- **A `[BurstCompile]` method**, where managed allocations are structurally impossible. There is
  nothing to suppress: since 1.5.6 the rules do not report inside Burst-compiled code in the first
  place (see [Burst-compiled code](rules/OPL001.md#burst-compiled-code)).
- **OPL007, a native container not disposed.** A leak is a leak whether it happens once or every
  frame, and assigning a container to a field is exactly what OPL007 checks the owning type for.
