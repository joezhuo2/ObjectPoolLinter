using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // Opt-in telemetry (F78): OPL010's per-rule summary, and the local file the code fixes count into.
    // Every test that writes the file points it at a temporary path of its own.
    [Collection(nameof(TelemetryTests))]
    public sealed class TelemetryTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ObjectPoolLinterTests", Guid.NewGuid().ToString("N"));

        public TelemetryTests()
        {
            TelemetryLog.PathOverride = Path.Combine(_directory, "telemetry.json");
        }

        public void Dispose()
        {
            TelemetryLog.PathOverride = null;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private const string Source = @"
using System.Collections.Generic;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        var a = {|#0:new List<int>()|};
        var b = {|#1:new List<int>()|};
        var name = {|#2:gameObject.name|};
    }
}
";

        private static DiagnosticResult Allocation(int location) =>
            new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(location);

        private static DiagnosticResult UnityApi(int location) =>
            new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(location);

        private static DiagnosticResult Summary(string counts, int sourceFiles) =>
            new DiagnosticResult(TelemetryAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithArguments(counts, sourceFiles);

        // --- OPL010 ---

        [Fact]
        public async Task Off_ByDefault_NoSummary()
        {
            await new MultiAnalyzerTest(Source, Allocation(0), Allocation(1), UnityApi(2)).RunAsync();
        }

        [Fact]
        public async Task Off_WhenFalse_NoSummary()
        {
            await new MultiAnalyzerTest(Source, Allocation(0), Allocation(1), UnityApi(2))
                .WithEditorConfig("object_pool_linter.telemetry = false")
                .RunAsync();
        }

        [Theory]
        [InlineData("true")]
        [InlineData("True")]
        [InlineData(" TRUE ")]
        public async Task On_ReportsCountsMostFrequentFirst(string value)
        {
            // The test file and the Unity stub.
            await new MultiAnalyzerTest(Source, Allocation(0), Allocation(1), UnityApi(2), Summary("OPL001: 2, OPL003: 1", 2))
                .WithEditorConfig("object_pool_linter.telemetry = " + value)
                .RunAsync();
        }

        [Fact]
        public async Task On_NothingReported_SaysSo()
        {
            const string clean = @"
using UnityEngine;

public class Idle : MonoBehaviour
{
    void Update() { }
}
";

            await new MultiAnalyzerTest(clean, Summary("no diagnostics", 2))
                .WithEditorConfig("object_pool_linter.telemetry = true")
                .RunAsync();
        }

        // Pragma-suppressed diagnostics still count: the summary is what the rules reported.
        [Fact]
        public async Task On_CountsDiagnosticsBeforePragmaSuppression()
        {
            const string suppressed = @"
using System.Collections.Generic;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
#pragma warning disable OPL001
        var a = new List<int>();
#pragma warning restore OPL001
    }
}
";

            await new MultiAnalyzerTest(suppressed, Summary("OPL001: 1", 2))
                .WithEditorConfig("object_pool_linter.telemetry = true")
                .RunAsync();
        }

        [Fact]
        public async Task InvalidValue_ReportsOpl004()
        {
            var test = new HotPathAnalyzerTest<OptionsValidationAnalyzer>(
                    "public class C { }",
                    SharedUnityStub.Source,
                    new DiagnosticResult(OptionsValidationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                        .WithArguments("'yes' in 'object_pool_linter.telemetry' is not true or false."))
                .WithEditorConfig("object_pool_linter.telemetry = yes");

            await test.RunAsync();
        }

        [Fact]
        public async Task KnownOption_NoOpl004()
        {
            await new HotPathAnalyzerTest<OptionsValidationAnalyzer>("public class C { }", SharedUnityStub.Source)
                .WithEditorConfig("object_pool_linter.telemetry = true")
                .RunAsync();
        }

        // --- The local file ---

        [Fact]
        public void Increment_CreatesFileAndCounts()
        {
            TelemetryLog.Increment(TelemetryLog.CodeFixesSection, "ObjectPoolLinterCacheLambda");
            TelemetryLog.Increment(TelemetryLog.CodeFixesSection, "ObjectPoolLinterCacheLambda");
            TelemetryLog.Increment(TelemetryLog.FixAllSection, "ObjectPoolLinterUseCompareTag");

            var data = TelemetryLog.Parse(File.ReadAllText(TelemetryLog.FilePath));
            Assert.Equal(2, data[TelemetryLog.CodeFixesSection]["ObjectPoolLinterCacheLambda"]);
            Assert.Equal(1, data[TelemetryLog.FixAllSection]["ObjectPoolLinterUseCompareTag"]);
        }

        [Fact]
        public void Format_IsTheDocumentedShape()
        {
            TelemetryLog.Increment(TelemetryLog.CodeFixesSection, "ObjectPoolLinterUseCompareTag");
            TelemetryLog.Increment(TelemetryLog.CodeFixesSection, "ObjectPoolLinterCacheLambda");

            Assert.Equal(
                "{\n  \"schema\": 1,\n  \"codeFixes\": {\n    \"ObjectPoolLinterCacheLambda\": 1,\n    \"ObjectPoolLinterUseCompareTag\": 1\n  },\n  \"fixAll\": {}\n}\n",
                File.ReadAllText(TelemetryLog.FilePath));
        }

        [Fact]
        public void Increment_UnreadableFile_StartsOver()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(TelemetryLog.FilePath, "not json at all");

            TelemetryLog.Increment(TelemetryLog.CodeFixesSection, "ObjectPoolLinterLinqToLoop");

            var data = TelemetryLog.Parse(File.ReadAllText(TelemetryLog.FilePath));
            Assert.Equal(1, data[TelemetryLog.CodeFixesSection]["ObjectPoolLinterLinqToLoop"]);
        }

        [Fact]
        public void Increment_FileLockedByAnotherProcess_DropsTheCountWithoutThrowing()
        {
            // FileShare.None is a mandatory lock only on Windows; elsewhere .NET emulates it with advisory
            // locks, which this test does not try to pin down.
            if (!OperatingSystem.IsWindows()) return;

            Directory.CreateDirectory(_directory);
            using (new FileStream(TelemetryLog.FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                TelemetryLog.Increment(TelemetryLog.CodeFixesSection, "ObjectPoolLinterLinqToLoop");

            Assert.Equal("", File.ReadAllText(TelemetryLog.FilePath));
        }

        // --- Code actions ---

        [Fact]
        public void Wrap_Off_ReturnsTheActionItself()
        {
            var action = CodeAction.Create("Fix", _ => Task.FromResult(CreateDocument(telemetry: false)), "Key");
            Assert.Same(action, TelemetryCodeAction.Wrap(action, telemetry: false));
        }

        [Fact]
        public async Task Wrap_On_CountsWhenAppliedButNotWhenPreviewed()
        {
            var document = CreateDocument(telemetry: true);
            var action = TelemetryCodeAction.Wrap(CodeAction.Create("Fix", _ => Task.FromResult(document), "ObjectPoolLinterUseGetTouch"), telemetry: true);

            Assert.Equal("Fix", action.Title);
            Assert.Equal("ObjectPoolLinterUseGetTouch", action.EquivalenceKey);

            var preview = await action.GetPreviewOperationsAsync(CancellationToken.None);
            Assert.All(preview, operation => Assert.IsType<ApplyChangesOperation>(operation));
            foreach (var operation in preview) operation.Apply(document.Project.Solution.Workspace, CancellationToken.None);
            Assert.False(File.Exists(TelemetryLog.FilePath));

            var operations = await action.GetOperationsAsync(CancellationToken.None);
            Assert.IsType<ApplyChangesOperation>(operations[0]);
            Assert.Equal(2, operations.Length);

            operations[1].Apply(document.Project.Solution.Workspace, CancellationToken.None);
            var data = TelemetryLog.Parse(File.ReadAllText(TelemetryLog.FilePath));
            Assert.Equal(1, data[TelemetryLog.CodeFixesSection]["ObjectPoolLinterUseGetTouch"]);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("false", false)]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        public async Task IsEnabled_ReadsTheDocumentsEditorConfig(string? value, bool expected)
        {
            var document = CreateDocument(value);
            Assert.Equal(expected, await TelemetryCodeAction.IsEnabledAsync(document, CancellationToken.None));
        }

        [Fact]
        public async Task FixAll_On_CountsOnceInTheFixAllSection()
        {
            var document = CreateDocument(telemetry: true);
            var provider = new HiddenAllocationCodeFixProvider();
            var context = new FixAllContext(
                document,
                provider,
                FixAllScope.Document,
                "ObjectPoolLinterCacheLambda",
                new[] { HiddenAllocationAnalyzer.DiagnosticId },
                new EmptyDiagnosticProvider(),
                CancellationToken.None);

            var action = await provider.GetFixAllProvider().GetFixAsync(context);
            Assert.NotNull(action);

            var operations = await action!.GetOperationsAsync(CancellationToken.None);
            foreach (var operation in operations.Where(o => o is not ApplyChangesOperation))
                operation.Apply(document.Project.Solution.Workspace, CancellationToken.None);

            var data = TelemetryLog.Parse(File.ReadAllText(TelemetryLog.FilePath));
            Assert.Equal(1, data[TelemetryLog.FixAllSection]["ObjectPoolLinterCacheLambda"]);
            Assert.Empty(data[TelemetryLog.CodeFixesSection]);
        }

        private static Document CreateDocument(bool telemetry) => CreateDocument(telemetry ? "true" : null);

        private static Document CreateDocument(string? telemetry)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ObjectPoolLinterTelemetry");
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("Game", LanguageNames.CSharp);
            var document = project.AddDocument("Spawner.cs", SourceText.From("public class Spawner { }"), filePath: Path.Combine(directory, "Spawner.cs"));

            if (telemetry == null) return document;

            var config = "root = true\n\n[*.cs]\nobject_pool_linter.telemetry = " + telemetry + "\n";
            return document.Project
                .AddAnalyzerConfigDocument(".editorconfig", SourceText.From(config), filePath: Path.Combine(directory, ".editorconfig"))
                .Project.GetDocument(document.Id)!;
        }

        private sealed class EmptyDiagnosticProvider : FixAllContext.DiagnosticProvider
        {
            public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
                Task.FromResult(Enumerable.Empty<Diagnostic>());

            public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
                Task.FromResult(Enumerable.Empty<Diagnostic>());

            public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
                Task.FromResult(Enumerable.Empty<Diagnostic>());
        }

        // Every rule that records into the counts, plus OPL010 itself.
        private sealed class MultiAnalyzerTest : AnalyzerTest<DefaultVerifier>
        {
            public MultiAnalyzerTest(string source, params DiagnosticResult[] expected)
            {
                TestCode = source;
                ReferenceAssemblies = TestReferenceAssemblies.Default;
                TestState.Sources.Add(SharedUnityStub.Source);
                ExpectedDiagnostics.AddRange(expected);
            }

            public override string Language => LanguageNames.CSharp;

            protected override string DefaultFileExt => "cs";

            protected override CompilationOptions CreateCompilationOptions() =>
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            protected override ParseOptions CreateParseOptions() =>
                new CSharpParseOptions(LanguageVersion.Latest);

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                yield return new ObjectPoolAnalyzer();
                yield return new HiddenAllocationAnalyzer();
                yield return new UnityApiAllocationAnalyzer();
                yield return new TelemetryAnalyzer();
            }

            public MultiAnalyzerTest WithEditorConfig(string options)
            {
                TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n\n[*]\n" + options + "\n"));
                return this;
            }
        }
    }
}
