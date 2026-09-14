using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace ObjectPoolLinter.Tests
{
    // Runs a single analyzer over C# test code. Used by the OPL002 and OPL003 tests; the OPL001 tests
    // keep their own harness.
    internal sealed class HotPathAnalyzerTest<TAnalyzer> : AnalyzerTest<DefaultVerifier>
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        public HotPathAnalyzerTest(string source, string unityStub, params DiagnosticResult[] expected)
        {
            TestCode = source;
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
            TestState.Sources.Add(unityStub);
            ExpectedDiagnostics.AddRange(expected);
        }

        public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.Latest;

        public override string Language => LanguageNames.CSharp;

        protected override string DefaultFileExt => "cs";

        protected override CompilationOptions CreateCompilationOptions()
        {
            return new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        }

        protected override ParseOptions CreateParseOptions()
        {
            return new CSharpParseOptions(LanguageVersion);
        }

        protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
        {
            yield return new TAnalyzer();
        }

        public HotPathAnalyzerTest<TAnalyzer> WithEditorConfig(string options)
        {
            TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n\n[*]\n" + options + "\n"));
            return this;
        }
    }
}
