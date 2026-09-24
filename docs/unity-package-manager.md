# Installing through the Unity Package Manager

ObjectPoolLinter ships a UPM tarball, `com.joezhuo.objectpoollinter-<version>.tgz`, on the
[releases page](https://github.com/joezhuo2/ObjectPoolLinter/releases). This page covers installing
it, where to keep the file, how upgrades behave, and why the asset GUIDs stay put.

Not using UPM? The same release carries a `.unitypackage`, and the NuGet package works for plain .NET
projects. Both are covered in the [README](../README.md#installation).

## What is in the package

```
package/
  package.json              name: com.joezhuo.objectpoollinter, unity: 2021.3
  README.md
  LICENSE.md
  RoslynAnalyzers/
    ObjectPoolLinter.dll            analyzers OPL001-OPL004 and the [ObjectPool] generator
    ObjectPoolLinter.CodeFixes.dll  the IDE lightbulb fixes
```

Both DLLs carry the `RoslynAnalyzer` asset label with every platform disabled, which is what tells
Unity to hand them to the C# compiler instead of building them into a player. Nothing ships into your
build output.

The package has no assembly definition file, so it applies to Unity's predefined assemblies
(`Assembly-CSharp` and friends). Running it against your own assembly definitions is covered under
"Scoping the analyzer" in the [README](../README.md).

## Install

1. Download `com.joezhuo.objectpoollinter-<version>.tgz` from the
   [releases page](https://github.com/joezhuo2/ObjectPoolLinter/releases).
2. **Put the file inside your project**, in a folder such as `Packages/tarballs/`, and commit it. The
   reason is in [Where to keep the tarball](#where-to-keep-the-tarball) below: Unity records the path
   to the file, not a copy of its contents.
3. In Unity: `Window > Package Manager`, then the `+` button, then
   **Install package from tarball...**, and pick the `.tgz`.

Unity extracts it into its package cache, adds a line to `Packages/manifest.json`, and recompiles.
OPL001 and OPL003 warnings appear in the Console within a few seconds. OPL002 is Info by default,
which the Console does not display - raise it to a warning in `.editorconfig` to see it there:

```ini
[*.cs]
dotnet_diagnostic.OPL002.severity = warning
```

### Editing the manifest directly

The Package Manager UI writes an entry you can also add by hand:

```json
{
  "dependencies": {
    "com.joezhuo.objectpoollinter": "file:../Packages/tarballs/com.joezhuo.objectpoollinter-1.5.3.tgz"
  }
}
```

The path is relative to `Packages/manifest.json` itself, which is why a tarball at
`<project>/Packages/tarballs/` is written as `file:../Packages/tarballs/...`. Hand-editing is the
quickest way to script an upgrade across several projects.

## Where to keep the tarball

Unity stores a **path**, not the package contents. If the path stops resolving, the package fails to
load and every OPL diagnostic silently disappears.

| Location | Result |
|---|---|
| Inside the project, committed (`Packages/tarballs/`) | Recommended. The manifest entry is a relative path, so every clone and every CI runner resolves it. |
| Elsewhere on your machine | The manifest gets an absolute path - `file:/Users/you/Downloads/...` - which breaks for everyone else. |
| A shared network path | Works, but ties compilation to that share being mounted. |

If you would rather not commit a binary, extract the tarball's `package/` directory into
`Packages/com.joezhuo.objectpoollinter/` instead. Unity treats any folder under `Packages/` with a
`package.json` as an *embedded* package, so no manifest entry is needed at all and the files are
plain source control.

## Upgrading and pinning

The version is the file: there is no registry to query, so UPM never upgrades this package on its own.
That makes the manifest entry a version pin by construction.

To upgrade:

1. Drop the new `.tgz` next to the old one.
2. Point the manifest line at the new filename (or remove and re-add through the Package Manager UI).
3. Delete the old tarball once everyone has pulled.

To roll back, point the line at the older file. The package cache is keyed by tarball, so switching
back and forth costs a recompile and nothing else.

Read [CHANGELOG.md](../CHANGELOG.md) before upgrading a minor version: new rules and new default
severities arrive there, and a build with `-warnaserror` can start failing on code that did not
change.

## GUID stability

Unity keys every asset by the GUID in its `.meta` file. If those change between versions, an upgrade
orphans the previous import: leftover DLLs without labels, duplicate analyzers, or an analyzer Unity
no longer recognizes.

They do not change here. [`build/pack-unity.ps1`](../build/pack-unity.ps1) derives each GUID from the
asset's path inside the package - the MD5 of `ObjectPoolLinter:<path>` - rather than generating a
fresh one per build. `RoslynAnalyzers/ObjectPoolLinter.dll` therefore has the same GUID in 1.5.3 that
it had in 1.5.0, and will keep it in every later release. The `.meta` files are not checked into the
repository; they are regenerated identically on every pack.

Two consequences worth knowing:

- **Upgrades are clean.** Replacing the tarball replaces the assets in place. No manual cleanup, and
  nothing else in your project referenced these GUIDs anyway - an analyzer is not referenced by
  scenes, prefabs or scripts.
- **The `.unitypackage` and the UPM tarball do not share GUIDs.** They place the DLLs at different
  paths (`Assets/Plugins/ObjectPoolLinter/...` versus `RoslynAnalyzers/...`), and the path is what the
  GUID is derived from. Switching a project from one artifact to the other is a fresh import, so
  **delete the old one first** - two copies of the same analyzer report every diagnostic twice.

To check a GUID yourself, open the `.meta` next to the DLL in Unity's package cache
(`Library/PackageCache/com.joezhuo.objectpoollinter@.../RoslynAnalyzers/ObjectPoolLinter.dll.meta`);
the `guid:` line is the first field.

## Verifying the install

1. `Window > Package Manager`, switch the scope to **In Project**: `ObjectPoolLinter` is listed with
   the version you installed.
2. Add a throwaway script and compile:

   ```csharp
   using UnityEngine;

   public class Probe : MonoBehaviour
   {
       void Update()
       {
           var list = new System.Collections.Generic.List<int>();
       }
   }
   ```

   The Console shows `OPL001: 'new List<int>' allocates inside the frequently-called method 'Update'`.
3. Delete the script.

**Tested on Unity 6000.4.6f1**, where the tarball imports cleanly and OPL001 is reported during a
batch-mode compile. 2021.3 and 2022.3 are declared but not tested here; see below.

## Minimum Unity version

The package declares `"unity": "2021.3"`, and that value is deliberate. The analyzer is built against
Roslyn 3.8 (`Microsoft.CodeAnalysis.CSharp` 3.8.0), and Unity documents 3.8 as the analyzer API
version for 2021.3 and 2022.3; Unity 6 accepts plugins built against 4.3 or lower, so the same build
covers it. Nothing older is covered by that documentation or tested here, so UPM refuses the package
on older editors rather than installing an analyzer that might silently fail to load.

The value is not hard-coded in `unity/package.json.in`, which carries a `__UNITY_VERSION__`
placeholder. [`build/pack-unity.ps1`](../build/pack-unity.ps1) fills it from `-UnityVersion`, which
defaults to `2021.3`:

```
pwsh build/pack-unity.ps1 -UnityVersion 2022.3
```

The parameter only accepts `<year>.<minor>`, the form UPM expects. Raise the default in the script
when the minimum moves - for example if the analyzer is rebuilt against a newer Roslyn that 2021.3
cannot load - and record it in the changelog, since it stops the package installing on older editors.

After packing, the script reads `package/package.json` back out of the `.tgz` and fails if any
`__NAME__` placeholder survived or if `version` or `unity` differ from what was requested. CI runs
the Unity pack on every push, so a broken manifest never reaches a release.

## Troubleshooting

**No diagnostics at all.** Check the label first: select the DLL in the Project window and confirm the
Inspector shows `RoslynAnalyzer` and no enabled platforms. Then confirm the path in
`Packages/manifest.json` still resolves.

**Every diagnostic reported twice.** Two copies of the analyzer are installed - almost always a
leftover `Assets/Plugins/ObjectPoolLinter/` from the `.unitypackage`. Delete it.

**Diagnostics in the Console but not in the IDE.** Rider and Visual Studio load analyzers from the
generated `.csproj`; regenerate the project files (`Edit > Preferences > External Tools > Regenerate
project files`). VS Code additionally needs background analysis turned on
(`dotnet.backgroundAnalysis.analyzerDiagnosticsScope`).

**`.editorconfig` options ignored.** IDEs honour `object_pool_linter.*` options; Unity's own editor
compile has not been verified to pass them to analyzers. A misspelled option is reported as
[OPL004](rules/OPL004.md). See [Configuring ObjectPoolLinter](configuration.md).

## See also

- [Configuring ObjectPoolLinter](configuration.md)
- [Generating pools with `[ObjectPool]`](source-generator.md)
- [README](../README.md) for the `.unitypackage`, NuGet and manual drop-in routes
