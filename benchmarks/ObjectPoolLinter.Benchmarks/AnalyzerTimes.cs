using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter.Benchmarks
{
    // `--analyzer-times [files]`: the time each analyzer spends in its callbacks, as Roslyn measures it for
    // `-p:ReportAnalyzer=true`, over the generated project. The median of several runs, after a warm-up.
    // The times are summed over threads, so with concurrent analysis they can add up to more than the
    // wall-clock overhead CompileBenchmarks reports. Use them to find the analyzer to look at, and
    // CompileBenchmarks for what a build pays.
    internal static class AnalyzerTimes
    {
        private const int Runs = 7;

        internal static async Task RunAsync(int files)
        {
            var trees = Corpus.Parse(files);
            var references = Corpus.References();
            var analyzers = Corpus.AllAnalyzers();
            var options = new CompilationWithAnalyzersOptions(
                new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                onAnalyzerException: null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: true);

            var times = analyzers.ToDictionary(a => a, _ => new List<double>());
            var reported = new Dictionary<string, int>(StringComparer.Ordinal);

            // The first run warms up the JIT and is not counted.
            for (var run = 0; run <= Runs; run++)
            {
                var compilation = Corpus.Compile(trees, references).WithAnalyzers(analyzers, options);
                var diagnostics = await compilation.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
                if (run == 0)
                {
                    foreach (var group in diagnostics.GroupBy(d => d.Id))
                        reported[group.Key] = group.Count();
                    continue;
                }

                foreach (var analyzer in analyzers)
                {
                    var telemetry = await compilation.GetAnalyzerTelemetryInfoAsync(analyzer, CancellationToken.None).ConfigureAwait(false);
                    times[analyzer].Add(telemetry.ExecutionTime.TotalMilliseconds);
                }
            }

            Console.WriteLine($"Analyzer execution time over {files} generated scripts, median of {Runs} runs");
            Console.WriteLine();
            Console.WriteLine("| Analyzer | Time (ms) |");
            Console.WriteLine("|----------|----------:|");
            foreach (var (analyzer, samples) in times.OrderByDescending(pair => Median(pair.Value)))
                Console.WriteLine($"| {analyzer.GetType().Name} | {Median(samples).ToString("F1", CultureInfo.InvariantCulture)} |");

            Console.WriteLine();
            Console.WriteLine("Reported: " + string.Join(", ", reported.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " " + pair.Value)));
        }

        private static double Median(List<double> samples)
        {
            Debug.Assert(samples.Count > 0);
            var sorted = samples.OrderBy(s => s).ToList();
            return sorted[sorted.Count / 2];
        }
    }
}
