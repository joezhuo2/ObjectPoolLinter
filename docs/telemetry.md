# Telemetry

ObjectPoolLinter can tell you which rules fire most on your code and which code fixes you actually
apply. It is **off unless you turn it on**, and it is **local only**: nothing is sent anywhere, by the
analyzer, the code fixes or anything else in the package. What it collects stays on your machine, in
your build output and in one file you can read, share or delete.

## Turning it on

In `.editorconfig` (or `.globalconfig`):

```ini
[*.cs]
object_pool_linter.telemetry = true
```

`true` and `false` are the values, in any case. Anything else is reported as
[OPL004](rules/OPL004.md) and leaves telemetry off. It is on for a whole project when the global
options or any one file's options set it, so a single line in the root `.editorconfig` is enough.

## What it collects

### Rule counts: OPL010

At the end of each compile, [OPL010](rules/OPL010.md) reports how many times each rule fired:

```
info OPL010: ObjectPoolLinter telemetry: OPL002: 375, OPL001: 125, OPL003: 125, OPL009: 125 (501 source files)
```

It is an ordinary Info diagnostic with no source location. It goes where diagnostics go: the build
output once you raise its severity, a SARIF log, the IDE's error list. The analyzer cannot write files
or open connections (Roslyn forbids both for analyzers), so this is the only place the counts appear.
[OPL010](rules/OPL010.md) covers what is counted and how to see it.

### Applied code fixes: the local file

Each time you apply an ObjectPoolLinter code fix in the IDE, with telemetry on for that file, the code
fix adds one to a counter in:

| OS | File |
| --- | --- |
| Windows | `%LOCALAPPDATA%\ObjectPoolLinter\telemetry.json` |
| macOS | `~/Library/Application Support/ObjectPoolLinter/telemetry.json` when the IDE runs on .NET 8 or later, `~/.local/share/ObjectPoolLinter/telemetry.json` on earlier .NET |
| Linux | `$XDG_DATA_HOME/ObjectPoolLinter/telemetry.json`, by default `~/.local/share/ObjectPoolLinter/telemetry.json` |

```json
{
  "schema": 1,
  "codeFixes": {
    "ObjectPoolLinterCacheLambda": 3,
    "ObjectPoolLinterUseCompareTag": 12
  },
  "fixAll": {
    "ObjectPoolLinterUseCompareTag": 1
  }
}
```

- `codeFixes` counts each fix applied on its own, `fixAll` each **Fix All** run (one per run, however
  many occurrences it fixed).
- The keys are the fixes' equivalence keys, which name the fix: `ObjectPoolLinterReplaceWithPoolGet`,
  `ObjectPoolLinterGeneratePool`, `ObjectPoolLinterRentFromArrayPool` and
  `ObjectPoolLinterAddPoolingComment` for OPL001; `ObjectPoolLinterCacheLambda`,
  `ObjectPoolLinterCacheMethodGroup`, `ObjectPoolLinterUseStringBuilder` and
  `ObjectPoolLinterLinqToLoop` for OPL002; `ObjectPoolLinterUseCompareTag`,
  `ObjectPoolLinterUseBufferOverload` and `ObjectPoolLinterUseGetTouch` for OPL003.
- Previewing a fix (hovering over it in the light bulb menu) counts nothing; only applying it does.
- The count is a step of the code fix after its edit. Visual Studio runs it. A host that applies only
  a fix's edit, as language-server-based editors may, applies the fix and counts nothing.
- One file is shared by every project and IDE on the machine. When another IDE process holds it, the
  fix waits a moment, then gives up on the count; a fix is never held up or failed by telemetry. A
  file that is not in this shape is started over.

## What it never collects

No file names, paths, project or solution names, type or member names, source code, user or machine
names, IP addresses, or timestamps. The rule counts are rule IDs and numbers; the file holds fix keys
and numbers. There is no identifier that ties the data to a person or a machine.

## Sharing it

The point of the data is to show which rules and fixes earn their keep. If you want to help, copy
the OPL010 line from a build and the contents of `telemetry.json` into a
[GitHub issue](https://github.com/joezhuo2/ObjectPoolLinter/issues/new/choose). Read them first: you
decide what leaves your machine.

## Turning it off and removing the data

Delete the `object_pool_linter.telemetry` line or set it to `false`. Nothing more is counted, and
OPL010 is no longer reported. Delete `telemetry.json` to remove what was collected; no other copy
exists.

## Privacy

Telemetry was designed to keep it outside what data protection rules such as the GDPR govern: it is
opt-in, it collects no personal data (see [What it never collects](#what-it-never-collects)), and it
transfers nothing to the maintainer or anyone else. Everything it produces stays in the places listed
above, under your control.
