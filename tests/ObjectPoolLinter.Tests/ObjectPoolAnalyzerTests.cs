using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    public class ObjectPoolAnalyzerTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object
    {
        public static Object Instantiate(Object original) => null;
        public static Object Instantiate(Object original, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation) => null;
    }

    public struct Vector3 { }
    public struct Quaternion { }

    public class Collider : Object { }
    public class Collider2D : Object { }
    public class Collision { }
    public class Collision2D { }

    public class MonoBehaviour : Object
    {
    }
}
";

        private static Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
        {
            var test = new Test
            {
                TestCode = source,
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };

            test.TestState.Sources.Add(UnityStub);
            test.ExpectedDiagnostics.AddRange(expected);
            return test.RunAsync();
        }

        // Verifies a compilation that does not reference the Unity stub, so the analyzer sees no
        // UnityEngine types at all.
        private static Task VerifyWithoutUnityAsync(string source, params DiagnosticResult[] expected)
        {
            var test = new Test
            {
                TestCode = source,
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };

            test.ExpectedDiagnostics.AddRange(expected);
            return test.RunAsync();
        }

        [Fact]
        public void Descriptor_HasHelpLinkToTheRuleDoc()
        {
            var descriptor = Assert.Single(new ObjectPoolAnalyzer().SupportedDiagnostics);

            Assert.Equal(ObjectPoolAnalyzer.DiagnosticId, descriptor.Id);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL001.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task NewObjectInUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task InstantiateInUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public Object prefab;
    void Update()
    {
        {|#0:Object.Instantiate(prefab)|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "Instantiate");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task NewObjectInNonHotPathMethod_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Start()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewStructInUpdate_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public struct MyStruct { }

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var s = new MyStruct();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInNonMonoBehaviourClass_DoesNotReport()
        {
            var source = @"
public class PlainClass
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInFixedUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void FixedUpdate()
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("FixedUpdate", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task NewObjectInLambdaRegisteredAsCallback_DoesNotReport()
        {
            var source = @"
using System;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public Action callback;

    void Update()
    {
        callback = () => { var list = new System.Collections.Generic.List<int>(); };
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInImmediatelyInvokedLambdaInUpdate_ReportsDiagnostic()
        {
            var source = @"
using System;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        ((Action)(() => { var list = {|#0:new System.Collections.Generic.List<int>()|}; }))();
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task NewObjectInLocalFunctionCalledFromUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        Spawn();

        void Spawn()
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task NewObjectInLocalFunctionOnlyUsedAsDelegate_DoesNotReport()
        {
            var source = @"
using System;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public Action callback;

    void Update()
    {
        callback = Spawn;

        void Spawn()
        {
            var list = new System.Collections.Generic.List<int>();
        }
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInLocalFunctionInsideEscapingLambda_DoesNotReport()
        {
            var source = @"
using System;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public Action callback;

    void Update()
    {
        callback = () =>
        {
            Spawn();

            void Spawn()
            {
                var list = new System.Collections.Generic.List<int>();
            }
        };
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task UpdateWithParameters_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update(float deltaTime)
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task StaticUpdate_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    static void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task GenericUpdate_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update<T>()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task OnTriggerStayWithCollider_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnTriggerStay(Collider other)
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("OnTriggerStay", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task OnTriggerStayWithoutParameter_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnTriggerStay()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task OnTriggerStayWithWrongParameterType_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnTriggerStay(Collider2D other)
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task OnCollisionStay2DWithCollision2D_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnCollisionStay2D(Collision2D collision)
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("OnCollisionStay2D", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task OnAnimatorIKWithLayerIndex_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnAnimatorIK(int layerIndex)
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("OnAnimatorIK", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task OnAnimatorIKWithWrongParameterType_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnAnimatorIK(float layerIndex)
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task UpdateWithRefParameter_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void OnAnimatorIK(ref int layerIndex)
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInUpdateOfLookAlikeMonoBehaviour_DoesNotReport()
        {
            var source = @"
namespace Game.UnityEngine
{
    public class MonoBehaviour { }
}

public class MyBehaviour : Game.UnityEngine.MonoBehaviour
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInUpdateOfNestedMonoBehaviour_DoesNotReport()
        {
            var source = @"
namespace UnityEngine
{
    public class Outer
    {
        public class MonoBehaviour { }
    }
}

public class MyBehaviour : UnityEngine.Outer.MonoBehaviour
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task LookAlikeInstantiateInUpdate_DoesNotReport()
        {
            var source = @"
using UnityEngine;

namespace Game.UnityEngine
{
    public class Object
    {
        public static Object Instantiate(Object original) => null;
    }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        Game.UnityEngine.Object.Instantiate(null);
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task AllocationInUpdateWithoutUnityEngine_DoesNotReport()
        {
            var source = @"
public class MonoBehaviour { }

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            await VerifyWithoutUnityAsync(source);
        }

        [Fact]
        public async Task AllocationInUpdateWithoutUnityObject_ReportsDiagnostic()
        {
            var source = @"
namespace UnityEngine
{
    public class MonoBehaviour { }
}

public class MyBehaviour : UnityEngine.MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "System.Collections.Generic.List<int>");

            await VerifyWithoutUnityAsync(source, expected);
        }

        [Fact]
        public async Task ArrayCreationInUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new int[10]|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "int[10]");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task ImplicitArrayCreationInUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new[] { 1, 2 }|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "int[]");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task TargetTypedNewInUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        System.Collections.Generic.List<int> list = {|#0:new()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task ArrayCreationInNonHotPathMethod_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Start()
    {
        var buffer = new int[10];
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task InstantiateOutsideHotPath_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public Object prefab;

    void Start()
    {
        Object.Instantiate(prefab);
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        [Fact]
        public async Task NewObjectInGrandchildOfMonoBehaviour_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class BaseBehaviour : MonoBehaviour { }

public class MiddleBehaviour : BaseBehaviour { }

public class MyBehaviour : MiddleBehaviour
{
    void Update()
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        [Fact]
        public async Task NewObjectInNestedMonoBehaviourClass_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class Outer : MonoBehaviour
{
    public class Inner : MonoBehaviour
    {
        void Update()
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Update", "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        // A plain class nested inside a MonoBehaviour must not inherit hot-path status from the
        // enclosing type: containment is not inheritance.
        [Fact]
        public async Task NewObjectInPlainClassNestedInMonoBehaviour_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class Outer : MonoBehaviour
{
    public class Helper
    {
        public void Update()
        {
            var list = new System.Collections.Generic.List<int>();
        }
    }
}
";

            await VerifyAnalyzerAsync(source);
        }

        // Smoke test across the full hot-path table: every message the analyzer recognises, declared
        // with the signature Unity actually calls, reports on an allocation in its body.
        [Theory]
        [InlineData("Update", "")]
        [InlineData("FixedUpdate", "")]
        [InlineData("LateUpdate", "")]
        [InlineData("OnGUI", "")]
        [InlineData("OnTriggerStay", "Collider other")]
        [InlineData("OnTriggerStay2D", "Collider2D other")]
        [InlineData("OnCollisionStay", "Collision collision")]
        [InlineData("OnCollisionStay2D", "Collision2D collision")]
        [InlineData("OnMouseOver", "")]
        [InlineData("OnMouseDrag", "")]
        [InlineData("OnAnimatorMove", "")]
        [InlineData("OnAnimatorIK", "int layerIndex")]
        [InlineData("OnRenderObject", "")]
        [InlineData("OnWillRenderObject", "")]
        [InlineData("OnPreRender", "")]
        [InlineData("OnPostRender", "")]
        [InlineData("OnDrawGizmos", "")]
        [InlineData("OnDrawGizmosSelected", "")]
        public async Task NewObjectInHotPathMessage_ReportsDiagnostic(string messageName, string parameters)
        {
            var source = $@"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{{
    void {messageName}({parameters})
    {{
        var list = {{|#0:new System.Collections.Generic.List<int>()|}};
    }}
}}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments(messageName, "System.Collections.Generic.List<int>");

            await VerifyAnalyzerAsync(source, expected);
        }

        // The inverse of the smoke test: a method whose name is not in the hot-path table is ignored
        // even when it is a real Unity message.
        [Theory]
        [InlineData("Start", "")]
        [InlineData("Awake", "")]
        [InlineData("OnEnable", "")]
        [InlineData("OnTriggerEnter", "Collider other")]
        [InlineData("OnCollisionEnter", "Collision collision")]
        public async Task NewObjectInColdPathMessage_DoesNotReport(string messageName, string parameters)
        {
            var source = $@"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{{
    void {messageName}({parameters})
    {{
        var list = new System.Collections.Generic.List<int>();
    }}
}}
";

            await VerifyAnalyzerAsync(source);
        }

        private sealed class Test : AnalyzerTest<DefaultVerifier>
        {
            public Test()
            {
                SolutionTransforms.Add((solution, projectId) =>
                {
                    var compilationOptions = solution.GetProject(projectId)!.CompilationOptions;
                    compilationOptions = compilationOptions!.WithSpecificDiagnosticOptions(
                        compilationOptions.SpecificDiagnosticOptions.SetItems(GetNullableWarningsFromCompiler()));
                    return solution.WithProjectCompilationOptions(projectId, compilationOptions);
                });
            }

            public override string Language => LanguageNames.CSharp;

            protected override string DefaultFileExt => "cs";

            protected override CompilationOptions CreateCompilationOptions()
            {
                var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
                return compilationOptions.WithSpecificDiagnosticOptions(
                    compilationOptions.SpecificDiagnosticOptions.SetItems(GetNullableWarningsFromCompiler()));
            }

            protected override ParseOptions CreateParseOptions()
            {
                return new CSharpParseOptions(LanguageVersion.Latest);
            }

            private static ImmutableDictionary<string, ReportDiagnostic> GetNullableWarningsFromCompiler()
            {
                return ImmutableDictionary<string, ReportDiagnostic>.Empty;
            }

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                yield return new ObjectPoolAnalyzer();
            }
        }
    }
}