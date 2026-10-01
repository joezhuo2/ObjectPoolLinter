# Contributing to ObjectPoolLinter

Thanks for helping. Bug reports, false-positive reports, new rule ideas and pull requests are all
welcome. This guide covers setting up the repository, the checks CI runs, and what a pull request
needs before it is merged.

For a security problem, do not open a public issue; follow [SECURITY.md](SECURITY.md) instead.

## Reporting a problem

Open an issue with the matching template:

- **Bug report** for a crash, a wrong code fix, a missed allocation (false negative) or a diagnostic
  on code that does not allocate (false positive). The most useful report is a short C# snippet that
  reproduces it, the diagnostic ID, and the host (Unity version, Visual Studio, Rider, VS Code or the
  .NET SDK) with the analyzer version.
- **Feature request** for a new rule, a new code fix, a new configuration option or a pattern the
  analyzer should recognize.

Check [Known limitations](README.md#known-limitations) first: call-graph analysis and allocations the
analyzer cannot see statically are out of scope on purpose.

## Setting up

You need the **.NET 10 SDK**. `global.json` pins `10.0.100` with `rollForward: latestFeature`, so any
`10.0.1xx` or later feature band works. PowerShell 7 (`pwsh`) runs the scripts under `build/`. Unity
is not needed: the tests and the sample compile against Unity stubs.

```bash
git clone https://github.com/joezhuo2/ObjectPoolLinter.git
cd ObjectPoolLinter
dotnet build ObjectPoolLinter.slnx -c Release
```

Building the solution also builds `samples/SampleUnityCode`, which prints OPL001 to OPL003 and OPL006
to OPL009 warnings. Those are expected.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/ObjectPoolLinter` | Analyzers, the `[ObjectPool]` source generator and the diagnostic suppressor. Built against Roslyn 3.8 for Unity. |
| `src/ObjectPoolLinter.CodeFixes` | Code fix providers. |
| `src/ObjectPoolLinter.Package` | The NuGet package project. Holds `<Version>`. |
| `tests/ObjectPoolLinter.Tests` | xUnit tests using `Microsoft.CodeAnalysis.Testing`. |
| `benchmarks/ObjectPoolLinter.Benchmarks` | BenchmarkDotNet project measuring the analyzers' build-time cost ([docs/benchmarks.md](docs/benchmarks.md)). |
| `samples/SampleUnityCode` | End-to-end sample compiled with the analyzer attached. |
| `docs/` | Per-rule pages (`docs/rules/OPLxxx.md`), configuration, suppressions, telemetry, benchmarks, the generator and Unity packaging. |
| `unity/` | Template for the UPM `package.json`. |
| `build/` | Coverage, vulnerability, sample, symbol and Unity packing scripts. |

## Running the checks

CI runs everything below on Ubuntu, Windows and macOS, except formatting and the vulnerability check,
which run once on Linux. Run them locally before opening a pull request.

**Tests.** They compile their sources against the .NET Standard 2.1 reference assemblies (Unity
2021.3's default profile). CI also runs them against the newest reference assemblies:

```bash
dotnet test ObjectPoolLinter.slnx -c Release
```

```bash
OPL_TEST_REFERENCE_ASSEMBLIES=newest dotnet test ObjectPoolLinter.slnx -c Release
```

**Coverage.** Line coverage of the analyzer and code fix assemblies must stay at or above 90%:

```bash
dotnet test ObjectPoolLinter.slnx -c Release --collect:"XPlat Code Coverage" --settings tests/ObjectPoolLinter.Tests/coverage.runsettings --results-directory artifacts/coverage
```

```bash
pwsh build/check-coverage.ps1
```

**Sample verification.** Rebuilds the sample and checks it reports exactly the expected warnings, no
more and no fewer. If your change adds or removes a diagnostic in the sample, update the expected list
in `build/verify-sample.ps1`:

```bash
pwsh build/verify-sample.ps1
```

**Formatting.** Enforces the root `.editorconfig`. Drop `--verify-no-changes` to apply the fixes:

```bash
dotnet format ObjectPoolLinter.slnx --verify-no-changes --exclude samples/
```

**Vulnerable packages.** Fails on any direct or transitive package with a known vulnerability:

```bash
pwsh build/check-vulnerable.ps1
```

**Build warnings.** CI builds with `-warnaserror`, so a new warning fails the build:

```bash
dotnet build ObjectPoolLinter.slnx -c Release -warnaserror
```

## Packing

```bash
dotnet pack src/ObjectPoolLinter.Package/ObjectPoolLinter.Package.csproj -c Release -o artifacts/nuget
```

```bash
dotnet run --file build/verify-symbols.cs -- artifacts/nuget
```

```bash
pwsh build/pack-unity.ps1
```

The first writes the `.nupkg` and `.snupkg`, the second checks the symbol package the way nuget.org
does, and the third writes the `.unitypackage` and UPM tarball to `artifacts/unity/`. To try a local
build in Unity, import the `.unitypackage` or add the tarball through the Package Manager.

## Making a change

### Code

- Match the surrounding code. `.editorconfig` covers formatting; for everything else, follow the file
  you are editing.
- The analyzer and code fix projects target `netstandard2.0` and Roslyn 3.8.0. Do not use a Roslyn API
  newer than 3.8 or raise the version in `Directory.Packages.props`: Unity 2021.3 and 2022.3 only load
  analyzers built against 3.8.
- Package versions live in `Directory.Packages.props`, not in project files.
- Analyzers must be fast and allocation-light themselves. They run on every keystroke in the IDE.
  A change to an analyzer's registration or matching should be measured with the benchmarks before
  and after: [docs/benchmarks.md](docs/benchmarks.md#running-it) has the commands. CI builds the
  benchmark project but does not run it.
- A new rule that reports from source records its diagnostics with `TelemetryCounts.Record` next to
  `ReportDiagnostic`, so the [telemetry](docs/telemetry.md) summary counts it. Telemetry never leaves
  the machine: do not add anything to it that names a file, type, member or person.

### Tests

Every change to behavior needs a test. Tests use the `{|#0:...|}` markup of
`Microsoft.CodeAnalysis.Testing` and compile against the shared Unity stub,
`tests/ObjectPoolLinter.Tests/SharedUnityStub.cs`. If a test needs a Unity member the stub lacks, add it
there rather than declaring a local copy. A false-positive fix needs a test that the code no longer
reports; a code fix needs a test of the fixed output.

### New or changed diagnostics

- Add a new diagnostic ID to `src/ObjectPoolLinter/AnalyzerReleases.Unshipped.md`. A changed default
  severity or category also goes there.
- Give each new rule a page under `docs/rules/` with what it reports, what it does not report and why,
  the code fixes, and how to suppress it. Point the descriptor's `HelpLinkUri` at that page.
- Update the rule table and any affected sections of `README.md`.
- A public type or member needs an XML doc comment; the build fails on a missing one (CS1591). An
  `override` takes `/// <inheritdoc/>`.
- A breaking change, as defined in [docs/migration.md](docs/migration.md#what-counts-as-a-breaking-change),
  waits for the next major version and gets a section in that guide. A new rule, or a new detection in
  an existing rule, gets a row in its
  [Upgrading within 1.x](docs/migration.md#upgrading-within-1x) table.

### Changelog

Add an entry under `## [Unreleased]` in `CHANGELOG.md`, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
sections (`Added`, `Changed`, `Fixed`, `Removed`). Write it for someone upgrading: what changes for
them, not how the code changed. The maintainer moves it under a version heading at release time.

## Pull requests

1. Fork the repository and branch from `main`.
2. Make the change with its tests, docs and changelog entry.
3. Run the checks above.
4. Open a pull request and fill in the template. Keep one change per pull request; a refactor that a
   fix depends on can go in a separate commit of the same pull request.

GitHub requests a review from the code owners in `.github/CODEOWNERS` for the files a pull request
touches. CI must pass before merge. Pull requests also run `dependency-review`, which fails if the change adds a
package or action with a known vulnerability.

## Releases

Releases are cut by the maintainer: bump `<Version>` in
`src/ObjectPoolLinter.Package/ObjectPoolLinter.Package.csproj`, move the `Unreleased` changelog entries
under the new version, and push a signed `v*` tag. The release workflow is described under
[Releases](README.md#releases).

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
