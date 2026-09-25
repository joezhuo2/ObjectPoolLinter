using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // T1-T3: the analyzers running together over one compilation, over several files, and with
    // .editorconfig sections that apply to some files only.
    public class IntegrationTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }

    public struct Ray { }
    public struct RaycastHit { }

    public static class Physics
    {
        public static RaycastHit[] RaycastAll(Ray ray) => null;
    }
}
";

        // Runs OPL001, OPL002 and OPL003 together, the way a build does.
        private sealed class AllRulesTest : AnalyzerTest<DefaultVerifier>
        {
            public AllRulesTest(params DiagnosticResult[] expected)
            {
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
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
            }

            public AllRulesTest WithSources(params (string Path, string Source)[] sources)
            {
                TestState.Sources.Add(("/UnityStub.cs", UnityStub));
                foreach (var source in sources)
                {
                    TestState.Sources.Add(source);
                }

                return this;
            }

            public AllRulesTest WithEditorConfig(string content)
            {
                TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n\n" + content + "\n"));
                return this;
            }
        }

        private static DiagnosticResult Allocation(string allocation, string method, int location) =>
            new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(allocation, method);

        private static DiagnosticResult Hidden(string allocation, string method, int location) =>
            new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(allocation, method);

        private static DiagnosticResult UnityApi(string api, string returned, string method, int location) =>
            new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(api, returned, method);

        // --- T1: all rules over the same source ---

        [Fact]
        public async Task AllRules_NestedAllocations_EachRuleReportsItsOwn()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Ray ray;
    int hp;

    void Update()
    {
        var list = {|#0:new List<RaycastHit>({|#1:Physics.RaycastAll(ray)|})|};
        var label = {|#2:$""hp {hp}""|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new List<RaycastHit>", "Update", 0),
                    UnityApi("Physics.RaycastAll", "RaycastHit[]", "Update", 1),
                    Hidden("string interpolation", "Update", 2))
                .WithSources(("/Scanner.cs", source))
                .RunAsync();
        }

        [Fact]
        public async Task AllRules_ColdMethod_NoRuleReports()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Ray ray;
    int hp;

    void Start()
    {
        var list = new List<RaycastHit>(Physics.RaycastAll(ray));
        var label = $""hp {hp}"";
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Scanner.cs", source))
                .RunAsync();
        }

        [Fact]
        public void AllRules_DistinctDiagnosticIds()
        {
            var ids = new DiagnosticAnalyzer[]
                {
                    new ObjectPoolAnalyzer(),
                    new HiddenAllocationAnalyzer(),
                    new UnityApiAllocationAnalyzer(),
                }
                .SelectMany(a => a.SupportedDiagnostics)
                .Select(d => d.Id)
                .ToImmutableArray();

            Assert.Equal(ids.Length, ids.Distinct().Count());
        }

        // --- T2: several files in one compilation ---

        [Fact]
        public async Task MultiFile_PartialMonoBehaviour_AllocationInOtherFile()
        {
            var declaration = @"
using UnityEngine;

public partial class Player : MonoBehaviour { }
";

            var behaviour = @"
using System.Collections.Generic;
using UnityEngine;

public partial class Player
{
    Ray ray;

    void Update()
    {
        var list = {|#0:new List<int>()|};
        var hits = {|#1:Physics.RaycastAll(ray)|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new List<int>", "Update", 0),
                    UnityApi("Physics.RaycastAll", "RaycastHit[]", "Update", 1))
                .WithSources(("/Player.cs", declaration), ("/Player.Update.cs", behaviour))
                .RunAsync();
        }

        [Fact]
        public async Task MultiFile_BaseClassInOtherFile()
        {
            var baseClass = @"
using UnityEngine;

public abstract class Unit : MonoBehaviour { }
";

            var derived = @"
using System.Collections.Generic;

public class Enemy : Unit
{
    void LateUpdate()
    {
        var list = {|#0:new List<int>()|};
    }
}

public class Plain
{
    void Update()
    {
        var list = new List<int>();
    }
}
";

            await new AllRulesTest(Allocation("new List<int>", "LateUpdate", 0))
                .WithSources(("/Unit.cs", baseClass), ("/Enemy.cs", derived))
                .RunAsync();
        }

        [Fact]
        public async Task MultiFile_SharedOptionsApplyToEveryFile()
        {
            var fileA = @"
using System.Collections.Generic;

public class Simulation
{
    public void Tick()
    {
        var list = {|#0:new List<int>()|};
    }
}
";

            var fileB = @"
using System.Collections.Generic;

public class Physics2
{
    public void Tick()
    {
        var list = {|#1:new List<int>()|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new List<int>", "Tick", 0),
                    Allocation("new List<int>", "Tick", 1))
                .WithSources(("/A.cs", fileA), ("/B.cs", fileB))
                .WithEditorConfig("[*.cs]\nobject_pool_linter.additional_hot_methods = Tick")
                .RunAsync();
        }

        // --- T3: .editorconfig sections that apply to one file ---

        private const string TickA = @"
using System.Collections.Generic;

public class Simulation
{
    public void Tick()
    {
        var list = {|#0:new List<int>()|};
    }
}
";

        private const string TickB = @"
using System.Collections.Generic;

public class Replay
{
    public void Tick()
    {
        var list = new List<int>();
    }
}
";

        [Fact]
        public async Task PerFileOptions_HotMethodOnlyInConfiguredFile()
        {
            await new AllRulesTest(Allocation("new List<int>", "Tick", 0))
                .WithSources(("/A.cs", TickA), ("/B.cs", TickB))
                .WithEditorConfig("[A.cs]\nobject_pool_linter.additional_hot_methods = Tick")
                .RunAsync();
        }

        [Fact]
        public async Task PerFileOptions_DifferentHotMethodsPerFile()
        {
            var fileB = @"
using System.Collections.Generic;

public class Replay
{
    public void Tick()
    {
        var a = new List<int>();
    }

    public void Step()
    {
        var b = {|#1:new List<int>()|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new List<int>", "Tick", 0),
                    Allocation("new List<int>", "Step", 1))
                .WithSources(("/A.cs", TickA), ("/B.cs", fileB))
                .WithEditorConfig(
                    "[A.cs]\nobject_pool_linter.additional_hot_methods = Tick\n\n" +
                    "[B.cs]\nobject_pool_linter.additional_hot_methods = Step")
                .RunAsync();
        }

        [Fact]
        public async Task PerFileOptions_ExcludedTypesOnlyInConfiguredFile()
        {
            var fileA = @"
using System.Collections.Generic;
using UnityEngine;

public class Hud : MonoBehaviour
{
    void Update()
    {
        var list = new List<int>();
    }
}
";

            var fileB = @"
using System.Collections.Generic;
using UnityEngine;

public class Hud2 : MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new List<int>()|};
    }
}
";

            await new AllRulesTest(Allocation("new List<int>", "Update", 0))
                .WithSources(("/A.cs", fileA), ("/B.cs", fileB))
                .WithEditorConfig("[A.cs]\nobject_pool_linter.excluded_types_regex = ^Hud")
                .RunAsync();
        }
    }
}
