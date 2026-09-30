## Summary

<!-- What does this change, and why? Link the issue it closes: "Fixes #123". -->

## Kind of change

- [ ] Bug fix (false positive, false negative, wrong code fix, crash)
- [ ] New rule, code fix or option
- [ ] Documentation
- [ ] Build, CI or packaging
- [ ] Refactor with no behavior change

## Checklist

See [CONTRIBUTING.md](https://github.com/joezhuo2/ObjectPoolLinter/blob/main/CONTRIBUTING.md) for the commands.

- [ ] Tests cover the change (a false-positive fix has a test that the code no longer reports)
- [ ] `dotnet test ObjectPoolLinter.slnx -c Release` passes
- [ ] `pwsh build/verify-sample.ps1` passes, and its expected list is updated if the sample's warnings changed
- [ ] `dotnet format ObjectPoolLinter.slnx --verify-no-changes --exclude samples/` passes
- [ ] No Roslyn API newer than 3.8 is used in `src/`
- [ ] New or changed diagnostic IDs are in `AnalyzerReleases.Unshipped.md`
- [ ] `README.md` and `docs/` are updated for user-visible changes
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]`
