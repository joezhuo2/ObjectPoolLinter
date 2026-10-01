using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter.Benchmarks
{
    // The overhead of the whole package on a build: the generated project compiled and emitted by the
    // compiler alone (the baseline), then with every ObjectPoolLinter analyzer attached, the way csc runs
    // them: one compilation, bound once, analyzed and emitted. The Ratio column is the build-time cost of
    // adding the package. Each run builds a fresh compilation, so nothing one run binds is reused by the
    // next.
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class CompileBenchmarks
    {
        private ImmutableArray<SyntaxTree> _trees;
        private ImmutableArray<MetadataReference> _references;
        private ImmutableArray<DiagnosticAnalyzer> _analyzers;
        private readonly AnalyzerOptions _options = new(ImmutableArray<AdditionalText>.Empty);

        // Scripts in the project: a small game and a large one.
        [Params(100, 500)]
        public int Files { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _trees = Corpus.Parse(Files);
            _references = Corpus.References();
            _analyzers = Corpus.AllAnalyzers();

            // A corpus that does not compile would time the compiler's error recovery instead.
            var errors = Corpus.Compile(_trees, _references).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("The benchmark corpus does not compile: " + errors[0]);
        }

        [Benchmark(Baseline = true, Description = "Compiler only")]
        public bool CompilerOnly()
        {
            using var output = new MemoryStream();
            return Corpus.Compile(_trees, _references).Emit(output).Success;
        }

        [Benchmark(Description = "Compiler + ObjectPoolLinter")]
        public async Task<bool> WithAnalyzers()
        {
            var compilation = Corpus.Compile(_trees, _references).WithAnalyzers(_analyzers, _options);
            await compilation.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);

            using var output = new MemoryStream();
            return compilation.Compilation.Emit(output).Success;
        }
    }
}
