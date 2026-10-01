using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // T48: the fix-all provider each code fix hands to the IDE. OPL001's fixes are independent of each
    // other and go through Roslyn's batch fixer; ObjectPoolCodeFixProviderTests runs a batch fix-all over a
    // document to prove it. The OPL002 and OPL003 fixes each pick a free field name and may append to
    // Awake(), so fixes merged from one starting document would collide: their fix-all applies the fixes
    // one at a time instead. The tests below run fix-all over the document, project and solution, and
    // each needs the second fix to see what the first one added.
    public class FixAllProviderTests
    {
        [Fact]
        public void ObjectPoolCodeFixProvider_UsesBatchFixer()
        {
            var provider = Assert.IsType<TelemetryFixAllProvider>(new ObjectPoolCodeFixProvider().GetFixAllProvider());
            Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.Inner);
        }

        [Fact]
        public void HiddenAllocationCodeFixProvider_FixesOneAtATime()
        {
            Assert.IsType<SequentialFixAllProvider>(new HiddenAllocationCodeFixProvider().GetFixAllProvider());
        }

        [Fact]
        public void UnityApiAllocationCodeFixProvider_FixesOneAtATime()
        {
            Assert.IsType<SequentialFixAllProvider>(new UnityApiAllocationCodeFixProvider().GetFixAllProvider());
        }

        // A new code fix provider fails this until a test above decides its fix-all behavior.
        [Fact]
        public void EveryCodeFixProvider_IsCovered()
        {
            var providers = typeof(ObjectPoolCodeFixProvider).Assembly.GetTypes()
                .Where(type => typeof(CodeFixProvider).IsAssignableFrom(type) && !type.IsAbstract)
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal);

            Assert.Equal(
                new[] { "HiddenAllocationCodeFixProvider", "ObjectPoolCodeFixProvider", "UnityApiAllocationCodeFixProvider" },
                providers);
        }

        // Both lambdas would be cached as `_next` and both would add an Awake() if the fixes were merged.
        [Fact]
        public async Task CacheLambda_FixAll_PicksDistinctNamesAndSharesAwake()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Run(Func<int> next) { }

    void Update()
    {
        var count = 3;
        Run({|#0:() => count + 1|});
        var wave = 2;
        Run({|#1:() => wave * 2|});
    }
}
";

            var fixedSource = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    private int count;
    private Func<int> _next;
    private int wave;
    private Func<int> _next2;

    private void Awake()
    {
        _next = () => count + 1;
        _next2 = () => wave * 2;
    }

    void Run(Func<int> next) { }

    void Update()
    {
        count = 3;
        Run(_next);
        wave = 2;
        Run(_next2);
    }
}
";

            var test = new FixAllTest<HiddenAllocationAnalyzer, HiddenAllocationCodeFixProvider>(source, fixedSource, "ObjectPoolLinterCacheLambda");
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithLocation(0));
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithLocation(1));
            await test.RunAsync();
        }

        // Fix-all applies only the fix the user picked: the string stays, and is still reported.
        [Fact]
        public async Task CacheLambda_FixAll_LeavesOtherFixesAlone()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Run(Func<int> next) { }
    void Log(string text) { }

    void Update()
    {
        var count = 3;
        Run({|#0:() => count + 1|});
        Log({|#1:$""count {count}""|});
    }
}
";

            var fixedSource = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    private int count;
    private Func<int> _next;

    private void Awake()
    {
        _next = () => count + 1;
    }

    void Run(Func<int> next) { }
    void Log(string text) { }

    void Update()
    {
        count = 3;
        Run(_next);
        Log({|#0:$""count {count}""|});
    }
}
";

            var test = new FixAllTest<HiddenAllocationAnalyzer, HiddenAllocationCodeFixProvider>(source, fixedSource, "ObjectPoolLinterCacheLambda");
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithLocation(0));
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithLocation(1));
            test.FixedState.ExpectedDiagnostics.Add(new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithLocation(0));
            await test.RunAsync();
        }

        // Both buffers would be named `_collidersBuffer` if the fixes were merged.
        [Fact]
        public async Task BufferOverload_FixAll_PicksDistinctNames()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    void Update()
    {
        var colliders = {|#0:GetComponentsInChildren<Collider>()|};
        for (int i = 0; i < colliders.Length; i++) { }
    }

    void LateUpdate()
    {
        var colliders = {|#1:GetComponentsInChildren<Collider>()|};
        for (int i = 0; i < colliders.Length; i++) { }
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    private readonly List<Collider> _collidersBuffer = new List<Collider>();
    private readonly List<Collider> _collidersBuffer2 = new List<Collider>();

    void Update()
    {
        GetComponentsInChildren<Collider>(_collidersBuffer);
        var colliders = _collidersBuffer;
        for (int i = 0; i < colliders.Count; i++) { }
    }

    void LateUpdate()
    {
        GetComponentsInChildren<Collider>(_collidersBuffer2);
        var colliders = _collidersBuffer2;
        for (int i = 0; i < colliders.Count; i++) { }
    }
}
";

            var test = new FixAllTest<UnityApiAllocationAnalyzer, UnityApiAllocationCodeFixProvider>(source, fixedSource, "ObjectPoolLinterUseBufferOverload");
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0));
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(1));
            await test.RunAsync();
        }

        [Fact]
        public async Task CompareTag_FixAll_RewritesEveryComparison()
        {
            var source = @"
using UnityEngine;

public class Trigger : MonoBehaviour
{
    void OnTriggerStay(Collider other)
    {
        if ({|#0:other.tag|} == ""Player"") { }
        if ({|#1:other.tag|} != ""Enemy"") { }
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Trigger : MonoBehaviour
{
    void OnTriggerStay(Collider other)
    {
        if (other.CompareTag(""Player"")) { }
        if (!other.CompareTag(""Enemy"")) { }
    }
}
";

            var test = new FixAllTest<UnityApiAllocationAnalyzer, UnityApiAllocationCodeFixProvider>(source, fixedSource, "ObjectPoolLinterUseCompareTag");
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0));
            test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(1));
            await test.RunAsync();
        }

        // Runs the fixes one at a time (FixedCode) and as a fix-all in each scope (BatchFixedCode, the same
        // code by default): a fix-all must leave the code a user fixing each diagnostic in turn would get.
        private sealed class FixAllTest<TAnalyzer, TProvider> : CodeFixTest<DefaultVerifier>
            where TAnalyzer : DiagnosticAnalyzer, new()
            where TProvider : CodeFixProvider, new()
        {
            public FixAllTest(string source, string fixedSource, string equivalenceKey)
            {
                TestCode = source;
                FixedCode = fixedSource;
                CodeActionEquivalenceKey = equivalenceKey;
                ReferenceAssemblies = TestReferenceAssemblies.Default;
                CompilerDiagnostics = CompilerDiagnostics.Errors;

                TestState.Sources.Add(SharedUnityStub.Source);
                FixedState.Sources.Add(SharedUnityStub.Source);
            }

            public override string Language => LanguageNames.CSharp;

            protected override string DefaultFileExt => "cs";

            public override Type SyntaxKindType => typeof(SyntaxKind);

            protected override CompilationOptions CreateCompilationOptions() =>
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            protected override ParseOptions CreateParseOptions() =>
                new CSharpParseOptions(LanguageVersion.Latest);

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                yield return new TAnalyzer();
            }

            protected override IEnumerable<CodeFixProvider> GetCodeFixProviders()
            {
                yield return new TProvider();
            }
        }
    }
}
