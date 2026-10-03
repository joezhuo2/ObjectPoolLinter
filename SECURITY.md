# Security Policy

## Supported versions

Security fixes are released for the latest minor version only. Upgrade to it to receive them.

| Version | Supported |
| --- | --- |
| 2.0.x | Yes |
| < 2.0 | No |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security problem.**

Report it privately through GitHub:
[Report a vulnerability](https://github.com/joezhuo2/ObjectPoolLinter/security/advisories/new)
(the **Security** tab of the repository, then **Report a vulnerability**).

Include as much of the following as you can:

- the affected version, and the host it ran in (Unity, Visual Studio, Rider, VS Code or the .NET SDK)
- what an attacker can do, and what they need to control to do it
- steps or a minimal project that reproduces it
- a suggested fix, if you have one

## What to expect

- An acknowledgement within 7 days.
- An assessment, and a fix plan if the report is accepted, within 30 days.
- A patched release, and a GitHub security advisory that credits you unless you ask not to be named.

Please keep the report private until the advisory is published.

## Scope

ObjectPoolLinter runs inside the compiler and the IDE, on the source of whoever uses it, so in scope
are, for example:

- analyzer, code fix, generator or suppressor code that can be made to execute unintended code, read or
  write files, or reach the network while a project is compiled or opened
- a code fix or the `[ObjectPool]` generator emitting code that introduces a vulnerability
- input that makes the analyzer hang or exhaust memory, stalling a build or the IDE
- the release pipeline: a way to publish a package, Unity artifact or attestation that this repository's
  release workflow did not produce

Out of scope:

- false positives, false negatives and wrong code fixes with no security impact; open a
  [bug report](https://github.com/joezhuo2/ObjectPoolLinter/issues/new/choose) for those
- vulnerabilities in Unity, Roslyn, the .NET SDK or an IDE themselves; report those to their vendors
- a vulnerable dependency with no reachable impact on the shipped package; Dependabot and NuGet audit
  already track those

## Verifying a release

Every released file is signed with Sigstore build provenance and ships with an SPDX SBOM. To check a
download came from this repository's release workflow:

```bash
gh attestation verify <file> --repo joezhuo2/ObjectPoolLinter
```

See [Releases](README.md#releases) for details.
