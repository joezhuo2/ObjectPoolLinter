using System.IO;
using System.Threading;
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

        // --- LINQ-style extension methods (F4) ---

        private const string SequenceExtensions = @"
using System.Collections;
using System.Collections.Generic;

public static class SequenceExtensions
{
    public static IEnumerable<T> TakeEvery<T>(this IEnumerable<T> source, int step)
    {
        var i = 0;
        foreach (var item in source)
            if (i++ % step == 0) yield return item;
    }

    public static int CountAll(this IEnumerable source)
    {
        var count = 0;
        foreach (var _ in source) count++;
        return count;
    }

    public static int Last<T>(this List<T> list) => list.Count - 1;

    public static int Peek<T>(this IReadOnlyList<T> list) => list.Count;

    public static int CountFast<TSource, T>(this TSource source) where TSource : IEnumerable<T> => 0;
}
";

        [Fact]
        public async Task CustomLinqExtension_Reports()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Squad : MonoBehaviour
{
    readonly List<int> hp = new List<int>();

    void Update()
    {
        var sampled = {|#0:hp.TakeEvery(2)|};
        var count = {|#1:hp.CountAll()|};
        var viaStatic = {|#2:SequenceExtensions.TakeEvery(hp, 3)|};
    }
}
";

            var test = CreateTest(
                source,
                Diagnostic("LINQ TakeEvery()"),
                Diagnostic("LINQ CountAll()", location: 1),
                Diagnostic("LINQ TakeEvery()", location: 2));
            test.TestState.Sources.Add(SequenceExtensions);
            await test.RunAsync();
        }

        [Fact]
        public async Task CustomLinqExtension_JoinsTheEnumerableChain()
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
        var alive = {|#0:hp.Where(h => h > 0).TakeEvery(2).ToList()|};
    }
}
";

            var test = CreateTest(source, Diagnostic("LINQ Where().TakeEvery().ToList()"));
            test.TestState.Sources.Add(SequenceExtensions);
            await test.RunAsync();
        }

        [Fact]
        public async Task ExtensionNotTakingASequenceInterface_DoesNotReport()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Squad : MonoBehaviour
{
    readonly List<int> hp = new List<int>();

    void Update()
    {
        var last = hp.Last();
        var peek = hp.Peek();
        var fast = hp.CountFast<List<int>, int>();
    }
}
";

            var test = CreateTest(source);
            test.TestState.Sources.Add(SequenceExtensions);
            await test.RunAsync();
        }

        // --- Iterator state machines (F5) ---

        [Fact]
        public async Task IteratorCall_Reports()
        {
            var source = @"
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    IEnumerator Spawn()
    {
        yield return null;
    }

    IEnumerable<int> Nearby()
    {
        yield return 1;
        yield break;
    }

    void Update()
    {
        var routine = {|#0:Spawn()|};
        foreach (var id in {|#1:Nearby()|}) { }

        IEnumerable<int> Local() { yield return 2; }
        var local = {|#2:Local()|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("iterator state machine for Spawn()"),
                Diagnostic("iterator state machine for Nearby()", location: 1),
                Diagnostic("iterator state machine for Local()", location: 2));
        }

        [Fact]
        public async Task YieldOnlyInsideNestedFunction_IsNotAnIterator()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int Count()
    {
        IEnumerable<int> Inner() { yield return 1; }
        return 1;
    }

    void Update()
    {
        var count = Count();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task IteratorCallInColdPath_DoesNotReport()
        {
            var source = @"
using System.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    IEnumerator Spawn()
    {
        yield return null;
    }

    void Start()
    {
        var routine = Spawn();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task HotMethodThatIsAnIterator_ReportsOnItsName()
        {
            var source = @"
using System.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    IEnumerator {|#0:Update|}()
    {
        yield return null;
    }
}
";

            await VerifyAsync(source, Diagnostic("iterator state machine for Update()"));
        }

        [Fact]
        public async Task AdditionalHotMethodThatIsAnIterator_Reports()
        {
            var source = @"
using System.Collections.Generic;

public class Simulation
{
    public IEnumerable<int> {|#0:Tick|}()
    {
        yield return 1;
    }
}
";

            await CreateTest(source, Diagnostic("iterator state machine for Tick()", "Tick"))
                .WithEditorConfig("object_pool_linter.additional_hot_methods = Tick")
                .RunAsync();
        }

        // --- Async state machines (F6) ---

        [Fact]
        public async Task AsyncCall_Reports()
        {
            var source = @"
using System.Threading.Tasks;
using UnityEngine;

public class Saver : MonoBehaviour
{
    async Task SaveAsync() => await Task.Yield();
    async ValueTask<int> CountAsync() { await Task.Yield(); return 1; }
    async void Fire() => await Task.Yield();

    void Update()
    {
        _ = {|#0:SaveAsync()|};
        _ = {|#1:CountAsync()|};
        {|#2:Fire()|};

        async Task Local() => await Task.Yield();
        _ = {|#3:Local()|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("async state machine for SaveAsync()"),
                Diagnostic("async state machine for CountAsync()", location: 1),
                Diagnostic("async state machine for Fire()", location: 2),
                Diagnostic("async state machine for Local()", location: 3));
        }

        [Fact]
        public async Task AsyncUpdate_ReportsOnItsName()
        {
            var source = @"
using System.Threading.Tasks;
using UnityEngine;

public class Saver : MonoBehaviour
{
    async void {|#0:Update|}()
    {
        await Task.Yield();
    }
}
";

            await VerifyAsync(source, Diagnostic("async state machine for Update()"));
        }

        [Fact]
        public async Task AsyncWithPoolingBuilder_DoesNotReport()
        {
            var source = @"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

public class Saver : MonoBehaviour
{
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    async ValueTask SaveAsync() => await Task.Yield();

    Task NotAsync() => Task.CompletedTask;

    void Update()
    {
        _ = SaveAsync();
        _ = NotAsync();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task IteratorAndAsyncMethodsFromAnotherAssembly_Report()
        {
            var library = await CompileLibraryAsync(@"
using System.Collections.Generic;
using System.Threading.Tasks;

public static class Library
{
    public static IEnumerable<int> Ids() { yield return 1; }
    public static async Task LoadAsync() => await Task.Yield();
    public static Task Plain() => Task.CompletedTask;
}
");

            var source = @"
using UnityEngine;

public class Loader : MonoBehaviour
{
    void Update()
    {
        var ids = {|#0:Library.Ids()|};
        _ = {|#1:Library.LoadAsync()|};
        _ = Library.Plain();
    }
}
";

            var test = CreateTest(
                source,
                Diagnostic("iterator state machine for Ids()"),
                Diagnostic("async state machine for LoadAsync()", location: 1));
            test.TestState.AdditionalReferences.Add(library);
            await test.RunAsync();
        }

        // --- foreach enumerators (F7) ---

        [Fact]
        public async Task ForEachOverInterface_Reports()
        {
            var source = @"
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Squad : MonoBehaviour
{
    IList<int> hp = new List<int>();
    IReadOnlyList<float> speeds = new List<float>();
    IEnumerable<int> ids = new List<int>();
    IDictionary<int, string> names = new Dictionary<int, string>();
    IEnumerable untyped = new ArrayList();

    void Update()
    {
        {|#0:foreach (var h in hp)|} { }
        {|#1:foreach (var s in speeds)|} { }
        {|#2:foreach (var id in (ids))|} { }
        {|#3:foreach (var (key, value) in names)|} { }
        {|#4:foreach (object o in untyped)|} { }
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("enumerator for foreach over IList<int>"),
                Diagnostic("enumerator for foreach over IReadOnlyList<float>", location: 1),
                Diagnostic("enumerator for foreach over IEnumerable<int>", location: 2),
                Diagnostic("enumerator for foreach over IDictionary<int, string>", location: 3),
                Diagnostic("enumerator for foreach over IEnumerable", location: 4));
        }

        [Fact]
        public async Task ForEachOverConcreteCollection_DoesNotReport()
        {
            var source = @"
using System;
using System.Collections.Generic;
using UnityEngine;

public class Squad : MonoBehaviour
{
    List<int> hp = new List<int>();
    int[] ids = new int[4];
    int[,] grid = new int[2, 2];
    string label = ""abc"";
    Dictionary<int, string> names = new Dictionary<int, string>();
    HashSet<int> seen = new HashSet<int>();

    void Update()
    {
        foreach (var h in hp) { }
        foreach (var id in ids) { }
        foreach (var cell in grid) { }
        foreach (var c in label) { }
        foreach (var pair in names) { }
        foreach (var s in seen) { }
        foreach (var x in ids.AsSpan()) { }
    }
}
";

            await VerifyAsync(source);
        }

        // A struct that implements IEnumerable<T> only explicitly is boxed to reach GetEnumerator, and
        // the enumerator comes back boxed as well: two allocations, reported separately.
        [Fact]
        public async Task ForEachThroughInterfaceReturningGetEnumerator_Reports()
        {
            var source = @"
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

public struct Bag : IEnumerable<int>
{
    IEnumerator<int> IEnumerable<int>.GetEnumerator() { yield break; }
    IEnumerator IEnumerable.GetEnumerator() { yield break; }
}

public class Squad : MonoBehaviour
{
    Bag bag;
    ReadOnlyCollection<int> view = new List<int>().AsReadOnly();

    void Scan<T>(T items) where T : IEnumerable<int>
    {
        foreach (var i in items) { }
    }

    void Update()
    {
        {|#0:foreach (var b in {|#2:bag|})|} { }
        {|#1:foreach (var v in view)|} { }
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("enumerator for foreach over Bag"),
                Diagnostic("enumerator for foreach over ReadOnlyCollection<int>", location: 1),
                Diagnostic("boxing Bag to IEnumerable<int>", location: 2));
        }

        [Fact]
        public async Task ForEachOverLinqOrIterator_ReportsOnlyTheCall()
        {
            var source = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    List<int> hp = new List<int>();

    IEnumerable<int> Alive()
    {
        foreach (var h in hp) if (h > 0) yield return h;
    }

    void Update()
    {
        foreach (var h in {|#0:hp.Where(x => x > 0)|}) { }
        foreach (var h in {|#1:from x in hp select x|}) { }
        foreach (var h in {|#2:Alive()|}) { }
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("LINQ Where()"),
                Diagnostic("LINQ query", location: 1),
                Diagnostic("iterator state machine for Alive()", location: 2));
        }

        [Fact]
        public async Task ForEachOutsideHotPath_DoesNotReport()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Squad : MonoBehaviour
{
    IList<int> hp = new List<int>();

    void Start()
    {
        foreach (var h in hp) { }
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task AwaitForEach_IsNotAnEnumeratorAllocation()
        {
            var source = @"
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class Squad : MonoBehaviour
{
    IAsyncEnumerable<int> stream;

    async void {|#0:Update|}()
    {
        await foreach (var x in stream) { }
    }
}
";

            await VerifyAsync(source, Diagnostic("async state machine for Update()"));
        }

        private static async Task<MetadataReference> CompileLibraryAsync(string source)
        {
            var references = await ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);
            var compilation = CSharpCompilation.Create(
                "Library",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }
    }
}
