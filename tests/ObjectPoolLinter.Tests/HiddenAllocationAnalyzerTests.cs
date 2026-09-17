using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    public class HiddenAllocationAnalyzerTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public struct Vector3 { }
    public class MonoBehaviour : Object { }
}
";

        private static HotPathAnalyzerTest<HiddenAllocationAnalyzer> CreateTest(string source, params DiagnosticResult[] expected)
        {
            return new HotPathAnalyzerTest<HiddenAllocationAnalyzer>(source, UnityStub, expected);
        }

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            return CreateTest(source, expected).RunAsync();
        }

        private static DiagnosticResult Diagnostic(string allocation, string method = "Update", int location = 0)
        {
            return new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(allocation, method);
        }

        [Fact]
        public void Descriptor_IsInfoWithHelpLink()
        {
            var descriptor = Assert.Single(new HiddenAllocationAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL002", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Info, descriptor.DefaultSeverity);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL002.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task StringConcatenation_ReportsOnceForTheWholeChain()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    string label = ""hp"";
    int hp;

    void Update()
    {
        var text = {|#0:label + "": "" + hp|};
    }
}
";

            await VerifyAsync(source, Diagnostic("string concatenation"));
        }

        [Fact]
        public async Task ConstantConcatenation_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    const string Prefix = ""hp"";

    void Update()
    {
        var text = Prefix + "": "" + ""full"";
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task StringCompoundAssignment_Reports()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    string log = """";

    void Update()
    {
        {|#0:log += ""tick""|};
    }
}
";

            await VerifyAsync(source, Diagnostic("string concatenation"));
        }

        [Fact]
        public async Task StringInterpolation_Reports()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp;

    void Update()
    {
        var text = {|#0:$""hp: {hp}""|};
        var plain = $""no holes"";
    }
}
";

            await VerifyAsync(source, Diagnostic("string interpolation"));
        }

        [Fact]
        public async Task StringConcatCall_ReportsEveryOverload()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    string a = ""a"", b = ""b"", c = ""c"", d = ""d"", e = ""e"";
    string[] parts = { ""a"", ""b"" };

    void Update()
    {
        var two = {|#0:string.Concat(a, b)|};
        var three = {|#1:string.Concat(a, b, c)|};
        var five = {|#2:string.Concat(a, b, c, d, e)|};
        var joined = {|#3:string.Concat(parts)|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("string.Concat()"),
                Diagnostic("string.Concat()", location: 1),
                Diagnostic("string.Concat()", location: 2),
                Diagnostic("string.Concat()", location: 3));
        }

        [Fact]
        public async Task StringConcatCall_DoesNotAlsoReportBoxingOrParamsArray()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp, mp, xp, lvl, gold;

    void Update()
    {
        var text = {|#0:string.Concat(hp, mp, xp, lvl, gold)|};
    }
}
";

            await VerifyAsync(source, Diagnostic("string.Concat()"));
        }

        [Fact]
        public async Task StringFormat_Reports_WithoutBoxingOrParamsArray()
        {
            var source = @"
using System.Globalization;
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp, mp, xp, lvl;

    void Update()
    {
        var one = {|#0:string.Format(""hp: {0}"", hp)|};
        var four = {|#1:string.Format(""{0} {1} {2} {3}"", hp, mp, xp, lvl)|};
        var invariant = {|#2:string.Format(CultureInfo.InvariantCulture, ""{0}"", hp)|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("string.Format()"),
                Diagnostic("string.Format()", location: 1),
                Diagnostic("string.Format()", location: 2));
        }

        [Fact]
        public async Task StringBuilderToString_Reports()
        {
            var source = @"
using System.Text;
using UnityEngine;

public class Hud : MonoBehaviour
{
    readonly StringBuilder builder = new StringBuilder();

    void Update()
    {
        builder.Clear();
        builder.Append(""hp"");
        var text = {|#0:builder.ToString()|};
        var slice = {|#1:builder.ToString(0, 1)|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("StringBuilder.ToString()"),
                Diagnostic("StringBuilder.ToString()", location: 1));
        }

        [Fact]
        public async Task StringCallsInColdPath_DoNotReport()
        {
            var source = @"
using System.Text;
using UnityEngine;

public class Hud : MonoBehaviour
{
    readonly StringBuilder builder = new StringBuilder();
    int hp;

    void Start()
    {
        var a = string.Concat(""a"", ""b"", ""c"");
        var b = string.Format(""{0}"", hp);
        var c = builder.ToString();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task LambdaCapturingLocal_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        var count = 3;
        Func<int> next = {|#0:() => count + 1|};
    }
}
";

            await VerifyAsync(source, Diagnostic("lambda capturing count"));
        }

        [Fact]
        public async Task LambdaCapturingThis_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int wave;

    void Update()
    {
        Func<int> next = {|#0:() => wave + 1|};
    }
}
";

            await VerifyAsync(source, Diagnostic("lambda capturing this"));
        }

        [Fact]
        public async Task NonCapturingLambda_DoesNotReport()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        Func<int, int> twice = x => { var y = x * 2; return y; };
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task LambdaWithObjectInitializer_IsNotACaptureOfThis()
        {
            var source = @"
using System;
using System.Collections.Generic;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        Func<List<int>> make = () => new List<int> { 1, 2 };
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task ExplicitDelegateCreation_IsLeftToOpl001()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Spawn() { }

    void Update()
    {
        var callback = new Action(Spawn);
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task InstanceMethodGroup_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Spawn() { }

    void Update()
    {
        Action callback = {|#0:Spawn|};
    }
}
";

            await VerifyAsync(source, Diagnostic("delegate for Spawn()"));
        }

        [Fact]
        public async Task StaticMethodGroup_UnderCSharp11_DoesNotReport()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    static void Spawn() { }

    void Update()
    {
        Action callback = Spawn;
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task StaticMethodGroup_UnderCSharp9_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    static void Spawn() { }

    void Update()
    {
        Action callback = {|#0:Spawn|};
    }
}
";

            var test = CreateTest(source, Diagnostic("delegate for Spawn()"));
            test.LanguageVersion = LanguageVersion.CSharp9;
            await test.RunAsync();
        }

        [Fact]
        public async Task ParamsArray_ReportsOnlyWhenExpanded()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    static void Show(string format, params string[] args) { }
    readonly string[] cached = { ""a"" };

    void Update()
    {
        {|#0:Show(""{0} {1}"", ""a"", ""b"")|};
        Show(""empty"");
        Show(""{0}"", cached);
    }
}
";

            await VerifyAsync(source, Diagnostic("params string[] for Show()"));
        }

        [Fact]
        public async Task LinqChain_ReportsOnceOnTheOutermostCall()
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
        var alive = {|#0:hp.Where(h => h > 0).Select(h => h * 2).ToList()|};
    }
}
";

            await VerifyAsync(source, Diagnostic("LINQ Where().Select().ToList()"));
        }

        [Fact]
        public async Task LinqQuerySyntax_Reports()
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
        var alive = {|#0:(from h in hp where h > 0 select h * 2).ToList()|};
        var lazy = {|#1:from h in hp where h > 0 select h|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("LINQ query.ToList()"),
                Diagnostic("LINQ query", location: 1));
        }

        [Fact]
        public async Task BoxingExistingValue_Reports()
        {
            var source = @"
using System;
using UnityEngine;

public class Counter : MonoBehaviour
{
    int count;

    void Update()
    {
        object boxed = {|#0:count|};
        IComparable comparable = {|#1:count|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("boxing int to object"),
                Diagnostic("boxing int to IComparable", location: 1));
        }

        [Fact]
        public async Task BoxingOnCreation_IsLeftToOpl001()
        {
            var source = @"
using UnityEngine;

public class Counter : MonoBehaviour
{
    void Update()
    {
        object boxed = new Vector3();
        object nothing = (int?)null;
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task StructWithoutOverride_CallingObjectMethod_Reports()
        {
            var source = @"
using UnityEngine;

public struct Cell { public int X; }

public class Grid : MonoBehaviour
{
    Cell cell;
    int count;

    void Update()
    {
        var hash = {|#0:cell.GetHashCode()|};
        var intHash = count.GetHashCode();
    }
}
";

            await VerifyAsync(source, Diagnostic("boxing Cell for GetHashCode()"));
        }

        [Fact]
        public async Task ColdPathMethod_DoesNotReport()
        {
            var source = @"
using System.Linq;
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp;

    void Start()
    {
        var text = ""hp: "" + hp;
        object boxed = hp;
        var any = new[] { 1 }.Any();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task AdditionalHotMethod_Reports()
        {
            var source = @"
public class Simulation
{
    int step;

    public void Tick(float deltaTime)
    {
        var text = {|#0:$""step {step}""|};
    }
}
";

            await CreateTest(source, Diagnostic("string interpolation", "Tick"))
                .WithEditorConfig("object_pool_linter.additional_hot_methods = Tick")
                .RunAsync();
        }
    }
}
