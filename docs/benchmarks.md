# Build-time cost

An analyzer runs inside every compile, so its cost lands on every build and every keystroke in the IDE.
`benchmarks/ObjectPoolLinter.Benchmarks` measures that cost with
[BenchmarkDotNet](https://benchmarkdotnet.org/): the same project compiled with and without the
analyzers, and the time each analyzer spends in its callbacks.

## The project it compiles

A real Unity project cannot be checked into this repository, so the benchmark generates one: 100 or
500 scripts in ten namespaces, each a `MonoBehaviour` with `Awake`, `Update`, `FixedUpdate` and a cold
method, plus a plain class with collection code, about 100 lines per script, compiled against the
Unity stub the tests use. One script in four allocates in `Update` (`new List<int>()`, a string
concatenation, a LINQ chain with a lambda, a `tag` comparison, a `GetComponent<T>()`), so each rule has
something to report; the rest allocate only where nothing is reported, as most scripts in a shipped
game do. The 500-script project reports OPL001 125 times, OPL002 375, OPL003 125 and OPL009 125.

It compiles against the .NET runtime's own framework assemblies rather than Unity's .NET Standard 2.1
profile, which costs about the same to bind against. The `[ObjectPool]` source generator is not
measured: it runs only for classes carrying the attribute.

## Results

Measured on an AMD Ryzen AI 9 HX 370 (12 cores, 24 threads), Windows 11, .NET 10.0.8, Roslyn 4.14,
ObjectPoolLinter 1.9.1. Your numbers will differ; the ratios are the part to compare.

### Compile overhead

`CompileBenchmarks`: a fresh compilation, bound, emitted to memory, by the compiler alone and then with
every ObjectPoolLinter analyzer and the suppressor attached, the way `csc` runs them in a build. Mean
of 10 runs after 3 warm-ups.

| Scripts | Compiler only | Compiler + ObjectPoolLinter | Ratio | Allocated, compiler only | Allocated, with analyzers |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 232 ms | 537 ms | 2.34 | 59 MB | 119 MB |
| 500 | 1,051 ms | 1,592 ms | 1.64 | 236 MB | 462 MB |

The spread between runs was wide (a standard deviation of 312 ms on the 500-script baseline), so read
the ratios as "between one and a half and two and a half times the compile step", not to the second
decimal. The compile step is one part of a Unity script reload: asset import, domain reload and
serialization are not in these numbers, and on a large project they usually take longer than the
compile itself.

### Time per analyzer

`--analyzer-times`: the time each analyzer spent in its callbacks over the 500-script project, as Roslyn
measures it for `-p:ReportAnalyzer=true`. Median of 7 runs after a warm-up. The times are summed over
the threads analysis runs on, so they add up to more than the wall-clock overhead above.

| Analyzer | Rules | Time |
| --- | --- | ---: |
| `ObjectPoolAnalyzer` | OPL001 | 3,161 ms |
| `HiddenAllocationAnalyzer` | OPL002 | 1,644 ms |
| `NativeContainerDisposeAnalyzer` | OPL007 | 242 ms |
| `ComponentLookupAnalyzer` | OPL009 | 159 ms |
| `UnityApiAllocationAnalyzer` | OPL003 | 34 ms |
| `AssetLoadAnalyzer` | OPL008 | 33 ms |
| `ObjectPoolSuppressionAnalyzer` | the automatic suppressions | 12 ms |
| `JobStructAnalyzer` | OPL006 | 3 ms |
| `TelemetryAnalyzer` | OPL010 (telemetry off) | 0.2 ms |
| `OptionsValidationAnalyzer` | OPL004 | 0.2 ms |

OPL001 and OPL002 account for nearly all of it: they look at every object creation, string operation,
lambda and invocation in the project, where the other rules match a handful of named APIs first and
look further only on a match. They are the place to start when the overhead needs to come down.

### The call graph (1.9.2)

1.9.2 builds a call graph once per compilation so that helpers called from a hot method are checked
([max_call_depth](configuration.md#helpers-called-from-a-hot-method-max_call_depth)). Measured on the
same machine, 1.9.2 and 1.9.1 one after the other, mean of 10 runs:

| Scripts | 1.9.1 | 1.9.2 | Ratio to compiler only, 1.9.1 | Ratio to compiler only, 1.9.2 |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 585 ms | 863 ms | 2.28 | 2.63 |
| 500 | 2,252 ms | 2,133 ms | 2.42 | 2.05 |

The 500-script difference is inside the noise (a standard deviation of 228 ms on the 1.9.1 run); the
100-script run is slower, though its compiler-only baseline moved by as much between the two runs.
Allocations did not change measurably (449 MB and 452 MB at 500 scripts).

`--analyzer-times` moved more: OPL002 from 2.4 s to 4.6 s, OPL009 from 0.2 s to 1.6 s and OPL001 from
4.6 s to 4.9 s at 500 scripts. Those times are summed over threads, and the first rule to ask for the
graph builds it while the others wait, so the waiting is counted once per waiting thread. The graph is
built in parallel and binds a call only when a method in the project has the name it calls, which keeps
the wall-clock cost small. `object_pool_linter.max_call_depth = 0` skips the walk.

## Running it

From the repository root, in Release (BenchmarkDotNet refuses a Debug build):

```bash
dotnet run -c Release --project benchmarks/ObjectPoolLinter.Benchmarks -- --filter *CompileBenchmarks*
```

```bash
dotnet run -c Release --project benchmarks/ObjectPoolLinter.Benchmarks -- --analyzer-times 500
```

The first takes about a minute and a half and writes its tables to
`BenchmarkDotNet.Artifacts/results/`; the second prints its table. Close other heavy programs first:
the compile is parallel and uses every core.

To see the same per-analyzer times on your own project, which is the better guide, build it with:

```bash
dotnet build -p:ReportAnalyzer=true -v:d
```

and look for the analyzer execution time summary near the end of the output. For a Unity project, that
needs the project built outside the editor, as described under
[Unity code compiled outside the editor](../README.md#unity-code-compiled-outside-the-editor).

## When to run it

Before and after a change to an analyzer's registration or matching, to see whether it moved the
compile overhead or its own time. CI builds the benchmark project, so it keeps compiling, but does not
run it: shared CI runners are too noisy for the numbers to mean anything.
