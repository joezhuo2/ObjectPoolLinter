using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // T47: every analyzer calls EnableConcurrentExecution, so Roslyn may run its callbacks for different
    // files and methods on several threads at once, and an IDE reuses one analyzer instance for every
    // compilation it analyzes. T50: the analyzers must also finish, and stay exact, on a compilation with
    // hundreds of types under a deep MonoBehaviour hierarchy. These tests run the five hot-path rules
    // (OPL001, OPL002, OPL003, OPL008, OPL009) through CompilationWithAnalyzers with concurrent analysis
    // on, over many files whose .editorconfig options differ, so that options or cached state read for one
    // file, method or compilation and applied to another shows up as a wrong diagnostic count.
    public class ConcurrentExecutionTests
    {
        private const string AdditionalHotMethods = "object_pool_linter.additional_hot_methods";

        // Generous, so that a slow CI machine does not fail the test; a hang or quadratic blow-up still does.
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        // One allocation of each kind the five rules report. OPL002 reports two: the interpolation and the
        // iterator's state machine. Every file calls the same iterator, so OPL002's per-compilation cache
        // of state-machine kinds is read and written from many threads.
        private const string HotBody = @"
            var list = new List<int>();
            var label = $""hp {hp}"";
            var routine = Routines.Spawn();
            var hits = Physics.RaycastAll(ray);
            var prefab = Resources.Load<GameObject>(""Enemy"");
            var body = GetComponent<Rigidbody>();";

        private static readonly ImmutableDictionary<string, int> PerHotMethod = new Dictionary<string, int>
        {
            [ObjectPoolAnalyzer.DiagnosticId] = 1,
            [HiddenAllocationAnalyzer.DiagnosticId] = 2,
            [UnityApiAllocationAnalyzer.DiagnosticId] = 1,
            [AssetLoadAnalyzer.DiagnosticId] = 1,
            [ComponentLookupAnalyzer.DiagnosticId] = 1,
        }.ToImmutableDictionary();

        private static ImmutableArray<DiagnosticAnalyzer> CreateAnalyzers() =>
            ImmutableArray.Create<DiagnosticAnalyzer>(
                new ObjectPoolAnalyzer(),
                new HiddenAllocationAnalyzer(),
                new UnityApiAllocationAnalyzer(),
                new AssetLoadAnalyzer(),
                new ComponentLookupAnalyzer());

        // --- T47: concurrent execution ---

        // 200 files, with `Tick` hot in the odd-numbered ones only. Concurrent analysis must report exactly
        // what the options of each file ask for, and the same diagnostics as a sequential run, every time.
        [Fact]
        public async Task ConcurrentAnalysis_PerFileOptions_MatchSequentialAndExpected()
        {
            var project = await BuildProjectAsync(units: 200, depth: 10, tickIsHot: index => index % 2 == 1);
            var expected = project.Expected;

            var sequential = await AnalyzeAsync(project, CreateAnalyzers(), concurrent: false);
            AssertSameDiagnostics(expected, sequential);

            for (var run = 0; run < 3; run++)
            {
                var concurrent = await AnalyzeAsync(project, CreateAnalyzers(), concurrent: true);
                AssertSameDiagnostics(expected, concurrent);
            }
        }

        // One set of analyzer instances shared by two compilations analyzed at the same time, as an IDE
        // does. `Tick` is hot everywhere in the first and nowhere in the second, so options or detector
        // state that outlives its compilation shows up in the other one's diagnostics.
        [Fact]
        public async Task SharedAnalyzerInstances_ParallelCompilations_DoNotLeakState()
        {
            var tickHot = await BuildProjectAsync(units: 60, depth: 5, tickIsHot: _ => true);
            var tickCold = await BuildProjectAsync(units: 60, depth: 5, tickIsHot: _ => false);
            Assert.True(tickHot.Expected.Length > tickCold.Expected.Length);

            var analyzers = CreateAnalyzers();
            for (var run = 0; run < 3; run++)
            {
                var results = await Task.WhenAll(
                    Task.Run(() => AnalyzeAsync(tickHot, analyzers, concurrent: true)),
                    Task.Run(() => AnalyzeAsync(tickCold, analyzers, concurrent: true)),
                    Task.Run(() => AnalyzeAsync(tickHot, analyzers, concurrent: true)),
                    Task.Run(() => AnalyzeAsync(tickCold, analyzers, concurrent: true)));

                AssertSameDiagnostics(tickHot.Expected, results[0]);
                AssertSameDiagnostics(tickCold.Expected, results[1]);
                AssertSameDiagnostics(tickHot.Expected, results[2]);
                AssertSameDiagnostics(tickCold.Expected, results[3]);
            }
        }

        // --- T50: a large compilation ---

        // 400 MonoBehaviours spread over a 150-deep inheritance chain, each file also holding a plain class
        // whose `Update` is not a Unity message. Every MonoBehaviour's `Update` reports, `Tick` reports in
        // every third file, and no plain class reports.
        [Fact]
        public async Task LargeCompilation_DeepHierarchy_FinishesWithExactDiagnostics()
        {
            var project = await BuildProjectAsync(units: 400, depth: 150, tickIsHot: index => index % 3 == 0);

            var actual = await AnalyzeAsync(project, CreateAnalyzers(), concurrent: true);

            AssertSameDiagnostics(project.Expected, actual);
            Assert.Equal(
                (400 + 134) * PerHotMethod.Values.Sum(),
                actual.Sum(entry => entry.Count));
        }

        private sealed record Project(
            CSharpCompilation Compilation,
            AnalyzerConfigOptionsProvider Options,
            ImmutableArray<(string File, string Method, string Id, int Count)> Expected);

        // `Base0` derives from MonoBehaviour and each `Base{n}` from `Base{n - 1}`; `Unit{i}` derives from
        // `Base{i % depth}`, so the deepest units sit depth + 1 levels below MonoBehaviour.
        private static async Task<Project> BuildProjectAsync(int units, int depth, Func<int, bool> tickIsHot)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(SharedUnityStub.Source, parseOptions, "/UnityStub.cs"),
                CSharpSyntaxTree.ParseText(Routines, parseOptions, "/Routines.cs"),
                CSharpSyntaxTree.ParseText(Hierarchy(depth), parseOptions, "/Hierarchy.cs"),
            };

            var optionsByPath = new Dictionary<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
            var expected = new List<(string File, string Method, string Id, int Count)>();

            for (var index = 0; index < units; index++)
            {
                var path = $"/Units/Unit{index}.cs";
                trees.Add(CSharpSyntaxTree.ParseText(Unit(index, depth), parseOptions, path));

                var hotMethods = new List<string> { "Update" };
                if (tickIsHot(index))
                {
                    hotMethods.Add("Tick");
                    optionsByPath[path] = ImmutableDictionary.CreateRange(
                        StringComparer.OrdinalIgnoreCase,
                        new[] { new KeyValuePair<string, string>(AdditionalHotMethods, "Tick") });
                }

                foreach (var method in hotMethods)
                {
                    foreach (var rule in PerHotMethod)
                    {
                        expected.Add((path, $"Unit{index}.{method}", rule.Key, rule.Value));
                    }
                }
            }

            var references = await TestReferenceAssemblies.Default.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);
            var compilation = CSharpCompilation.Create(
                "LargeGame",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            return new Project(compilation, new OptionsProvider(optionsByPath), Order(expected));
        }

        // Runs the analyzers and groups what they report by file, method and rule. Analyzer exceptions are
        // collected and fail the test rather than surfacing as AD0001 diagnostics.
        private static async Task<ImmutableArray<(string File, string Method, string Id, int Count)>> AnalyzeAsync(
            Project project,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            bool concurrent)
        {
            var exceptions = new ConcurrentBag<Exception>();
            var options = new CompilationWithAnalyzersOptions(
                new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, project.Options),
                onAnalyzerException: (exception, _, _) => exceptions.Add(exception),
                concurrentAnalysis: concurrent,
                logAnalyzerExecutionTime: false);

            using var timeout = new CancellationTokenSource(Timeout);
            var diagnostics = await project.Compilation
                .WithAnalyzers(analyzers, options)
                .GetAnalyzerDiagnosticsAsync(timeout.Token);

            Assert.Empty(exceptions);

            return Order(diagnostics
                .GroupBy(diagnostic => (
                    File: diagnostic.Location.SourceTree!.FilePath,
                    Method: EnclosingMethod(diagnostic.Location),
                    diagnostic.Id))
                .Select(group => (group.Key.File, group.Key.Method, group.Key.Id, group.Count())));
        }

        // Compares element by element (Assert.Equal compares two ImmutableArrays by reference) and names
        // the entries that differ, which a diff of hundreds of tuples would bury.
        private static void AssertSameDiagnostics(
            ImmutableArray<(string File, string Method, string Id, int Count)> expected,
            ImmutableArray<(string File, string Method, string Id, int Count)> actual)
        {
            var missing = expected.Except(actual).Take(10);
            var extra = actual.Except(expected).Take(10);
            Assert.True(
                expected.SequenceEqual(actual),
                $"Missing: {string.Join(", ", missing)}{Environment.NewLine}Unexpected: {string.Join(", ", extra)}");
        }

        private static ImmutableArray<(string File, string Method, string Id, int Count)> Order(
            IEnumerable<(string File, string Method, string Id, int Count)> entries) =>
            entries
                .OrderBy(entry => entry.File, StringComparer.Ordinal)
                .ThenBy(entry => entry.Method, StringComparer.Ordinal)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToImmutableArray();

        private static string EnclosingMethod(Location location)
        {
            var node = location.SourceTree!.GetRoot().FindNode(location.SourceSpan);
            var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
            var type = method.Ancestors().OfType<TypeDeclarationSyntax>().First();
            return $"{type.Identifier.Text}.{method.Identifier.Text}";
        }

        private const string Routines = @"
using System.Collections;

namespace Game.Units
{
    public static class Routines
    {
        public static IEnumerator Spawn()
        {
            yield return null;
        }
    }
}
";

        private static string Hierarchy(int depth)
        {
            var source = new StringBuilder();
            source.AppendLine("using UnityEngine;");
            source.AppendLine();
            source.AppendLine("namespace Game.Units");
            source.AppendLine("{");
            source.AppendLine("    public class Base0 : MonoBehaviour { }");
            for (var level = 1; level < depth; level++)
            {
                source.AppendLine($"    public class Base{level} : Base{level - 1} {{ }}");
            }

            source.AppendLine("}");
            return source.ToString();
        }

        // The plain class has the same `Update` without the component lookup, which it cannot call. It is
        // not a MonoBehaviour and `Update` is never listed as an additional hot method, so it never reports.
        private static string Unit(int index, int depth) => $@"
using System.Collections.Generic;
using UnityEngine;

namespace Game.Units
{{
    public class Unit{index} : Base{index % depth}
    {{
        int hp;
        Ray ray;

        void Update()
        {{{HotBody}
        }}

        public void Tick()
        {{{HotBody}
        }}
    }}

    public class Plain{index}
    {{
        int hp;
        Ray ray;

        void Update()
        {{
            var list = new List<int>();
            var label = $""hp {{hp}}"";
            var routine = Routines.Spawn();
            var hits = Physics.RaycastAll(ray);
            var prefab = Resources.Load<GameObject>(""Enemy"");
        }}
    }}
}}
";

        private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
        {
            private readonly IReadOnlyDictionary<string, ImmutableDictionary<string, string>> _optionsByPath;

            public OptionsProvider(IReadOnlyDictionary<string, ImmutableDictionary<string, string>> optionsByPath)
            {
                _optionsByPath = optionsByPath;
            }

            public override AnalyzerConfigOptions GlobalOptions => Options.Empty;

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
                _optionsByPath.TryGetValue(tree.FilePath, out var values) ? new Options(values) : Options.Empty;

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
        }

        private sealed class Options : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new(ImmutableDictionary<string, string>.Empty);

            private readonly ImmutableDictionary<string, string> _values;

            public Options(ImmutableDictionary<string, string> values)
            {
                _values = values;
            }

            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
                _values.TryGetValue(key, out value);
        }
    }
}
