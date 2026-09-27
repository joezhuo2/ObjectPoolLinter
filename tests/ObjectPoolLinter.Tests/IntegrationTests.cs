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
    // .editorconfig sections that apply to some files only. T20-T24: delegate, params and LINQ edge
    // cases, run against all three rules so that a case one rule skips is not reported by another.
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
                ReferenceAssemblies = TestReferenceAssemblies.Default;
                ExpectedDiagnostics.AddRange(expected);
            }

            public override string Language => LanguageNames.CSharp;

            protected override string DefaultFileExt => "cs";

            protected override CompilationOptions CreateCompilationOptions() =>
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.Latest;

            protected override ParseOptions CreateParseOptions() =>
                new CSharpParseOptions(LanguageVersion);

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

        // --- T20: `new` of a delegate type ---

        // `new Action(...)` is an object creation whatever it wraps. OPL001 reports it under the delegate
        // type's name; OPL002 leaves it alone, so the same allocation is not reported twice, even when
        // the lambda inside captures.
        [Theory]
        [InlineData("new Action(() => {})", "new Action")]
        [InlineData("new Action(delegate { })", "new Action")]
        [InlineData("new Action(Spawn)", "new Action")]
        [InlineData("new Action(Tick)", "new Action")]
        [InlineData("new Func<int>(() => wave)", "new Func<int>")]
        [InlineData("new Func<int, int>(x => x * 2)", "new Func<int, int>")]
        public async Task NewDelegate_ReportsAsObjectCreationOnly(string creation, string allocation)
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int wave;

    void Spawn() { }
    static void Tick() { }

    void Update()
    {
        var callback = {|#0:" + creation + @"|};
    }
}
";

            await new AllRulesTest(Allocation(allocation, "Update", 0))
                .WithSources(("/Spawner.cs", source))
                .RunAsync();
        }

        // --- T21: lambdas that capture nothing ---

        // The compiler caches a lambda that captures nothing in a static field, so it allocates once.
        // Reading a constant, a static field or calling a static method is not a capture, and neither is
        // a non-capturing lambda nested inside another one.
        [Theory]
        [InlineData("Func<int> f = () => 42;")]
        [InlineData("Func<int> f = static () => 42;")]
        [InlineData("Func<int, int> f = x => x * 2;")]
        [InlineData("Func<int> f = () => Limit;")]
        [InlineData("Func<int> f = () => shared;")]
        [InlineData("Func<int> f = () => Math.Max(1, 2);")]
        [InlineData("Func<int> f = () => { Func<int> inner = () => 1; return inner(); };")]
        [InlineData("Action f = delegate { };")]
        [InlineData("Run(() => 42);")]
        public async Task NonCapturingLambda_NoRuleReports(string statement)
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    const int Limit = 10;
    static int shared;

    static void Run(Func<int> f) { }

    void Update()
    {
        " + statement + @"
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Spawner.cs", source))
                .RunAsync();
        }

        // --- T22: static method groups on a class that is not a MonoBehaviour ---

        private const string HelperSource = @"
public static class Helper
{
    public static void Spawn() { }
    public static int Twice(int x) => x * 2;
}

public class Pool
{
    public static void Warm() { }
}
";

        // From C# 11 the compiler caches the delegate for a static method group wherever the method is
        // declared: here on a static class and on a plain class, in another file.
        [Theory]
        [InlineData("Action f = Helper.Spawn;", LanguageVersion.CSharp11)]
        [InlineData("Action f = Helper.Spawn;", LanguageVersion.Latest)]
        [InlineData("Func<int, int> f = Helper.Twice;", LanguageVersion.CSharp11)]
        [InlineData("Action f = Pool.Warm;", LanguageVersion.CSharp11)]
        [InlineData("Run(Helper.Spawn);", LanguageVersion.CSharp11)]
        public async Task StaticMethodGroupOnPlainClass_UnderCSharp11_NoRuleReports(string statement, LanguageVersion languageVersion)
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    static void Run(Action f) { }

    void Update()
    {
        " + statement + @"
    }
}
";

            await new AllRulesTest { LanguageVersion = languageVersion }
                .WithSources(("/Helper.cs", HelperSource), ("/Spawner.cs", source))
                .RunAsync();
        }

        // C# 10 is the last version that allocates a new delegate for the same method group.
        [Fact]
        public async Task StaticMethodGroupOnPlainClass_UnderCSharp10_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        Action f = {|#0:Helper.Spawn|};
    }
}
";

            await new AllRulesTest(Hidden("delegate for Spawn()", "Update", 0)) { LanguageVersion = LanguageVersion.CSharp10 }
                .WithSources(("/Helper.cs", HelperSource), ("/Spawner.cs", source))
                .RunAsync();
        }

        // --- T23: a params parameter given no arguments ---

        // An empty expansion passes Array.Empty<T>(), which allocates nothing. So does passing
        // Array.Empty<T>() yourself.
        [Theory]
        [InlineData("Log(\"ready\");")]
        [InlineData("Helper.Show();")]
        [InlineData("Pick<int>();")]
        [InlineData("logger.Write(\"ready\");")]
        [InlineData("Helper.Show(Array.Empty<string>());")]
        public async Task ParamsWithNoArguments_NoRuleReports(string statement)
        {
            var source = @"
using System;
using UnityEngine;

public static class Helper
{
    public static void Show(params string[] names) { }
}

public class Logger
{
    public void Write(string format, params object[] args) { }
}

public class Hud : MonoBehaviour
{
    readonly Logger logger = new Logger();

    static void Log(string format, params object[] args) { }
    static T Pick<T>(params T[] items) => default;

    void Update()
    {
        " + statement + @"
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Hud.cs", source))
                .RunAsync();
        }

        // --- T24: Enumerable.Empty<T>() ---

        // Enumerable.Empty<T>() returns a cached instance. It is not reported on its own, and a LINQ
        // call on top of it is reported without it in the chain.
        [Theory]
        [InlineData("var none = Enumerable.Empty<int>();")]
        [InlineData("IEnumerable<string> none = Enumerable.Empty<string>();")]
        [InlineData("Consume(Enumerable.Empty<int>());")]
        [InlineData("var none = System.Linq.Enumerable.Empty<Squad>();")]
        public async Task EnumerableEmpty_NoRuleReports(string statement)
        {
            var source = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    static void Consume(IEnumerable<int> items) { }

    void Update()
    {
        " + statement + @"
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Squad.cs", source))
                .RunAsync();
        }

        [Fact]
        public async Task EnumerableEmpty_UnderStaticUsing_NoRuleReports()
        {
            var source = @"
using static System.Linq.Enumerable;
using UnityEngine;

public class Squad : MonoBehaviour
{
    void Update()
    {
        var none = Empty<int>();
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Squad.cs", source))
                .RunAsync();
        }

        [Fact]
        public async Task EnumerableEmpty_LinqCallOnTop_ReportsWithoutEmptyInTheChain()
        {
            var source = @"
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    void Update()
    {
        var alive = {|#0:Enumerable.Empty<int>().Where(h => h > 0).ToList()|};
    }
}
";

            await new AllRulesTest(Hidden("LINQ Where().ToList()", "Update", 0))
                .WithSources(("/Squad.cs", source))
                .RunAsync();
        }
    }
}
