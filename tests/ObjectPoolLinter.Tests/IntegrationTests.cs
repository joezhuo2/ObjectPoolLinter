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
    // cases, T25-T26: compound assignments and interpolated strings that allocate nothing, and T30-T35:
    // lambda captures, several chains in one method, SelectMany and GroupBy, and boxing through a
    // static method's argument. T36-T40: OPL001 edge cases, namely initializers, multi-dimensional arrays,
    // Instantiate with a position and rotation, several allocations in one statement, and arrays
    // allocated outside a MonoBehaviour. T41-T45: a hot method named with a deep namespace, several
    // excluded types in one entry, generated code, OPL003 in sub-namespaces of UnityEngine, and OPL003
    // through a derived-type reference. T46: allocating and buffer-filling overloads of the same Unity
    // API in one Update. All run against all three rules so that a case one rule skips is not reported
    // by another.
    public class IntegrationTests
    {
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
                TestState.Sources.Add(("/UnityStub.cs", SharedUnityStub.Source));
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

        // --- T25: compound assignment on a type that is not string ---

        // Only `+=` on a string builds a new string. Arithmetic and bitwise compound assignments on
        // numbers, enums, nullable numbers, list, array and dictionary elements, and a struct with a
        // user-defined operator allocate nothing, and no widening conversion in them boxes.
        [Theory]
        [InlineData("count += 1;")]
        [InlineData("count -= 1;")]
        [InlineData("count *= 2;")]
        [InlineData("count |= 4;")]
        [InlineData("count <<= 1;")]
        [InlineData("count += 'a';")]
        [InlineData("speed += 0.5f;")]
        [InlineData("ticks += count;")]
        [InlineData("maybe += 1;")]
        [InlineData("layers |= Layers.Enemy;")]
        [InlineData("counts[0] += 1;")]
        [InlineData("scores[0] += count;")]
        [InlineData("hits[\"head\"] += 1;")]
        [InlineData("wallet += wallet;")]
        public async Task CompoundAssignmentOnNonString_NoRuleReports(string statement)
        {
            var source = @"
using System;
using System.Collections.Generic;
using UnityEngine;

[Flags]
public enum Layers { None = 0, Player = 1, Enemy = 2 }

public struct Money
{
    public int Coins;

    public static Money operator +(Money a, Money b)
    {
        a.Coins += b.Coins;
        return a;
    }
}

public class Tally : MonoBehaviour
{
    int count;
    float speed;
    long ticks;
    int? maybe;
    Layers layers;
    Money wallet;
    readonly List<int> counts = new List<int>();
    readonly int[] scores = new int[4];
    readonly Dictionary<string, int> hits = new Dictionary<string, int>();

    void Update()
    {
        " + statement + @"
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Tally.cs", source))
                .RunAsync();
        }

        // The string case next to the others, so the test above cannot pass because OPL002 never
        // looks at compound assignments at all.
        [Fact]
        public async Task CompoundAssignmentOnString_ReportsNextToNumericOnes()
        {
            var source = @"
using UnityEngine;

public class Tally : MonoBehaviour
{
    int count;
    string log = """";

    void Update()
    {
        count += 1;
        {|#0:log += ""tick""|};
        count *= 2;
    }
}
";

            await new AllRulesTest(Hidden("string concatenation", "Update", 0))
                .WithSources(("/Tally.cs", source))
                .RunAsync();
        }

        // --- T26: interpolated strings with no holes ---

        // `$"text"` with no holes, or whose holes are all constant strings, is compiled to a literal.
        // The compiler gives a string with no holes a constant value under C# 9 as well, and OPL002
        // skips it either for that constant value or because it has no interpolation parts, so both
        // checks have to go before these cases report. Holes of constant strings need C# 10, where
        // only the constant value check skips them.
        [Theory]
        [InlineData("var plain = $\"no holes\";", LanguageVersion.CSharp9)]
        [InlineData("var plain = $\"no holes\";", LanguageVersion.Latest)]
        [InlineData("var path = $@\"C:\\temp\";", LanguageVersion.CSharp9)]
        [InlineData("var path = $@\"C:\\temp\";", LanguageVersion.Latest)]
        [InlineData("var braces = $\"{{escaped}}\";", LanguageVersion.CSharp9)]
        [InlineData("var braces = $\"{{escaped}}\";", LanguageVersion.Latest)]
        [InlineData("Show($\"ready\");", LanguageVersion.CSharp9)]
        [InlineData("Show($\"ready\");", LanguageVersion.Latest)]
        [InlineData("var raw = $\"\"\"no holes\"\"\";", LanguageVersion.Latest)]
        [InlineData("const string label = $\"{Prefix}-hud\";", LanguageVersion.Latest)]
        [InlineData("var joined = $\"{Prefix}{Suffix}\";", LanguageVersion.Latest)]
        public async Task InterpolationWithNoHoles_NoRuleReports(string statement, LanguageVersion languageVersion)
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    const string Prefix = ""wave"";
    const string Suffix = ""-1"";

    static void Show(string text) { }

    void Update()
    {
        " + statement + @"
    }
}
";

            await new AllRulesTest { LanguageVersion = languageVersion }
                .WithSources(("/Hud.cs", source))
                .RunAsync();
        }

        // A hole holding a value that is not a constant string still builds a string at run time.
        [Theory]
        [InlineData(LanguageVersion.CSharp9)]
        [InlineData(LanguageVersion.Latest)]
        public async Task InterpolationWithNonConstantHole_Reports(LanguageVersion languageVersion)
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    const string Prefix = ""wave"";
    string suffix = ""-1"";

    void Update()
    {
        var plain = $""no holes"";
        var joined = {|#0:$""{Prefix}{suffix}""|};
    }
}
";

            await new AllRulesTest(Hidden("string interpolation", "Update", 0)) { LanguageVersion = languageVersion }
                .WithSources(("/Hud.cs", source))
                .RunAsync();
        }

        // --- T30: a lambda capturing several locals ---

        // Captured names are listed in order of first use, each once, separated by ", ", with `this`
        // for an instance member.
        [Fact]
        public async Task LambdaCapturingSeveralLocals_ListsEachNameOnceInOrderOfFirstUse()
        {
            var source = @"
using System;
using UnityEngine;

public class Squad : MonoBehaviour
{
    int wave;

    void Update()
    {
        var count = 3;
        var hp = 10;
        Func<int> sum = {|#0:() => count + hp|};
        Func<int> reversed = {|#1:() => hp + count|};
        Func<int> repeated = {|#2:() => count + count * hp + count|};
        Func<int> withThis = {|#3:() => count + wave + hp|};
        Func<int, int> withOwnParameter = {|#4:h => h + count + hp|};
    }
}
";

            await new AllRulesTest(
                    Hidden("lambda capturing count, hp", "Update", 0),
                    Hidden("lambda capturing hp, count", "Update", 1),
                    Hidden("lambda capturing count, hp", "Update", 2),
                    Hidden("lambda capturing count, this, hp", "Update", 3),
                    Hidden("lambda capturing count, hp", "Update", 4))
                .WithSources(("/Squad.cs", source))
                .RunAsync();
        }

        // --- T31: a lambda capturing a parameter of the hot method ---

        // Unity passes the layer index to OnAnimatorIK(int), so its parameter is a real hot-path
        // capture; so is the parameter of a method added through additional_hot_methods.
        [Fact]
        public async Task LambdaCapturingHotMethodParameter_ReportsTheParameterName()
        {
            var source = @"
using System;
using UnityEngine;

public class Rig : MonoBehaviour
{
    void OnAnimatorIK(int layerIndex)
    {
        Func<int> next = {|#0:() => layerIndex + 1|};
    }

    void Tick(int x)
    {
        var offset = 2;
        Func<int> f = {|#1:() => x + 1|};
        Func<int> g = {|#2:() => x + offset|};
        Func<int, int> own = y => y + 1;
    }
}
";

            await new AllRulesTest(
                    Hidden("lambda capturing layerIndex", "OnAnimatorIK", 0),
                    Hidden("lambda capturing x", "Tick", 1),
                    Hidden("lambda capturing x, offset", "Tick", 2))
                .WithSources(("/Rig.cs", source))
                .WithEditorConfig("[*.cs]\nobject_pool_linter.additional_hot_methods = Tick")
                .RunAsync();
        }

        // `Update(int)` is not the message Unity calls every frame, so a capture there is not reported.
        [Fact]
        public async Task LambdaCapturingParameterOfUpdateOverload_DoesNotReport()
        {
            var source = @"
using System;
using UnityEngine;

public class Rig : MonoBehaviour
{
    void Update(int x)
    {
        Func<int> f = () => x + 1;
    }
}
";

            await new AllRulesTest()
                .WithSources(("/Rig.cs", source))
                .RunAsync();
        }

        // --- T32: several string concatenations in one method ---

        // Each separate chain is its own site to fix and is reported on its own, including two
        // chains passed as arguments to the same call.
        [Fact]
        public async Task SeveralConcatenationsInOneMethod_EachChainReported()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    string x = ""a"", y = ""b"", z = ""c"", w = ""d"";

    void Update()
    {
        var a = {|#0:x + y|};
        var b = {|#1:z + w|};
        var c = {|#2:x + y + z + w|};
        Show({|#3:a + b|}, {|#4:c + x|});
    }

    static void Show(string first, string second) { }
}
";

            await new AllRulesTest(
                    Hidden("string concatenation", "Update", 0),
                    Hidden("string concatenation", "Update", 1),
                    Hidden("string concatenation", "Update", 2),
                    Hidden("string concatenation", "Update", 3),
                    Hidden("string concatenation", "Update", 4))
                .WithSources(("/Hud.cs", source))
                .RunAsync();
        }

        // --- T33: several LINQ chains in one method ---

        // A sequence passed as a later argument, such as the one given to Concat, is a chain of its own.
        [Fact]
        public async Task SeveralLinqChainsInOneMethod_EachChainReported()
        {
            var source = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    readonly List<int> hp = new List<int>();

    void Update()
    {
        var alive = {|#0:hp.Where(h => h > 0).ToList()|};
        var doubled = {|#1:hp.Select(h => h * 2).ToList()|};
        var both = {|#2:hp.Where(h => h > 0).Concat({|#3:hp.Select(h => h * 2)|}).ToList()|};
    }
}
";

            await new AllRulesTest(
                    Hidden("LINQ Where().ToList()", "Update", 0),
                    Hidden("LINQ Select().ToList()", "Update", 1),
                    Hidden("LINQ Where().Concat().ToList()", "Update", 2),
                    Hidden("LINQ Select()", "Update", 3))
                .WithSources(("/Squad.cs", source))
                .RunAsync();
        }

        // --- T34: SelectMany and GroupBy chains ---

        [Fact]
        public async Task SelectManyAndGroupByChains_DescribeEachLink()
        {
            var source = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    readonly List<List<int>> squads = new List<List<int>>();
    readonly List<int> hp = new List<int>();

    void Update()
    {
        var keys = {|#0:squads.SelectMany(s => s).GroupBy(h => h / 10).Select(g => g.Key).ToList()|};
        var flat = {|#1:hp.GroupBy(h => h % 2).SelectMany(g => g).Distinct().ToArray()|};
        var scaled = {|#2:squads.SelectMany(s => s, (s, h) => h * 2).ToList()|};
        var groups = {|#3:hp.GroupBy(h => h % 2, h => h * 2)|};
    }
}
";

            await new AllRulesTest(
                    Hidden("LINQ SelectMany().GroupBy().Select().ToList()", "Update", 0),
                    Hidden("LINQ GroupBy().SelectMany().Distinct().ToArray()", "Update", 1),
                    Hidden("LINQ SelectMany().ToList()", "Update", 2),
                    Hidden("LINQ GroupBy()", "Update", 3))
                .WithSources(("/Squad.cs", source))
                .RunAsync();
        }

        // --- T35: boxing through a static method's object parameter ---

        // A struct created in the argument is boxed on the spot and reported by OPL001; an existing
        // value is reported by OPL002. A generic parameter takes the struct without boxing.
        [Fact]
        public async Task BoxingViaStaticMethodArgument_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public struct Cell { public int X; }

public static class Log
{
    public static void Write(object value) { }
    public static void Compare(IComparable value) { }
    public static void Keep<T>(T value) { }
}

public class Grid : MonoBehaviour
{
    Cell cell;
    int count;

    void Update()
    {
        Log.Write({|#0:new Cell()|});
        Log.Write({|#1:cell|});
        Log.Write({|#2:count|});
        Log.Compare({|#3:count|});
        Log.Keep(cell);
        Log.Keep(new Cell());
    }
}
";

            await new AllRulesTest(
                    Allocation("new Cell boxed to object", "Update", 0),
                    Hidden("boxing Cell to object", "Update", 1),
                    Hidden("boxing int to object", "Update", 2),
                    Hidden("boxing int to IComparable", "Update", 3))
                .WithSources(("/Grid.cs", source))
                .RunAsync();
        }

        // --- T36: object and collection initializers ---

        // The initializer runs after the constructor, so the allocation still happens. The whole
        // expression, initializer included, is reported under the created type's name, and an object
        // created inside a collection initializer is reported on its own.
        [Fact]
        public async Task CreationWithInitializer_Reports()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy { public int Hp; }

public class Spawner : MonoBehaviour
{
    void Update()
    {
        var ids = {|#0:new List<int> { 1, 2, 3 }|};
        var byName = {|#1:new Dictionary<string, int> { [""a""] = 1 }|};
        var enemy = {|#2:new Enemy { Hp = 10 }|};
        List<int> targetTyped = {|#3:new() { 4, 5 }|};
        var squad = {|#4:new List<Enemy> { {|#5:new Enemy()|} }|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new List<int>", "Update", 0),
                    Allocation("new Dictionary<string, int>", "Update", 1),
                    Allocation("new Enemy", "Update", 2),
                    Allocation("new List<int>", "Update", 3),
                    Allocation("new List<Enemy>", "Update", 4),
                    Allocation("new Enemy", "Update", 5))
                .WithSources(("/Spawner.cs", source))
                .RunAsync();
        }

        // --- T37: multi-dimensional arrays ---

        // A rectangular array is one object on the heap whatever its rank; a jagged array's outer
        // array is reported, and its rows are allocated later by separate `new` expressions.
        [Fact]
        public async Task MultiDimensionalArrayCreation_Reports()
        {
            var source = @"
using UnityEngine;

public class Grid : MonoBehaviour
{
    void Update()
    {
        var grid = {|#0:new int[,] { { 1 }, { 2 } }|};
        var sized = {|#1:new float[2, 3]|};
        var cube = {|#2:new byte[2, 2, 2]|};
        var implicitGrid = {|#3:new[,] { { 1, 2 }, { 3, 4 } }|};
        var jagged = {|#4:new int[2][]|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new int[,]", "Update", 0),
                    Allocation("new float[,]", "Update", 1),
                    Allocation("new byte[,,]", "Update", 2),
                    Allocation("new int[,]", "Update", 3),
                    Allocation("new int[][]", "Update", 4))
                .WithSources(("/Grid.cs", source))
                .RunAsync();
        }

        // --- T38: Instantiate with a position and rotation ---

        // The three-argument overload clones the object like the one-argument one, qualified or not,
        // and the generic overload is resolved for a GameObject. The Vector3 and Quaternion passed in
        // are structs that are not boxed, so only the call is reported. The same calls in Start are not.
        [Fact]
        public async Task InstantiateWithPositionAndRotation_Reports()
        {
            var source = @"
using UnityEngine;

public class Spawner : MonoBehaviour
{
    Object prefab;
    GameObject enemy;
    Vector3 position;
    Quaternion rotation;

    void Start()
    {
        Object.Instantiate(prefab, position, rotation);
        Instantiate(enemy, position, rotation);
    }

    void Update()
    {
        {|#0:Object.Instantiate(prefab, position, rotation)|};
        {|#1:Instantiate(prefab, position, rotation)|};
        GameObject clone = {|#2:Instantiate(enemy, position, rotation)|};
        {|#3:Object.Instantiate(prefab, new Vector3(), new Quaternion())|};
    }
}
";

            await new AllRulesTest(
                    Allocation("Instantiate", "Update", 0),
                    Allocation("Instantiate", "Update", 1),
                    Allocation("Instantiate", "Update", 2),
                    Allocation("Instantiate", "Update", 3))
                .WithSources(("/Spawner.cs", source))
                .RunAsync();
        }

        // --- T39: several allocations in one statement ---

        // Each `new` is its own allocation and its own diagnostic, whether the allocations are sibling
        // arguments, tuple elements, or one nested in another's constructor arguments.
        [Fact]
        public async Task SeveralAllocationsInOneStatement_EachReported()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class A { }
public class B { }

public class Wrapper
{
    public Wrapper(A inner) { }
}

public class Spawner : MonoBehaviour
{
    static void Use(A a, B b) { }
    static void Take(A a, int[] values, List<int> list) { }

    void Update()
    {
        Use({|#0:new A()|}, {|#1:new B()|});
        Take({|#2:new A()|}, {|#3:new int[3]|}, {|#4:new List<int> { 1 }|});
        var pair = ({|#5:new A()|}, {|#6:new B()|});
        var nested = {|#7:new Wrapper({|#8:new A()|})|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new A", "Update", 0),
                    Allocation("new B", "Update", 1),
                    Allocation("new A", "Update", 2),
                    Allocation("new int[]", "Update", 3),
                    Allocation("new List<int>", "Update", 4),
                    Allocation("new A", "Update", 5),
                    Allocation("new B", "Update", 6),
                    Allocation("new Wrapper", "Update", 7),
                    Allocation("new A", "Update", 8))
                .WithSources(("/Spawner.cs", source))
                .RunAsync();
        }

        // --- T40: arrays allocated outside a MonoBehaviour ---

        // A Unity message has to be declared on a class deriving from MonoBehaviour. A struct, a static
        // class, an interface's default implementation (even one a MonoBehaviour inherits, since Unity
        // looks the message up on the class) and a struct nested in a MonoBehaviour are not. The
        // MonoBehaviour's own Update is the control that shows the rule is running.
        [Fact]
        public async Task ArrayAllocationOutsideMonoBehaviour_OnlyTheMonoBehaviourReports()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public struct Mover
{
    void Update() { var buffer = new int[4]; }
    public void FixedUpdate() { var buffer = new[] { 1, 2 }; }
}

public static class Ticker
{
    static void Update() { var buffer = new int[4]; }
    public static void LateUpdate() { var grid = new int[2, 2]; }
}

public interface ITicker
{
    void Update() { var buffer = new int[4]; }
    void LateUpdate() { var list = new List<int>(); }
}

public class Hud : MonoBehaviour, ITicker
{
    struct Cell
    {
        void Update() { var buffer = new int[4]; }
    }

    void Update() { var buffer = {|#0:new int[4]|}; }
}
";

            await new AllRulesTest(Allocation("new int[]", "Update", 0))
                .WithSources(("/Hud.cs", source))
                .RunAsync();
        }

        // --- T41: a hot method named with a deep namespace ---

        // The entry splits at its last dot: `Think` is the method and `Game.AI.Enemy.Brain` the type,
        // matched against the type's namespace-qualified name. Nested types are joined with dots too, so
        // the entry matches `Brain` nested in `Game.AI.Enemy` as well as `Brain` in namespace
        // `Game.AI.Enemy`. A parameter list or a leading `global::` does not change where the name splits.
        [Theory]
        [InlineData("Game.AI.Enemy.Brain.Think", "namespace Game.AI.Enemy { public class Brain { public void Think() { var a = {|#0:new List<int>()|}; } } }")]
        [InlineData("Game.AI.Enemy.Brain.Think", "namespace Game.AI { public class Enemy { public class Brain { public void Think() { var a = {|#0:new List<int>()|}; } } } }")]
        [InlineData("Game.AI.Enemy.Brain.Think()", "namespace Game.AI.Enemy { public class Brain { public void Think() { var a = {|#0:new List<int>()|}; } } }")]
        [InlineData("global::Game.AI.Enemy.Brain.Think", "namespace Game.AI.Enemy { public class Brain { public void Think() { var a = {|#0:new List<int>()|}; } } }")]
        public async Task DeepNamespaceHotMethod_Reports(string entry, string declaration)
        {
            var source = "using System.Collections.Generic;\n" + declaration + "\n";

            await new AllRulesTest(Allocation("new List<int>", "Think", 0))
                .WithSources(("/Brain.cs", source))
                .WithEditorConfig("[*.cs]\nobject_pool_linter.additional_hot_methods = " + entry)
                .RunAsync();
        }

        // Only the exact qualified type matches: a `Brain.Think` in another namespace, one namespace
        // deeper, or in the global namespace is not hot, and neither is another method on the right type.
        [Fact]
        public async Task DeepNamespaceHotMethod_OtherTypesWithTheSameNameDoNotReport()
        {
            var source = @"
using System.Collections.Generic;

namespace Game.AI.Enemy
{
    public class Brain
    {
        public void Think() { var a = {|#0:new List<int>()|}; }
        public void Plan() { var b = new List<int>(); }
    }
}

namespace Other.AI.Enemy
{
    public class Brain
    {
        public void Think() { var a = new List<int>(); }
    }
}

namespace Game.AI.Enemy.Boss
{
    public class Brain
    {
        public void Think() { var a = new List<int>(); }
    }
}

public class Brain
{
    public void Think() { var a = new List<int>(); }
}
";

            await new AllRulesTest(Allocation("new List<int>", "Think", 0))
                .WithSources(("/Brain.cs", source))
                .WithEditorConfig("[*.cs]\nobject_pool_linter.additional_hot_methods = Game.AI.Enemy.Brain.Think")
                .RunAsync();
        }

        // --- T42: several excluded types in one entry ---

        // Every name in the comma-separated list is excluded, whatever the spacing and with a trailing
        // comma, and a qualified name can sit next to a simple one. The exclusion covers all three rules.
        // Radar is not listed and is the control.
        [Theory]
        [InlineData("LoadingScreen, Hud")]
        [InlineData("LoadingScreen,Hud")]
        [InlineData("  LoadingScreen ,  Hud ,")]
        [InlineData("Hud, Game.UI.LoadingScreen")]
        public async Task SeveralExcludedTypesInOneEntry_EachExcluded(string value)
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    public class LoadingScreen : MonoBehaviour
    {
        int percent;

        void Update()
        {
            var list = new List<int>();
            var label = ""Loading "" + percent;
            var title = name;
        }
    }
}

public class Hud : MonoBehaviour
{
    int score;

    void LateUpdate()
    {
        var list = new List<int>();
        var label = ""Score "" + score;
        var title = name;
    }
}

public class Radar : MonoBehaviour
{
    int range;

    void Update()
    {
        var list = {|#0:new List<int>()|};
        var label = {|#1:""Range "" + range|};
        var title = {|#2:name|};
    }
}
";

            await new AllRulesTest(
                    Allocation("new List<int>", "Update", 0),
                    Hidden("string concatenation", "Update", 1),
                    UnityApi("Object.name", "string", "Update", 2))
                .WithSources(("/Screens.cs", source))
                .WithEditorConfig("[*.cs]\nobject_pool_linter.excluded_types = " + value)
                .RunAsync();
        }

        // --- T43: generated code ---

        private const string GeneratedHud = @"
using System.Collections.Generic;
using UnityEngine;

public partial class GeneratedHud : MonoBehaviour
{
    int score;

    void Update()
    {
        var list = new List<int>();
        var label = ""Score "" + score;
        var title = name;
    }
}
";

        private const string HandWrittenHud = @"
using System.Collections.Generic;
using UnityEngine;

public class Hud : MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new List<int>()|};
    }
}
";

        // Every rule opts out of generated code, so none of them reports inside a file Roslyn treats as
        // generated: by file name (`.designer.cs`, `.generated.cs`, `.g.cs`, `.g.i.cs`, in any case),
        // or by an `<auto-generated>` or `<autogenerated>` comment at the top of the file. The
        // hand-written Hud in another file still reports.
        [Theory]
        [InlineData("/GeneratedHud.Designer.cs", "")]
        [InlineData("/GeneratedHud.designer.cs", "")]
        [InlineData("/GeneratedHud.generated.cs", "")]
        [InlineData("/GeneratedHud.g.cs", "")]
        [InlineData("/GeneratedHud.g.i.cs", "")]
        [InlineData("/GeneratedHud.cs", "// <auto-generated/>\n")]
        [InlineData("/GeneratedHud.cs", "// <auto-generated>\n//     This code was generated by a tool.\n// </auto-generated>\n")]
        [InlineData("/GeneratedHud.cs", "// <autogenerated />\n")]
        public async Task GeneratedCode_NoRuleReports(string path, string header)
        {
            await new AllRulesTest(Allocation("new List<int>", "Update", 0))
                .WithSources((path, header + GeneratedHud), ("/Hud.cs", HandWrittenHud))
                .RunAsync();
        }

        // `generated_code = true` in .editorconfig marks a file as generated whatever its name.
        [Fact]
        public async Task GeneratedCodeFromEditorConfig_NoRuleReports()
        {
            await new AllRulesTest(Allocation("new List<int>", "Update", 0))
                .WithSources(("/GeneratedHud.cs", GeneratedHud), ("/Hud.cs", HandWrittenHud))
                .WithEditorConfig("[GeneratedHud.cs]\ngenerated_code = true")
                .RunAsync();
        }

        // `[GeneratedCode]` on a class or on a single method takes it out of analysis. Roslyn ignores the
        // attribute on a class declared in more than one part, so when Split has a hand-written half in
        // another file, both halves are analyzed; a generated half needs a generated file name, a header,
        // or the attribute on each method.
        [Fact]
        public async Task GeneratedCodeAttribute_OnClassOrMethod_NotOnAClassInSeveralParts()
        {
            var source = @"
using System.CodeDom.Compiler;
using System.Collections.Generic;
using UnityEngine;

[GeneratedCode(""tool"", ""1.0"")]
public class Generated : MonoBehaviour
{
    int score;

    void Update()
    {
        var list = new List<int>();
        var label = ""Score "" + score;
        var title = name;
    }
}

public class Mixed : MonoBehaviour
{
    [GeneratedCode(""tool"", ""1.0"")]
    void Update() { var list = new List<int>(); }

    void LateUpdate() { var list = {|#0:new List<int>()|}; }
}

[GeneratedCode(""tool"", ""1.0"")]
public partial class Split : MonoBehaviour
{
    void Update() { var list = {|#2:new List<int>()|}; }
}
";

            var handWritten = @"
using System.Collections.Generic;

public partial class Split
{
    void LateUpdate() { var list = {|#1:new List<int>()|}; }
}
";

            await new AllRulesTest(
                    Allocation("new List<int>", "LateUpdate", 0),
                    Allocation("new List<int>", "LateUpdate", 1),
                    Allocation("new List<int>", "Update", 2))
                .WithSources(("/Generated.cs", source), ("/Split.cs", handWritten))
                .RunAsync();
        }

        // A comment that mentions generated code without the tag, or a tag below the first line of code,
        // does not make the file generated.
        [Theory]
        [InlineData("// This file was generated once and is now maintained by hand.\n")]
        [InlineData("using System;\n// <auto-generated/>\n")]
        public async Task NotGeneratedCode_Reports(string header)
        {
            var source = header + @"
using System.Collections.Generic;
using UnityEngine;

public class Hud : MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new List<int>()|};
    }
}
";

            await new AllRulesTest(Allocation("new List<int>", "Update", 0))
                .WithSources(("/Hud.cs", source))
                .RunAsync();
        }

        // --- T44: OPL003 in sub-namespaces of UnityEngine ---

        // OPL003 matches members declared in `UnityEngine` itself and in exactly two of its
        // sub-namespaces, `UnityEngine.SceneManagement` and `UnityEngine.AI`. Any other sub-namespace
        // (`UnityEngine.Rendering`), one level below those two (`UnityEngine.AI.Baking`), an `AI`
        // namespace under another sub-namespace (`UnityEngine.Rendering.AI`) and a `UnityEngine` that is
        // not at the root (`Game.UnityEngine`) are not matched, even for array-returning members.
        [Fact]
        public async Task SubNamespaceMembers_OnlySceneManagementAndAIReport()
        {
            var source = @"
using UnityEngine;

namespace UnityEngine.AI
{
    public static class NavMesh
    {
        public static int[] GetAreaIds() => null;
    }
}

namespace UnityEngine.SceneManagement
{
    public static class SceneUtility
    {
        public static string[] scenePaths => null;
    }
}

namespace UnityEngine.Rendering
{
    public static class Probes
    {
        public static int[] GetIds() => null;
        public static string[] names => null;
    }
}

namespace UnityEngine.AI.Baking
{
    public static class Baker
    {
        public static int[] GetIds() => null;
    }
}

namespace UnityEngine.Rendering.AI
{
    public static class Denoiser
    {
        public static int[] GetIds() => null;
    }
}

namespace Game.UnityEngine
{
    public static class Physics
    {
        public static int[] RaycastAll() => null;
    }
}

public class Scanner : MonoBehaviour
{
    void Update()
    {
        var areas = {|#0:UnityEngine.AI.NavMesh.GetAreaIds()|};
        var paths = {|#1:UnityEngine.SceneManagement.SceneUtility.scenePaths|};
        var ids = UnityEngine.Rendering.Probes.GetIds();
        var names = UnityEngine.Rendering.Probes.names;
        var baked = UnityEngine.AI.Baking.Baker.GetIds();
        var denoised = UnityEngine.Rendering.AI.Denoiser.GetIds();
        var hits = Game.UnityEngine.Physics.RaycastAll();
    }
}
";

            await new AllRulesTest(
                    UnityApi("NavMesh.GetAreaIds", "int[]", "Update", 0),
                    UnityApi("SceneUtility.scenePaths", "string[]", "Update", 1))
                .WithSources(("/Scanner.cs", source))
                .RunAsync();
        }

        // --- T45: OPL003 through a derived-type reference ---

        // `name` is declared once, on UnityEngine.Object, so reading it through a Component, a Transform,
        // a Collider, a GameObject, `this`, `base` or a type parameter constrained to Component resolves
        // to `Object.name` and is reported under that name. `tag` read through a Transform resolves to
        // `Component.tag`. Writes are not reported, and a user type that hides `name` with its own
        // property is not an engine member.
        [Fact]
        public async Task InheritedEngineProperty_ReportsUnderTheDeclaringType()
        {
            var source = @"
using UnityEngine;

public class Unit : MonoBehaviour
{
    public new string name => ""unit"";
}

public class Tracker : MonoBehaviour
{
    public Component target;
    public Collider hitbox;
    public Unit unit;

    void Update()
    {
        var a = {|#0:target.name|};
        var b = {|#1:transform.name|};
        var c = {|#2:hitbox.name|};
        var d = {|#3:gameObject.name|};
        var e = {|#4:this.name|};
        var f = {|#5:base.name|};
        var g = {|#6:transform.tag|};
        var h = unit.name;
        target.name = ""renamed"";
    }
}

public class Follower<T> : MonoBehaviour where T : Component
{
    public T item;

    void Update()
    {
        var label = {|#7:item.name|};
    }
}
";

            await new AllRulesTest(
                    UnityApi("Object.name", "string", "Update", 0),
                    UnityApi("Object.name", "string", "Update", 1),
                    UnityApi("Object.name", "string", "Update", 2),
                    UnityApi("Object.name", "string", "Update", 3),
                    UnityApi("Object.name", "string", "Update", 4),
                    UnityApi("Object.name", "string", "Update", 5),
                    UnityApi("Component.tag", "string", "Update", 6),
                    UnityApi("Object.name", "string", "Update", 7))
                .WithSources(("/Tracker.cs", source))
                .RunAsync();
        }

        // --- T46: allocating and non-allocating overloads in the same method ---

        // Each API is called twice in one Update: once through the overload that returns a new array and
        // once through the one that fills a caller-owned list or array. Only the first is reported, for
        // every pair, whether the call is unqualified, through a field or through a struct; the buffers are
        // allocated in field initializers, which are not hot.
        [Fact]
        public async Task OverloadsInSameUpdate_OnlyTheAllocatingOverloadReports()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public class Scanner : MonoBehaviour
{
    readonly List<Collider> _colliders = new List<Collider>();
    readonly List<Material> _materials = new List<Material>();
    readonly List<GameObject> _roots = new List<GameObject>();
    readonly RaycastHit[] _hits = new RaycastHit[8];
    readonly Vector3[] _corners = new Vector3[16];

    public Renderer body;
    public NavMeshPath route;
    Ray ray;

    void Update()
    {
        var a = {|#0:GetComponentsInChildren<Collider>()|};
        GetComponentsInChildren(_colliders);

        var b = {|#1:GetComponentsInChildren<Collider>(true)|};
        GetComponentsInChildren(true, _colliders);

        var c = {|#2:body.GetComponents<Collider>()|};
        body.GetComponents(_colliders);

        var d = {|#3:GetComponentsInParent<Collider>()|};
        GetComponentsInParent(false, _colliders);

        var e = {|#4:body.sharedMaterials|};
        body.GetSharedMaterials(_materials);

        var f = {|#5:Physics.RaycastAll(ray)|};
        var hitCount = Physics.RaycastNonAlloc(ray, _hits);

        var g = {|#6:route.corners|};
        var cornerCount = route.GetCornersNonAlloc(_corners);

        var scene = SceneManager.GetActiveScene();
        var h = {|#7:scene.GetRootGameObjects()|};
        scene.GetRootGameObjects(_roots);
    }
}
";

            await new AllRulesTest(
                    UnityApi("Component.GetComponentsInChildren", "Collider[]", "Update", 0),
                    UnityApi("Component.GetComponentsInChildren", "Collider[]", "Update", 1),
                    UnityApi("Component.GetComponents", "Collider[]", "Update", 2),
                    UnityApi("Component.GetComponentsInParent", "Collider[]", "Update", 3),
                    UnityApi("Renderer.sharedMaterials", "Material[]", "Update", 4),
                    UnityApi("Physics.RaycastAll", "RaycastHit[]", "Update", 5),
                    UnityApi("NavMeshPath.corners", "Vector3[]", "Update", 6),
                    UnityApi("Scene.GetRootGameObjects", "GameObject[]", "Update", 7))
                .WithSources(("/Scanner.cs", source))
                .RunAsync();
        }
    }
}
