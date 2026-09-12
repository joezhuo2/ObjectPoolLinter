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

        [Fact]
        public async Task NewObjectInUpdate_ReportsDiagnostic()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(8, 20, 8, 62)
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
        Object.Instantiate(prefab);
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(9, 9, 9, 35)
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
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(8, 20, 8, 62)
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
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(8, 20, 8, 62)
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
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(8, 20, 8, 62)
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
        var list = new System.Collections.Generic.List<int>();
    }
}
";

            var expected = new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithSpan(8, 20, 8, 62)
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