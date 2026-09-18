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
    public class UnityApiAllocationCodeFixProviderTests
    {
        private const string UnityStub = @"
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get => null; set { } }
    }

    public class GameObject : Object
    {
        public string tag { get => null; set { } }
        public bool CompareTag(string tag) => false;
    }

    public class Component : Object
    {
        public GameObject gameObject => null;
        public string tag { get => null; set { } }
        public bool CompareTag(string tag) => false;
        public T[] GetComponents<T>() => null;
        public void GetComponents<T>(List<T> results) { }
        public T[] GetComponentsInChildren<T>() => null;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => null;
        public void GetComponentsInChildren<T>(List<T> results) { }
        public void GetComponentsInChildren<T>(bool includeInactive, List<T> results) { }
        public T[] GetComponentsInParent<T>() => null;
        public void GetComponentsInParent<T>(bool includeInactive, List<T> results) { }
    }

    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class Collider : Component { }

    public struct Vector2 { public float x; }
    public struct Touch { public Vector2 position; }

    public static class Input
    {
        public static Touch[] touches => null;
        public static int touchCount => 0;
        public static Touch GetTouch(int index) => default;
    }
}
";

        private const string UseCompareTagKey = "ObjectPoolLinterUseCompareTag";
        private const string UseBufferOverloadKey = "ObjectPoolLinterUseBufferOverload";
        private const string UseGetTouchKey = "ObjectPoolLinterUseGetTouch";

        private static Task VerifyFixAsync(string source, string fixedSource, string equivalenceKey)
        {
            var test = CreateTest(source, fixedSource);
            test.CodeActionEquivalenceKey = equivalenceKey;
            test.TestState.ExpectedDiagnostics.Add(Diagnostic());
            return test.RunAsync();
        }

        // The diagnostic is reported, and no fix is offered for it.
        private static Task VerifyNoFixAsync(string source)
        {
            var test = CreateTest(source, source);
            test.TestState.ExpectedDiagnostics.Add(Diagnostic());
            test.FixedState.ExpectedDiagnostics.Add(Diagnostic());
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

        // The arguments are not checked; UnityApiAllocationAnalyzerTests covers the message.
        private static DiagnosticResult Diagnostic(int location = 0) =>
            new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(location);

        [Fact]
        public async Task CompareTag_OtherTagEquals()
        {
            var source = @"
using UnityEngine;

public class Trigger : MonoBehaviour
{
    void OnTriggerStay(Collider other)
    {
        if ({|#0:other.tag|} == ""Player"")
            return;
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Trigger : MonoBehaviour
{
    void OnTriggerStay(Collider other)
    {
        if (other.CompareTag(""Player""))
            return;
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseCompareTagKey);
        }

        [Fact]
        public async Task CompareTag_OwnTagNotEquals_Negates()
        {
            var source = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    const string PlayerTag = ""Player"";

    void Update()
    {
        var hostile = {|#0:tag|} != PlayerTag;
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    const string PlayerTag = ""Player"";

    void Update()
    {
        var hostile = !CompareTag(PlayerTag);
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseCompareTagKey);
        }

        [Fact]
        public async Task CompareTag_StringOnLeft_GameObjectTag()
        {
            var source = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    GameObject target;

    void Update()
    {
        if (""Player"" == {|#0:target.tag|}) { }
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    GameObject target;

    void Update()
    {
        if (target.CompareTag(""Player"")) { }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseCompareTagKey);
        }

        [Fact]
        public async Task CompareTag_NullComparison_NoFix()
        {
            var source = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Update()
    {
        if ({|#0:tag|} == null) { }
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task CompareTag_TagStoredInLocal_NoFix()
        {
            var source = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Update()
    {
        var current = {|#0:tag|};
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task CompareTag_SideEffectsOnBothSidesReordered_NoFix()
        {
            var source = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    string NextTag() => ""Player"";
    GameObject NextTarget() => null;

    void Update()
    {
        if (NextTag() == {|#0:NextTarget().tag|}) { }
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task BufferOverload_LocalWithLengthAndIndexer()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    void Update()
    {
        var colliders = {|#0:GetComponentsInChildren<Collider>()|};
        for (int i = 0; i < colliders.Length; i++)
        {
            var collider = colliders[i];
        }
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    private readonly List<Collider> _collidersBuffer = new List<Collider>();

    void Update()
    {
        GetComponentsInChildren<Collider>(_collidersBuffer);
        var colliders = _collidersBuffer;
        for (int i = 0; i < colliders.Count; i++)
        {
            var collider = colliders[i];
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseBufferOverloadKey);
        }

        [Fact]
        public async Task BufferOverload_ForEach_ArgumentKept()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    List<int> seen = new List<int>();

    void LateUpdate()
    {
        foreach (var collider in {|#0:GetComponentsInChildren<Collider>(true)|})
        {
        }
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    List<int> seen = new List<int>();
    private readonly List<Collider> _colliderBuffer = new List<Collider>();

    void LateUpdate()
    {
        GetComponentsInChildren<Collider>(true, _colliderBuffer);
        foreach (var collider in _colliderBuffer)
        {
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseBufferOverloadKey);
        }

        [Fact]
        public async Task BufferOverload_InParent_ExplicitArrayType_PassesIncludeInactiveFalse()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Collider target;

    void Update()
    {
        Collider[] parents = {|#0:target.GetComponentsInParent<Collider>()|};
        foreach (var parent in parents) { }
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Collider target;
    private readonly List<Collider> _parentsBuffer = new List<Collider>();

    void Update()
    {
        target.GetComponentsInParent<Collider>(false, _parentsBuffer);
        var parents = _parentsBuffer;
        foreach (var parent in parents) { }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseBufferOverloadKey);
        }

        [Fact]
        public async Task BufferOverload_ArrayEscapes_NoFix()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Collider[] cached;

    void Update()
    {
        var colliders = {|#0:GetComponents<Collider>()|};
        cached = colliders;
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task BufferOverload_UsedInExpression_NoFix()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    void Update()
    {
        var count = {|#0:GetComponents<Collider>()|}.Length;
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task GetTouch_ForEachBecomesForLoop()
        {
            var source = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        foreach (var touch in {|#0:Input.touches|})
        {
            var position = touch.position;
        }
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        for (int i = 0; i < Input.touchCount; i++)
        {
            var touch = Input.GetTouch(i);
            var position = touch.position;
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseGetTouchKey);
        }

        [Fact]
        public async Task GetTouch_ForEachWithoutBraces_IndexNameTaken()
        {
            var source = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    int i;

    void Handle(Touch touch, int frame) { }

    void Update()
    {
        foreach (Touch touch in {|#0:Input.touches|})
            Handle(touch, i);
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    int i;

    void Handle(Touch touch, int frame) { }

    void Update()
    {
        for (int i2 = 0; i2 < Input.touchCount; i2++)
        {
            Touch touch = Input.GetTouch(i2);
            Handle(touch, i);
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseGetTouchKey);
        }

        [Fact]
        public async Task GetTouch_LengthBecomesTouchCount()
        {
            var source = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        if ({|#0:Input.touches|}.Length > 1) { }
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        if (Input.touchCount > 1) { }
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseGetTouchKey);
        }

        [Fact]
        public async Task GetTouch_IndexerBecomesGetTouch()
        {
            var source = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        var first = {|#0:Input.touches|}[0].position;
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        var first = Input.GetTouch(0).position;
    }
}
";

            await VerifyFixAsync(source, fixedSource, UseGetTouchKey);
        }

        [Fact]
        public async Task GetTouch_IndexerWritten_NoFix()
        {
            var source = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        {|#0:Input.touches|}[0].position.x = 1;
    }
}
";

            await VerifyNoFixAsync(source);
        }

        [Fact]
        public async Task GetTouch_ArrayStored_NoFix()
        {
            var source = @"
using UnityEngine;

public class TouchInput : MonoBehaviour
{
    void Update()
    {
        var touches = {|#0:Input.touches|};
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
                yield return new UnityApiAllocationAnalyzer();
            }

            protected override IEnumerable<CodeFixProvider> GetCodeFixProviders()
            {
                yield return new UnityApiAllocationCodeFixProvider();
            }
        }
    }
}
