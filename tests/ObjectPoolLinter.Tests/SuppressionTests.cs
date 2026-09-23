using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F25: ObjectPoolSuppressionAnalyzer, which suppresses OPL001, OPL002, OPL003 and OPL008 where
    // the allocation is known not to run every frame.
    public class SuppressionTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }

    public class MonoBehaviour : Object { }

    public static class Time
    {
        public static int frameCount { get { return 0; } }
    }
}
";

        private static DiagnosticResult Allocation(string allocation, string method = "Update", int location = 0) =>
            new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(allocation, method);

        private static DiagnosticResult Hidden(string allocation, int location = 0) =>
            new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(allocation, "Update");

        // A suppressed diagnostic is still reported, carrying the suppression: that is what an IDE
        // shows greyed out and what the build leaves out of its warning count.
        private static DiagnosticResult Suppressed(DiagnosticResult diagnostic) => diagnostic.WithIsSuppressed(true);

        private static SuppressionTest<TAnalyzer> CreateTest<TAnalyzer>(string source, params DiagnosticResult[] expected)
            where TAnalyzer : DiagnosticAnalyzer, new() =>
            new(source, UnityStub, expected);

        // --- The first-frame guard ---

        [Fact]
        public async Task FirstFrameGuard_SuppressesAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        if (Time.frameCount == 0)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Suppressed(Allocation("new List<int>"))).RunAsync();
        }

        [Fact]
        public async Task FirstFrameGuard_InConjunction_SuppressesAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public bool enabledHere;

    void Update()
    {
        if (enabledHere && 0 == Time.frameCount)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Suppressed(Allocation("new List<int>"))).RunAsync();
        }

        [Fact]
        public async Task FirstFrameGuard_ElseBranch_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        if (Time.frameCount == 0)
        {
        }
        else
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        [Fact]
        public async Task FrameCountComparedToAnotherValue_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        if (Time.frameCount == 60)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        // --- #if UNITY_EDITOR ---

        [Fact]
        public async Task EditorOnlyRegion_SuppressesAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
#if UNITY_EDITOR
        var list = {|#0:new System.Collections.Generic.List<int>()|};
#endif
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Suppressed(Allocation("new List<int>"))).WithEditorSymbol().RunAsync();
        }

        [Fact]
        public async Task EditorOnlyRegion_ElseBranch_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
#if UNITY_EDITOR
#else
        var list = {|#0:new System.Collections.Generic.List<int>()|};
#endif
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        [Fact]
        public async Task NegatedEditorSymbol_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
#if !UNITY_EDITOR
        var list = {|#0:new System.Collections.Generic.List<int>()|};
#endif
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        // --- The static boolean latch ---

        [Fact]
        public async Task StaticLatch_SuppressesAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private static bool s_spawned;

    void Update()
    {
        if (!s_spawned)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
            s_spawned = true;
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Suppressed(Allocation("new List<int>"))).RunAsync();
        }

        [Fact]
        public async Task StaticLatch_WithoutAssignment_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private static bool s_spawned;

    void Update()
    {
        if (!s_spawned)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        [Fact]
        public async Task InstanceLatch_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private bool spawned;

    void Update()
    {
        if (!spawned)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
            spawned = true;
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        // --- The result assigned to a field ---

        [Fact]
        public async Task AssignedToField_SuppressesAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private System.Collections.Generic.List<int> _buffer;

    void Update()
    {
        _buffer = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Suppressed(Allocation("new List<int>"))).RunAsync();
        }

        [Fact]
        public async Task CoalesceAssignedToField_SuppressesAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private System.Collections.Generic.List<int> _buffer;

    void Update()
    {
        _buffer = _buffer ?? {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Suppressed(Allocation("new List<int>"))).RunAsync();
        }

        [Fact]
        public async Task AssignedToLocal_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>")).RunAsync();
        }

        [Fact]
        public async Task AssignedToField_SuppressesHiddenAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private string _label;
    public string first;
    public string second;

    void Update()
    {
        _label = {|#0:first + second|};
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(source, Suppressed(Hidden("string concatenation"))).RunAsync();
        }

        [Fact]
        public async Task HiddenAllocationInLocal_IsNotSuppressed()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public string first;
    public string second;

    void Update()
    {
        var label = {|#0:first + second|};
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(source, Hidden("string concatenation")).RunAsync();
        }

        // OPL008 asks for exactly this: load the asset once and keep it in a field.
        [Fact]
        public async Task AssetLoadCachedInField_IsSuppressed()
        {
            var source = @"
using UnityEngine;

namespace UnityEngine
{
    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
    }
}

public class MyBehaviour : MonoBehaviour
{
    private Object _prefab;

    void Update()
    {
        if (_prefab == null)
            _prefab = {|#0:Resources.Load<Object>(""Enemy"")|};

        var uncached = {|#1:Resources.Load<Object>(""Enemy"")|};
    }
}
";

            DiagnosticResult Load(int location) =>
                new DiagnosticResult(AssetLoadAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                    .WithLocation(location)
                    .WithArguments("Resources.Load", "Update", "Load it once in Awake or Start and keep it in a field");

            await CreateTest<AssetLoadAnalyzer>(source, Suppressed(Load(0)), Load(1)).RunAsync();
        }

        // --- object_pool_linter.suppressions ---

        [Fact]
        public async Task SuppressionsNone_ReportsEveryPattern()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        if (Time.frameCount == 0)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, Allocation("new List<int>"))
                .WithEditorConfig("object_pool_linter.suppressions = none")
                .RunAsync();
        }

        [Fact]
        public async Task SuppressionsSubset_KeepsTheOtherPatternsReported()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    private System.Collections.Generic.List<int> _buffer;

    void Update()
    {
        if (Time.frameCount == 0)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }

        _buffer = {|#1:new System.Collections.Generic.List<int>()|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(
                    source,
                    Allocation("new List<int>"),
                    Suppressed(Allocation("new List<int>", location: 1)))
                .WithEditorConfig("object_pool_linter.suppressions = cached_field")
                .RunAsync();
        }

        // Runs one rule together with the suppressor, which is what an IDE or a build does.
        private sealed class SuppressionTest<TAnalyzer> : AnalyzerTest<DefaultVerifier>
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            private readonly List<string> _preprocessorSymbols = new();

            public SuppressionTest(string source, string unityStub, params DiagnosticResult[] expected)
            {
                TestCode = source;
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
                TestState.Sources.Add(unityStub);
                ExpectedDiagnostics.AddRange(expected);
            }

            public override string Language => LanguageNames.CSharp;

            protected override string DefaultFileExt => "cs";

            protected override CompilationOptions CreateCompilationOptions() =>
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            protected override ParseOptions CreateParseOptions() =>
                new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(_preprocessorSymbols);

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                yield return new TAnalyzer();
                yield return new ObjectPoolSuppressionAnalyzer();
            }

            public SuppressionTest<TAnalyzer> WithEditorConfig(string options)
            {
                TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n\n[*]\n" + options + "\n"));
                return this;
            }

            public SuppressionTest<TAnalyzer> WithEditorSymbol()
            {
                _preprocessorSymbols.Add("UNITY_EDITOR");
                return this;
            }
        }
    }
}
