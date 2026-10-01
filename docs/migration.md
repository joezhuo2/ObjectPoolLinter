# Migration guide

ObjectPoolLinter follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). This page says
what a major version is allowed to change, and for each major version, what to change in your project
when you upgrade to it. Minor and patch releases need no migration; what they add is listed below so
that a build which treats warnings as errors is not caught out.

- [What counts as a breaking change](#what-counts-as-a-breaking-change)
- [Upgrading within 1.x](#upgrading-within-1x)
- [1.0.0: from a 0.x build](#100-from-a-0x-build)
- [Writing the guide for the next major version](#writing-the-guide-for-the-next-major-version)

## What counts as a breaking change

Only a major version may:

- **remove or renumber a diagnostic ID.** IDs are never reused: a retired rule's ID stays retired, so a
  `#pragma`, `[SuppressMessage]` or `dotnet_diagnostic.OPLnnn.severity` line written for it can never
  silently start applying to a different rule;
- **raise a rule's default severity**, for example from Info to Warning, or enable a rule that was
  disabled by default;
- **rename or remove an `object_pool_linter.*` option, or change what an existing value means**
  (a different default, a different matching rule for names, a different set of accepted values);
- **change the format arguments of a diagnostic message** in a way that tools parsing the message
  would notice, or the `AllocationKind` property values of OPL002;
- **raise the Roslyn version the analyzer is built against**, which raises the minimum Unity, Visual
  Studio and .NET SDK versions (see [Requirements](../README.md#requirements));
- **rename a shipped assembly or the NuGet package**, or split or merge the DLLs.

A minor version may add rules, add options, add detections to an existing rule, add or extend code
fixes and automatic suppressions, and lower a default severity. Adding a detection means a rule can
report code it did not report before, and a new rule can arrive as a Warning, so a build with
`-warnaserror` or `<TreatWarningsAsErrors>` can start failing after a minor upgrade. The table below
lists every such change; turn a new rule off or down with `dotnet_diagnostic.<rule>.severity` if you
are not ready for it.

A patch version fixes false positives, false negatives and code fix output, and changes documentation
and build tooling.

## Upgrading within 1.x

No migration is needed between 1.x versions: no diagnostic ID has been removed or renumbered, no
default severity raised, and no option renamed or given a new meaning since 1.0.0. These releases add
rules, detections or options that can change what a build reports:

| Version | Change | Default severity | Opt out with |
| --- | --- | --- | --- |
| 1.2.0 | `additional_hot_methods` and `excluded_types` options | — | — |
| 1.3.0 | New rule [OPL002](rules/OPL002.md), hidden allocations | Info | `dotnet_diagnostic.OPL002.severity = none` |
| 1.3.0 | New rule [OPL003](rules/OPL003.md), allocating Unity APIs | **Warning** | `dotnet_diagnostic.OPL003.severity = none` |
| 1.5.2 | New rule [OPL004](rules/OPL004.md), invalid `object_pool_linter.*` options | **Warning** | `dotnet_diagnostic.OPL004.severity = none` |
| 1.5.2 | Parameter lists in `additional_hot_methods`; `excluded_types_regex`; per-kind OPL002 severities | — | — |
| 1.5.4 | New generator diagnostic [OPL005](rules/OPL005.md), unusable `[ObjectPool]` | **Warning** | `dotnet_diagnostic.OPL005.severity = none` |
| 1.5.4 | Automatic suppressions (OPLS001 to OPLS004); fewer diagnostics, not more | — | `object_pool_linter.suppressions = none` |
| 1.5.5 | OPL002 reports LINQ-style extension methods and iterator and `async` state machines | Info | `object_pool_linter.linq_severity`, `iterator_severity`, `async_severity` |
| 1.5.6 | New rule [OPL006](rules/OPL006.md), managed fields in job structs | **Warning** | `dotnet_diagnostic.OPL006.severity = none` |
| 1.5.6 | OPL002 reports `foreach` enumerators obtained through an interface; Burst-compiled code is no longer reported | Info | `object_pool_linter.enumerator_severity` |
| 1.6.0 | New rule [OPL007](rules/OPL007.md), undisposed native containers | **Warning** | `dotnet_diagnostic.OPL007.severity = none` |
| 1.6.0 | New rule [OPL008](rules/OPL008.md), asset loads in hot paths | Info | `dotnet_diagnostic.OPL008.severity = none` |
| 1.6.1 | New rule [OPL009](rules/OPL009.md), component lookups in hot paths | Info | `dotnet_diagnostic.OPL009.severity = none` |
| 1.6.1 | OPL003 reports more members that build a new string or object (`Application.dataPath`, `Scene.name`, `NavMeshAgent.path`, `JsonUtility`, ...) | **Warning** | `#pragma` or `[SuppressMessage]` per site, or the rule's severity |

`AnalyzerReleases.Shipped.md` in `src/ObjectPoolLinter/` is the authoritative record of when each rule
shipped and at which severity. [CHANGELOG.md](../CHANGELOG.md) lists every detection added to an
existing rule.

## 1.0.0: from a 0.x build

The 0.x versions were never published to nuget.org or as Unity artifacts, so this applies only to a
build made from the repository before 1.0.0.

| What changed | What to do |
| --- | --- |
| **The analyzer and the code fixes are two assemblies**, `ObjectPoolLinter.dll` and `ObjectPoolLinter.CodeFixes.dll`. | A manual drop-in in Unity needs both DLLs, each labelled `RoslynAnalyzer` with every platform cleared ([Installation](../README.md#unity)). A project reference needs both projects as `OutputItemType="Analyzer"`, as `samples/SampleUnityCode/SampleUnityCode.csproj` shows. |
| **Built against Roslyn 3.8** instead of 4.8. | Nothing in your project; older hosts now load the analyzer. See [Requirements](../README.md#requirements). |
| **The OPL001 message was reworded** and puts the allocation first: `'new List<int>' allocates inside the frequently-called method 'Update'.` The allocation is named from the resolved type, so `new()` and fully qualified names read the same, and arrays read as `new int[]`. | Update anything that matches on the message text, such as a CI script grepping build output. Match on the ID, `OPL001`, where you can. |
| **The message format arguments are in reading order**: `{0}` is the allocation, `{1}` the method. | Update tools that read `Diagnostic.GetMessage()` arguments by position. |
| **The "Replace with object pool Get()" fix is not offered for an allocation with an initializer** (`new Enemy { Hp = 5 }`). | Use the TODO-comment fix there and move the initializer by hand. |

Suppressions keep working unchanged: the ID `OPL001` and the `Performance` category are the same.

## Writing the guide for the next major version

Each major version gets its own section on this page, written in the same release that introduces
it, before the tag is pushed. A section lists, for every breaking change in the
[categories above](#what-counts-as-a-breaking-change):

1. what changed, with the old and new form side by side (ID, severity, option name or value, message);
2. what to change in a project: the `.editorconfig` lines to rename, the `#pragma` and
   `[SuppressMessage]` IDs to update, the hosts to upgrade;
3. how to keep the old behavior, where an option allows it.

The changelog entry for that version links to its section here.
