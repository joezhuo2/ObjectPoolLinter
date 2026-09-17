using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    public class HiddenAllocationCodeFixProviderTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public struct Vector3 { }
    public class MonoBehaviour : Object { }
}
";

        private const string CacheLambdaKey = "ObjectPoolLinterCacheLambda";
        private const string CacheMethodGroupKey = "ObjectPoolLinterCacheMethodGroup";
        private const string UseStringBuilderKey = "ObjectPoolLinterUseStringBuilder";
        private const string LinqToLoopKey = "ObjectPoolLinterLinqToLoop";

        private static Task VerifyFixAsync(string source, string fixedSource, string equivalenceKey, params DiagnosticResult[] remaining)
        {
            var test = CreateTest(source, fixedSource);
            test.CodeActionEquivalenceKey = equivalenceKey;
            test.TestState.ExpectedDiagnostics.Add(Diagnostic());
            test.FixedState.ExpectedDiagnostics.AddRange(remaining);
            return test.RunAsync();
        }

        // The diagnostics are reported, and no fix is offered for any of them.
        private static Task VerifyNoFixAsync(string source, params DiagnosticResult[] expected)
        {
            if (expected.Length == 0) expected = new[] { Diagnostic() };

            var test = CreateTest(source, source);
            test.TestState.ExpectedDiagnostics.AddRange(expected);
            test.FixedState.ExpectedDiagnostics.AddRange(expected);
            return test.RunAsync();
        }

        private static Test CreateTest(string source, string fixedSource)
        {
            var test = new Test
            {
                TestCode = source,
                FixedCode = fixedSource,
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CompilerDiagnostics = CompilerDiagnostics.Errors,
                CodeFixTestBehaviors = CodeFixTestBehaviors.FixOne | CodeFixTestBehaviors.SkipFixAllCheck,
            };

            test.TestState.Sources.Add(UnityStub);
            test.FixedState.Sources.Add(UnityStub);
            return test;
        }

        private static DiagnosticResult Diagnostic(int location = 0) =>
            new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info).WithLocation(location);

        [Fact]
        public async Task CacheLambda_PromotesCapturedLocalAndAddsAwake()
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

    void Update()
    {
        count = 3;
        Run(_next);
    }
}
";

            await VerifyFixAsync(source, fixedSource, CacheLambdaKey);
        }

        [Fact]
        public async Task CacheLambda_CapturingThis_AppendsToExistingAwake()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int wave;
    Action onWave;

    void Awake()
    {
        wave = 1;
    }

    void Update()
    {
        onWave += {|#0:() =>
        {
            wave++;
        }|};
    }
}
";

            var fixedSource = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int wave;
    Action onWave;
    private Action _onWave;

    void Awake()
    {
        wave = 1;
        _onWave = () =>
        {
            wave++;
        };
    }

    void Update()
    {
        onWave += _onWave;
    }
}
";

            await VerifyFixAsync(source, fixedSource, CacheLambdaKey);
        }

        [Fact]
        public async Task CacheLambda_CapturingParameter_OffersNoFix()
        {
            var source = @"
using System;
using UnityEngine;

namespace UnityEngine
{
    public class Collider : Object { }
}

public class Spawner : MonoBehaviour
{
    void Run(Func<float> next) { }

    void OnTriggerStay(Collider other)
    {
        Run({|#0:() => other == null ? 0f : 1f|});
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task CacheLambda_LocalDeclaredInLoop_OffersNoFix()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Run(Func<int> next) { }

    void Update()
    {
        for (var i = 0; i < 3; i++)
        {
            var count = i;
            Run({|#0:() => count + 1|});
        }
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task CacheLambda_ExpressionBodiedAwake_OffersNoFix()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int wave;

    void Awake() => wave = 1;

    void Run(Func<int> next) { }

    void Update()
    {
        Run({|#0:() => wave|});
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task CacheMethodGroup_AssignsFieldInNewAwake()
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
        callback();
    }
}
";

            var fixedSource = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    private Action _spawn;

    private void Awake()
    {
        _spawn = Spawn;
    }

    void Spawn() { }

    void Update()
    {
        Action callback = _spawn;
        callback();
    }
}
";

            await VerifyFixAsync(source, fixedSource, CacheMethodGroupKey);
        }

        [Fact]
        public async Task CacheMethodGroup_PicksFreeFieldName()
        {
            var source = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int _spawn;
    event Action Spawned;

    void Spawn() { }

    void Awake()
    {
    }

    void Update()
    {
        Spawned += {|#0:this.Spawn|};
    }
}
";

            var fixedSource = @"
using System;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    int _spawn;
    event Action Spawned;
    private Action _spawn2;

    void Spawn() { }

    void Awake()
    {
        _spawn2 = this.Spawn;
    }

    void Update()
    {
        Spawned += _spawn2;
    }
}
";

            await VerifyFixAsync(source, fixedSource, CacheMethodGroupKey);
        }

        [Fact]
        public async Task CacheMethodGroup_OnAnotherInstance_OffersNoFix()
        {
            var source = @"
using System;
using UnityEngine;

public class Enemy
{
    public void Die() { }
}

public class Spawner : MonoBehaviour
{
    Enemy enemy = null!;

    void Update()
    {
        Action callback = {|#0:enemy.Die|};
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task UseStringBuilder_AddsFieldAndUsing()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp;

    void Update()
    {
        // label
        var text = {|#0:$""hp: {hp}""|};
    }
}
";

            var fixedSource = @"
using System.Text;
using UnityEngine;

public class Hud : MonoBehaviour
{
    int hp;
    private readonly StringBuilder _textBuilder = new StringBuilder();

    void Update()
    {
        // label
        _textBuilder.Clear().Append(""hp: "").Append(hp);
        var text = {|#0:_textBuilder.ToString()|};
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseStringBuilderKey, Diagnostic());
        }

        [Fact]
        public async Task UseStringBuilder_BoxesStructHolesExplicitlyAndEscapesText()
        {
            var source = @"
using System.Text;
using UnityEngine;

public class Hud : MonoBehaviour
{
    Vector3 position;
    string title = """";

    void Show(string text) { }

    void Update()
    {
        Show({|#0:$""{title} at \""{position}\""""|});
    }
}
";

            var fixedSource = @"
using System.Text;
using UnityEngine;

public class Hud : MonoBehaviour
{
    Vector3 position;
    string title = """";
    private readonly StringBuilder _textBuilder = new StringBuilder();

    void Show(string text) { }

    void Update()
    {
        _textBuilder.Clear().Append(title).Append("" at \"""").Append({|#1:(object)position|}).Append(""\"""");
        Show({|#0:_textBuilder.ToString()|});
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseStringBuilderKey, Diagnostic(0), Diagnostic(1));
        }

        [Fact]
        public async Task UseStringBuilder_FormatClauseOrEarlierSideEffect_OffersNoFix()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    float speed;
    int hp;

    int Next() => hp++;
    void Show(int a, string b) { }

    void Update()
    {
        var text = {|#0:$""{speed:F2}""|};
        Show(Next(), {|#1:$""{hp}""|});
    }
}
";

            await VerifyNoFixAsync(source, Diagnostic(0), Diagnostic(1));
        }

        [Fact]
        public async Task LinqToLoop_WhereSelectOverList()
        {
            var source = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    List<int> hp = null!;

    void Update()
    {
        var alive = {|#0:hp.Where(h => h > 0).Select(h => h * 2).ToList()|};
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    List<int> hp = null!;
    private readonly List<int> _aliveBuffer = new List<int>();

    void Update()
    {
        _aliveBuffer.Clear();
        for (int i = 0; i < hp.Count; i++)
        {
            var h = hp[i];
            if (h > 0)
                _aliveBuffer.Add(h * 2);
        }
        var alive = _aliveBuffer;
    }
}
";

            await VerifyFixAsync(source, fixedSource, LinqToLoopKey);
        }

        [Fact]
        public async Task LinqToLoop_SelectOverArrayFromCall_HoistsSourceAndRenames()
        {
            var source = @"
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    int i;

    string[] Names() => new string[0];

    void Update()
    {
        var lengths = {|#0:Names().Select(i => i.Length).ToList()|};
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    int i;
    private readonly List<int> _lengthsBuffer = new List<int>();

    string[] Names() => new string[0];

    void Update()
    {
        _lengthsBuffer.Clear();
        var source = Names();
        for (int i2 = 0; i2 < source.Length; i2++)
        {
            var i = source[i2];
            _lengthsBuffer.Add(i.Length);
        }
        var lengths = _lengthsBuffer;
    }
}
";

            await VerifyFixAsync(source, fixedSource, LinqToLoopKey);
        }

        [Fact]
        public async Task LinqToLoop_UnsupportedChain_OffersNoFix()
        {
            var source = @"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class Squad : MonoBehaviour
{
    List<int> hp = null!;

    void Update()
    {
        var sorted = {|#0:hp.OrderBy(h => h).ToList()|};
        var alive = {|#1:hp.Where(h => { return h > 0; }).ToList()|};
    }
}
";

            await VerifyNoFixAsync(source, Diagnostic(0), Diagnostic(1));
        }

        [Fact]
        public async Task OtherConstructs_OfferNoFix()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    string label = """";
    int hp;

    void Update()
    {
        var text = {|#0:label + hp|};
    }
}
";

            await VerifyNoFixAsync(source);
        }

        private sealed class Test : CodeFixTest<DefaultVerifier>
        {
            public override string Language => LanguageNames.CSharp;

            protected override string DefaultFileExt => "cs";

            public override System.Type SyntaxKindType => typeof(SyntaxKind);

            protected override CompilationOptions CreateCompilationOptions() =>
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            protected override ParseOptions CreateParseOptions() =>
                new CSharpParseOptions(LanguageVersion.Latest);

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                yield return new HiddenAllocationAnalyzer();
            }

            protected override IEnumerable<CodeFixProvider> GetCodeFixProviders()
            {
                yield return new HiddenAllocationCodeFixProvider();
            }
        }
    }
}
