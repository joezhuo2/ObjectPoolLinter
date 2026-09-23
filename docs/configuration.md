# Configuring ObjectPoolLinter

ObjectPoolLinter reads its settings from `.editorconfig` (or `.globalconfig`), the same files that set
C# style and analyzer severities. There is no separate config file and nothing to register: put the
lines in the right file and the next build, or the IDE's next analysis pass, picks them up.

- [Quick start](#quick-start)
- [Where the file goes](#where-the-file-goes)
- [Option reference](#option-reference)
- [Hot methods](#hot-methods-additional_hot_methods)
- [Excluded types](#excluded-types-excluded_types-and-excluded_types_regex)
- [Rule severity](#rule-severity)
- [Per-kind OPL002 severity](#per-kind-opl002-severity)
- [Checking your config (OPL004)](#checking-your-config-opl004)
- [How files combine](#how-files-combine)
- [Unity notes](#unity-notes)

## Quick start

1. Create a file named `.editorconfig` in your project root. In Unity that is the folder that holds
   `Assets/`, `Packages/` and `ProjectSettings/`.
2. Paste this in and edit it to suit your project. Every line is optional:

   ```ini
   root = true

   [*.cs]
   # Custom update loops the linter should treat like Update().
   object_pool_linter.additional_hot_methods = Tick(float), Simulate, EnemyBrain.Think()

   # Types whose methods are never reported.
   object_pool_linter.excluded_types = LoadingScreen, Game.UI.CreditsRoll
   object_pool_linter.excluded_types_regex = ^Game\.(Debug|Editor)\.

   # Rule severities: none | silent | suggestion | warning | error
   dotnet_diagnostic.OPL001.severity = warning
   dotnet_diagnostic.OPL003.severity = warning

   # OPL002 per kind of allocation (leave dotnet_diagnostic.OPL002.severity unset to use these).
   object_pool_linter.linq_severity = warning
   object_pool_linter.boxing_severity = warning
   object_pool_linter.delegate_severity = warning
   object_pool_linter.string_severity = suggestion
   object_pool_linter.params_severity = suggestion
   object_pool_linter.iterator_severity = warning
   object_pool_linter.async_severity = suggestion
   object_pool_linter.enumerator_severity = warning
   ```

3. Rebuild (`dotnet build`) or, in Unity, let the scripts recompile. A misspelled or unusable option
   shows up as an [OPL004](rules/OPL004.md) warning naming the line to fix.

`root = true` stops the search for further `.editorconfig` files in folders above this one. Leave it
out if you keep a shared `.editorconfig` higher up.

## Where the file goes

Options are resolved per source file. For each `.cs` file the compiler reads every `.editorconfig`
from that file's folder up to the nearest one with `root = true`, and applies the `[*.cs]` sections
that match. So:

| You want | Put the settings in |
| --- | --- |
| The same settings for the whole project | `.editorconfig` at the project root |
| Different settings for one folder (`Assets/Scripts/Debug/`) | A second `.editorconfig` in that folder |
| One file of settings shared by several projects | A `.globalconfig` |

A `.globalconfig` applies to every file in the compilation regardless of folder. It starts with
`is_global = true`; put the ObjectPoolLinter options under a `[*.cs]` section. The .NET SDK picks up a
file named `.globalconfig` in the project folder; other names are added with
`<GlobalAnalyzerConfigFiles Include="path" />` in the project file.

## Option reference

| Option | Value | Applies to | Added in |
| --- | --- | --- | --- |
| `object_pool_linter.additional_hot_methods` | Comma-separated method entries | OPL001, OPL002, OPL003, OPL008 | 1.2.0; parameter lists in 1.5.2 |
| `object_pool_linter.excluded_types` | Comma-separated type names | OPL001, OPL002, OPL003, OPL008 | 1.2.0 |
| `object_pool_linter.excluded_types_regex` | One .NET regular expression | OPL001, OPL002, OPL003, OPL008 | 1.5.2 |
| `object_pool_linter.string_severity` | Severity | OPL002 | 1.5.2 |
| `object_pool_linter.delegate_severity` | Severity | OPL002 | 1.5.2 |
| `object_pool_linter.params_severity` | Severity | OPL002 | 1.5.2 |
| `object_pool_linter.linq_severity` | Severity | OPL002 | 1.5.2 |
| `object_pool_linter.boxing_severity` | Severity | OPL002 | 1.5.2 |
| `object_pool_linter.iterator_severity` | Severity | OPL002 | 1.5.5 |
| `object_pool_linter.async_severity` | Severity | OPL002 | 1.5.5 |
| `object_pool_linter.enumerator_severity` | Severity | OPL002 | 1.5.6 |
| `object_pool_linter.suppressions` | Comma-separated pattern names, `all` or `none` | OPL001, OPL002, OPL003, OPL008 | 1.5.4 |
| `dotnet_diagnostic.OPL00N.severity` | Severity | The named rule | Standard Roslyn |

A severity is one of `none`, `silent`, `suggestion`, `warning`, `error`, the same words
`dotnet_diagnostic.*.severity` takes. The OPL002 kind options also accept `default`, meaning the
rule's own severity.

Option names are not case-sensitive. Method and type names in the values are.

## Hot methods (`additional_hot_methods`)

Out of the box the rules look inside the 18 per-frame Unity messages (`Update`, `FixedUpdate`,
`LateUpdate`, `OnGUI`, the `Stay` physics callbacks and the rest) declared on a `MonoBehaviour`. Add
your own per-frame code here: a `Tick()` driven by a manager, Zenject's `ITickable.Tick`, a
fixed-step `Simulate`, or a Unity message the built-in list leaves out, such as `OnPreCull`.

Each entry is one of:

| Entry | Matches |
| --- | --- |
| `Tick` | Every method named `Tick`, on any type, with any parameters, static or instance |
| `EnemyBrain.Think` | `Think` declared on a type named `EnemyBrain`, any parameters |
| `Game.AI.EnemyBrain.Think` | `Think` on `EnemyBrain` in namespace `Game.AI` only |
| `Tick(float)` | `Tick` methods whose parameter list is exactly one `float` |
| `EnemyBrain.Think()` | `EnemyBrain.Think` with no parameters |
| `Step(ref Vector3, float)` | `Step(ref Vector3, float)`; `ref`, `out` and `in` must match |
| `Apply(Dictionary<int, string>)` | Generic parameter types are written as in C# |

Parameter types can be written as the C# keyword (`float`), the simple type name (`Vector3`,
`Single`), or the namespace-qualified name (`UnityEngine.Vector3`, `System.Single`). Spaces are
ignored, a leading `global::` is dropped, and `params` in front of a parameter is accepted and ignored.
Commas inside the parentheses or angle brackets do not split the list.

Additional hot methods are not limited to MonoBehaviours, because custom loops usually live on plain
classes. The rules still run only in a compilation that references `UnityEngine.MonoBehaviour`.

## Excluded types (`excluded_types` and `excluded_types_regex`)

Methods declared on an excluded type are never reported, whether they are Unity messages or
additional hot methods. Use this for a MonoBehaviour whose `Update` rarely does real work (a loading
screen, a debug overlay) instead of scattering pragmas through it.

`excluded_types` takes a comma-separated list:

- A simple name (`LoadingScreen`) matches that type in any namespace.
- A qualified name (`Game.UI.LoadingScreen`) matches only that one. Nested types use dots
  (`Game.Outer.Inner`), generic types are written without their type parameters (`Pool`, not
  `Pool<T>`), and a leading `global::` is ignored.

`excluded_types_regex` takes one .NET regular expression, matched against each type's
namespace-qualified name in the same form (`Game.Debug.Overlay`, `Game.Debug.Overlay.Panel`):

```ini
[*.cs]
# Everything in Game.Debug and its sub-namespaces.
object_pool_linter.excluded_types_regex = ^Game\.Debug\.
```

```ini
[*.cs]
# Several patterns: join them with |.
object_pool_linter.excluded_types_regex = ^Game\.(Debug|Editor)\.|Gizmo$
```

- The match is unanchored, so add `^` and `$` where you mean them. It is case-sensitive; start the
  pattern with `(?i)` to ignore case.
- Commas are part of the pattern (`{1,3}` works), which is why this option holds a single pattern.
- The pattern is compiled once per project, and each match has a 250 ms time limit, so a runaway
  pattern cannot hang the build. A type whose match times out is not excluded.
- A pattern that does not compile excludes nothing and is reported by OPL004.

Both options can be used together; a type listed in either is excluded. Exclusions apply to the type
the method is declared on. Derived types are still analyzed; list them too, or use a regex that
matches them.

## Rule severity

Each rule's severity is set the standard Roslyn way:

```ini
[*.cs]
dotnet_diagnostic.OPL001.severity = warning      # default
dotnet_diagnostic.OPL002.severity = warning      # default is suggestion (Info)
dotnet_diagnostic.OPL003.severity = error
dotnet_diagnostic.OPL004.severity = warning      # default
dotnet_diagnostic.OPL005.severity = warning      # default
dotnet_diagnostic.OPL006.severity = warning      # default
dotnet_diagnostic.OPL007.severity = warning      # default
dotnet_diagnostic.OPL008.severity = warning      # default is suggestion (Info)
```

[OPL006](rules/OPL006.md) and [OPL007](rules/OPL007.md) do not depend on hot paths, so the hot-method
and excluded-type options do not affect them.

`none` turns a rule off. For a single site, use `#pragma warning disable OPL001` or a
`[SuppressMessage]` attribute; see *When to suppress* in each rule's page.

[OPL005](rules/OPL005.md) comes from the `[ObjectPool]` source generator rather than an analyzer,
but its severity is set the same way. The generator itself has no `object_pool_linter.*` options:
everything it takes is on the attribute. See
[Generating pools with `[ObjectPool]`](source-generator.md).

## Per-kind OPL002 severity

[OPL002](rules/OPL002.md) covers eight kinds of hidden allocation, and they are rarely equally
important. Each kind has its own severity option:

| Option | Kind | Messages it covers |
| --- | --- | --- |
| `object_pool_linter.string_severity` | String building | `string concatenation`, `string interpolation`, `string.Concat()`, `string.Format()`, `StringBuilder.ToString()` |
| `object_pool_linter.delegate_severity` | Delegates | `lambda capturing ...`, `delegate for ...()` |
| `object_pool_linter.params_severity` | `params` arrays | `params object[] for ...()` |
| `object_pool_linter.linq_severity` | LINQ, including LINQ-style extension methods on `IEnumerable<T>` | `LINQ ...` |
| `object_pool_linter.boxing_severity` | Boxing | `boxing ...` |
| `object_pool_linter.iterator_severity` | Iterator state machines | `iterator state machine for ...()` |
| `object_pool_linter.async_severity` | Async state machines | `async state machine for ...()` |
| `object_pool_linter.enumerator_severity` | `foreach` enumerators obtained through an interface | `enumerator for foreach over ...` |

```ini
[*.cs]
# Warn on LINQ and boxing, keep strings as suggestions, never report params arrays.
object_pool_linter.linq_severity = warning
object_pool_linter.boxing_severity = warning
object_pool_linter.string_severity = suggestion
object_pool_linter.params_severity = none
```

How it combines with `dotnet_diagnostic.OPL002.severity`:

- A kind with no option of its own uses the rule's severity: Info, or whatever
  `dotnet_diagnostic.OPL002.severity` sets.
- **`dotnet_diagnostic.OPL002.severity` wins when it is set.** The compiler applies it to every OPL002
  diagnostic after the analyzer has picked a per-kind severity. To use per-kind severities, leave
  `dotnet_diagnostic.OPL002.severity` out, and `dotnet_analyzer_diagnostic.category-Performance.severity`
  too, since it has the same effect.
- The exception is `none`: a kind set to `none` is never reported, whatever the rule-wide severity.
- `dotnet_diagnostic.OPL002.severity = none` turns off every kind.

Each OPL002 diagnostic carries its kind in the `AllocationKind` property (`string`, `delegate`,
`params`, `linq` or `boxing`), for tools that read diagnostic properties.

## Automatic suppressions (`suppressions`)

OPL001, OPL002, OPL003 and OPL008 are suppressed automatically where the allocation is known not to
run every frame: behind a `Time.frameCount == 0` guard, inside `#if UNITY_EDITOR`, behind a static
`bool` latch the guarded branch sets, or assigned straight into a field. Each pattern can be switched
off by listing only the ones you want:

```ini
[*.cs]
# Every pattern. This is the default, so the line only documents it.
object_pool_linter.suppressions = all

# Nothing is suppressed automatically: the rules report every allocation they find.
object_pool_linter.suppressions = none

# Only these two; a latch or a field assignment is reported as usual.
object_pool_linter.suppressions = first_frame, editor_only
```

| Name | Suppresses an allocation that is |
| --- | --- |
| `first_frame` | In the taken branch of `if (Time.frameCount == 0)` |
| `editor_only` | Inside `#if UNITY_EDITOR` |
| `static_latch` | In the taken branch of an `if` on a static `bool` field the branch assigns |
| `cached_field` | Assigned to a field |

Names are case-insensitive, and an unknown one is reported as [OPL004](rules/OPL004.md) and leaves
every pattern on. What each pattern matches exactly, and what it deliberately does not, is in
[Automatic suppressions](suppressions.md).

## Checking your config (OPL004)

[OPL004](rules/OPL004.md) reports, as a build warning, any `object_pool_linter.*` option that has no
effect:

```
warning OPL004: ObjectPoolLinter option ignored: 'object_pool_linter.exlude_types' is not a recognized option. Did you mean 'object_pool_linter.excluded_types'?
warning OPL004: ObjectPoolLinter option ignored: 'loud' in 'object_pool_linter.linq_severity' is not a severity. Use none, silent, suggestion, warning, error or default.
warning OPL004: ObjectPoolLinter option ignored: 'Tick(float' in 'object_pool_linter.additional_hot_methods' has a parameter list that does not end with ')'.
```

It is reported once per project, at the end of the compilation, with no source location: it appears
in build output and in the IDE's error list when full-solution analysis is on, but not as a squiggle
in a file.

Finding misspelled option *names* needs a compiler that can list the options it holds. The analyzer
is built against Roslyn 3.8, which cannot, and asks for the list at run time instead; it works with
the .NET 10 SDK and other recent compilers. On a compiler without that ability, invalid *values* are
still reported, but a misspelled *name* is not.

## How files combine

- **Nearer wins.** An `.editorconfig` in a subfolder overrides the same option from a folder above.
- **Later wins.** Inside one file, a later matching section overrides an earlier one.
- **Values replace, they do not merge.** A subfolder's `additional_hot_methods = Simulate` replaces the
  root's `Tick, OnPreCull`; it does not add to it. Repeat the root's entries if you want both.
- **`.editorconfig` beats `.globalconfig`.** Global configs have the lowest precedence. If two global
  configs set the same option differently, the compiler warns and ignores the option unless one has a
  higher `global_level`.

Example: the root config adds `Tick`; `Assets/Scripts/Debug/.editorconfig` holds

```ini
[*.cs]
object_pool_linter.additional_hot_methods = Tick, DrawDebug
object_pool_linter.linq_severity = none
```

Files under `Debug/` treat `Tick` and `DrawDebug` as hot and ignore LINQ; every other file keeps the
root's settings.

## Unity notes

- Put the `.editorconfig` next to `Assets/`.
- Unity's own editor compile has not been verified to pass `object_pool_linter.*` options to
  analyzers. `dotnet build`, Visual Studio, Rider and VS Code do. If the IDE reports something the
  Unity Console does not, or the other way round, that is the likely cause.
- The Unity Console shows warnings and errors only. OPL002 and OPL008 are Info by default, so raise
  them (or some of OPL002's kinds) to `warning` to see them there.
