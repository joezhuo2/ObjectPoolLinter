using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // The .editorconfig options added in 1.5.2: parameter lists in additional_hot_methods,
    // excluded_types_regex, the per-kind OPL002 severities, and OPL004 for options that do nothing.
    public class ConfigurationTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public struct Vector3 { }
    public class MonoBehaviour : Object { }
}
";

        private static HotPathAnalyzerTest<TAnalyzer> CreateTest<TAnalyzer>(string source, string editorConfig, params DiagnosticResult[] expected)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            return new HotPathAnalyzerTest<TAnalyzer>(source, UnityStub, expected).WithEditorConfig(editorConfig);
        }

        private static DiagnosticResult Allocation(string allocation, string method, int location = 0)
        {
            return new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(allocation, method);
        }

        private static DiagnosticResult Hidden(string allocation, DiagnosticSeverity severity, int location)
        {
            return new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, severity)
                .WithLocation(location)
                .WithArguments(allocation, "Update");
        }

        private static DiagnosticResult InvalidOption(string message)
        {
            return new DiagnosticResult(OptionsValidationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithArguments(message);
        }

        // --- Parameter lists in additional_hot_methods ---

        private const string TickOverloads = @"
public class Simulation
{
    public void Tick(float deltaTime)
    {
        var a = {|#0:new System.Collections.Generic.List<int>()|};
    }

    public void Tick(int frames)
    {
        var b = new System.Collections.Generic.List<int>();
    }

    public void Tick()
    {
        var c = new System.Collections.Generic.List<int>();
    }
}
";

        [Theory]
        [InlineData("Tick(float)")]
        [InlineData("Tick( float )")]
        [InlineData("Tick(Single)")]
        [InlineData("Tick(System.Single)")]
        [InlineData("Simulation.Tick(float)")]
        public async Task HotMethodWithParameters_MatchesOnlyThatOverload(string entry)
        {
            await CreateTest<ObjectPoolAnalyzer>(
                    TickOverloads,
                    "object_pool_linter.additional_hot_methods = " + entry,
                    Allocation("new List<int>", "Tick"))
                .RunAsync();
        }

        [Fact]
        public async Task HotMethodWithEmptyParameterList_MatchesOnlyParameterlessOverload()
        {
            var source = @"
public class EnemyBrain
{
    public void Think()
    {
        var a = {|#0:new System.Collections.Generic.List<int>()|};
    }

    public void Think(int depth)
    {
        var b = new System.Collections.Generic.List<int>();
    }
}

public class AllyBrain
{
    public void Think()
    {
        var c = new System.Collections.Generic.List<int>();
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(
                    source,
                    "object_pool_linter.additional_hot_methods = EnemyBrain.Think()",
                    Allocation("new List<int>", "Think"))
                .RunAsync();
        }

        [Fact]
        public async Task HotMethodWithParameters_GenericsRefAndMixedEntries()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Mover
{
    public void Apply(Dictionary<int, string> map, float deltaTime)
    {
        var a = {|#0:new List<int>()|};
    }

    public void Step(ref Vector3 position)
    {
        var b = {|#1:new List<int>()|};
    }

    public void Step(Vector3 position)
    {
        var c = new List<int>();
    }

    public void Simulate()
    {
        var d = {|#2:new List<int>()|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(
                    source,
                    "object_pool_linter.additional_hot_methods = Apply(Dictionary<int, string>, float), Step(ref UnityEngine.Vector3), Simulate",
                    Allocation("new List<int>", "Apply", 0),
                    Allocation("new List<int>", "Step", 1),
                    Allocation("new List<int>", "Simulate", 2))
                .RunAsync();
        }

        // --- excluded_types_regex ---

        private const string NamespacedHuds = @"
using UnityEngine;

namespace Game.Debug
{
    public class Overlay : MonoBehaviour
    {
        void Update()
        {
            var a = new System.Collections.Generic.List<int>();
        }

        public class Panel : MonoBehaviour
        {
            void Update()
            {
                var b = new System.Collections.Generic.List<int>();
            }
        }
    }
}

namespace Game
{
    public class Hud : MonoBehaviour
    {
        void Update()
        {
            var c = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

        [Theory]
        [InlineData(@"^Game\.Debug\.")]
        [InlineData(@"\.Debug\.|Editor$")]
        public async Task ExcludedTypesRegex_ExcludesANamespace(string pattern)
        {
            await CreateTest<ObjectPoolAnalyzer>(
                    NamespacedHuds,
                    "object_pool_linter.excluded_types_regex = " + pattern,
                    Allocation("new List<int>", "Update"))
                .RunAsync();
        }

        [Fact]
        public async Task ExcludedTypesRegex_IsCaseSensitive()
        {
            var source = @"
using UnityEngine;

namespace Game.Debug
{
    public class Overlay : MonoBehaviour
    {
        void Update()
        {
            var a = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(
                    source,
                    @"object_pool_linter.excluded_types_regex = ^game\.debug\.",
                    Allocation("new List<int>", "Update"))
                .RunAsync();
        }

        [Fact]
        public async Task ExcludedTypesRegex_Invalid_ExcludesNothing()
        {
            var source = @"
using UnityEngine;

public class Overlay : MonoBehaviour
{
    void Update()
    {
        var a = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(
                    source,
                    "object_pool_linter.excluded_types_regex = Overlay(",
                    Allocation("new List<int>", "Update"))
                .RunAsync();
        }

        // --- Per-kind OPL002 severity ---

        private const string MixedAllocations = @"
using System.Linq;
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp;
    int[] scores = new int[4];

    void Update()
    {
        var label = {|#0:$""hp {hp}""|};
        var best = {|#1:scores.Where(s => s > 0).ToArray()|};
    }
}
";

        [Fact]
        public async Task KindSeverity_RaisesOneKindAndLeavesTheRest()
        {
            await CreateTest<HiddenAllocationAnalyzer>(
                    MixedAllocations,
                    "object_pool_linter.linq_severity = warning",
                    Hidden("string interpolation", DiagnosticSeverity.Info, 0),
                    Hidden("LINQ Where().ToArray()", DiagnosticSeverity.Warning, 1))
                .RunAsync();
        }

        [Fact]
        public async Task KindSeverity_NoneSilencesOnlyThatKind()
        {
            await CreateTest<HiddenAllocationAnalyzer>(
                    MixedAllocations,
                    "object_pool_linter.linq_severity = none\nobject_pool_linter.string_severity = error",
                    Hidden("string interpolation", DiagnosticSeverity.Error, 0))
                .RunAsync();
        }

        [Fact]
        public async Task KindSeverity_NoneStillAppliesUnderRuleWideSeverity()
        {
            await CreateTest<HiddenAllocationAnalyzer>(
                    MixedAllocations,
                    "dotnet_diagnostic.OPL002.severity = warning\nobject_pool_linter.linq_severity = none",
                    Hidden("string interpolation", DiagnosticSeverity.Warning, 0))
                .RunAsync();
        }

        [Fact]
        public async Task KindSeverity_RuleWideSeverityWinsOverKindSeverity()
        {
            await CreateTest<HiddenAllocationAnalyzer>(
                    MixedAllocations,
                    "dotnet_diagnostic.OPL002.severity = warning\nobject_pool_linter.string_severity = suggestion",
                    Hidden("string interpolation", DiagnosticSeverity.Warning, 0),
                    Hidden("LINQ Where().ToArray()", DiagnosticSeverity.Warning, 1))
                .RunAsync();
        }

        [Fact]
        public async Task KindSeverity_DelegateParamsAndBoxing()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int count;

    void Run(Func<int> f) { }
    void Log(string format, params object[] args) { }

    void Update()
    {
        Run({|#0:() => count|});
        {|#1:Log(""{0}"", ""a"")|};
        object boxed = {|#2:count|};
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(
                    source,
                    "object_pool_linter.delegate_severity = warning\nobject_pool_linter.params_severity = silent\nobject_pool_linter.boxing_severity = error",
                    Hidden("lambda capturing this", DiagnosticSeverity.Warning, 0),
                    Hidden("params object[] for Log()", DiagnosticSeverity.Hidden, 1),
                    Hidden("boxing int to object", DiagnosticSeverity.Error, 2))
                .RunAsync();
        }

        [Fact]
        public async Task KindSeverity_IteratorAndAsync()
        {
            var source = @"
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    IEnumerator Spawn() { yield return null; }
    async Task SaveAsync() => await Task.Yield();

    void Update()
    {
        var routine = {|#0:Spawn()|};
        _ = {|#1:SaveAsync()|};
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(
                    source,
                    "object_pool_linter.iterator_severity = warning\nobject_pool_linter.async_severity = error",
                    Hidden("iterator state machine for Spawn()", DiagnosticSeverity.Warning, 0),
                    Hidden("async state machine for SaveAsync()", DiagnosticSeverity.Error, 1))
                .RunAsync();

            await CreateTest<HiddenAllocationAnalyzer>(
                    source.Replace("{|#0:", "").Replace("{|#1:", "").Replace("()|}", "()"),
                    "object_pool_linter.iterator_severity = none\nobject_pool_linter.async_severity = none")
                .RunAsync();
        }

        [Fact]
        public async Task KindSeverity_Enumerator()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Squad : MonoBehaviour
{
    IList<int> hp = new List<int>();

    void Update()
    {
        {|#0:foreach (var h in hp)|} { }
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(
                    source,
                    "object_pool_linter.enumerator_severity = warning",
                    Hidden("enumerator for foreach over IList<int>", DiagnosticSeverity.Warning, 0))
                .RunAsync();

            await CreateTest<HiddenAllocationAnalyzer>(
                    source.Replace("{|#0:", "").Replace(")|}", ")"),
                    "object_pool_linter.enumerator_severity = none")
                .RunAsync();
        }

        // --- OPL004 ---

        private const string AnySource = @"
public class Anything { }
";

        [Fact]
        public void OptionsValidation_DescriptorIsWarningWithHelpLink()
        {
            var descriptor = Assert.Single(new OptionsValidationAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL004", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL004.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task OptionsValidation_ValidOptions_NoDiagnostic()
        {
            await CreateTest<OptionsValidationAnalyzer>(
                    AnySource,
                    "object_pool_linter.additional_hot_methods = Tick(float), EnemyBrain.Think\n" +
                    "object_pool_linter.excluded_types = LoadingScreen\n" +
                    @"object_pool_linter.excluded_types_regex = ^Game\.Debug\." + "\n" +
                    "object_pool_linter.linq_severity = warning\n" +
                    "object_pool_linter.string_severity = default\n" +
                    "object_pool_linter.iterator_severity = warning\n" +
                    "object_pool_linter.async_severity = none\n" +
                    "object_pool_linter.enumerator_severity = suggestion\n" +
                    "dotnet_diagnostic.OPL002.severity = warning\n" +
                    "indent_style = space")
                .RunAsync();
        }

        [Fact]
        public async Task OptionsValidation_Typo_SuggestsTheKnownOption()
        {
            await CreateTest<OptionsValidationAnalyzer>(
                    AnySource,
                    "object_pool_linter.exlude_types = LoadingScreen",
                    InvalidOption("'object_pool_linter.exlude_types' is not a recognized option. Did you mean 'object_pool_linter.excluded_types'?"))
                .RunAsync();
        }

        [Fact]
        public async Task OptionsValidation_UnknownOption_NoSuggestion()
        {
            await CreateTest<OptionsValidationAnalyzer>(
                    AnySource,
                    "object_pool_linter.pool_everything = true",
                    InvalidOption("'object_pool_linter.pool_everything' is not a recognized option."))
                .RunAsync();
        }

        [Fact]
        public async Task OptionsValidation_ReportsEachProblemOnceAcrossFiles()
        {
            var test = CreateTest<OptionsValidationAnalyzer>(
                AnySource,
                "object_pool_linter.linq_severity = loud",
                InvalidOption("'loud' in 'object_pool_linter.linq_severity' is not a severity. Use none, silent, suggestion, warning, error or default."));
            test.TestState.Sources.Add("public class Other { }");

            await test.RunAsync();
        }

        [Fact]
        public async Task OptionsValidation_UnknownSuppression()
        {
            await CreateTest<OptionsValidationAnalyzer>(
                    AnySource,
                    "object_pool_linter.suppressions = cached_fields",
                    InvalidOption("'cached_fields' in 'object_pool_linter.suppressions' is not a suppression. Use all, none, first_frame, editor_only, static_latch or cached_field."))
                .RunAsync();
        }

        [Fact]
        public async Task OptionsValidation_InvalidValues()
        {
            await CreateTest<OptionsValidationAnalyzer>(
                    AnySource,
                    "object_pool_linter.additional_hot_methods = Brain..Think, Think), Tick(float\n" +
                    "object_pool_linter.excluded_types_regex = Overlay(",
                    InvalidOption("'Brain..Think' in 'object_pool_linter.additional_hot_methods' is not a method name, Type.Method or Namespace.Type.Method."),
                    InvalidOption("'Think)' in 'object_pool_linter.additional_hot_methods' has a ')' without a matching '('."),
                    InvalidOption("'Tick(float' in 'object_pool_linter.additional_hot_methods' has a parameter list that does not end with ')'."),
                    InvalidOption("'Overlay(' in 'object_pool_linter.excluded_types_regex' is not a valid regular expression."))
                .RunAsync();
        }
    }
}
