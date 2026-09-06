using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    public class ObjectPoolCodeFixProviderTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object
    {
        public static Object Instantiate(Object original) => null;
    }

    public class MonoBehaviour : Object
    {
    }
}
";

        private const string AddPoolingCommentKey = "ObjectPoolLinterAddPoolingComment";
        private const string ReplaceWithPoolGetKey = "ObjectPoolLinterReplaceWithPoolGet";

        private static Task VerifyFixAsync(string source, string fixedSource, string equivalenceKey, bool diagnosticRemains)
        {
            var test = new Test
            {
                TestCode = source,
                FixedCode = fixedSource,
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = equivalenceKey,
                CompilerDiagnostics = CompilerDiagnostics.Errors,
                CodeFixTestBehaviors = CodeFixTestBehaviors.FixOne | CodeFixTestBehaviors.SkipFixAllCheck,
            };

            test.TestState.Sources.Add(UnityStub);
            test.FixedState.Sources.Add(UnityStub);

            test.TestState.ExpectedDiagnostics.Add(Expected());
            if (diagnosticRemains)
                test.FixedState.ExpectedDiagnostics.Add(Expected());

            return test.RunAsync();
        }

        private static DiagnosticResult Expected() =>
            new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0);

        [Fact]
        public async Task AddPoolingComment_OnLocalDeclaration_ProducesCompilableCode()
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

            var fixedSource = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        // TODO: use an object pool to avoid per-frame allocation
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            await VerifyFixAsync(source, fixedSource, AddPoolingCommentKey, diagnosticRemains: true);
        }

        [Fact]
        public async Task AddPoolingComment_OnExpressionStatement_ProducesCompilableCode()
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

            var fixedSource = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    public Object prefab;
    void Update()
    {
        // TODO: use an object pool to avoid per-frame allocation
        {|#0:Object.Instantiate(prefab)|};
    }
}
";

            await VerifyFixAsync(source, fixedSource, AddPoolingCommentKey, diagnosticRemains: true);
        }

        [Fact]
        public async Task AddPoolingComment_OnNestedStatement_KeepsInnerIndentation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        if (true)
        {
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        if (true)
        {
            // TODO: use an object pool to avoid per-frame allocation
            var list = {|#0:new System.Collections.Generic.List<int>()|};
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, AddPoolingCommentKey, diagnosticRemains: true);
        }

        [Fact]
        public async Task ReplaceWithPoolGet_StillProducesTheExpectedInvocation()
        {
            var source = @"
using UnityEngine;

public class ListPool<T>
{
    public static System.Collections.Generic.List<T> Get() => null;
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new System.Collections.Generic.List<int>()|};
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class ListPool<T>
{
    public static System.Collections.Generic.List<T> Get() => null;
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = ListPool<int>.Get();
    }
}
";

            await VerifyFixAsync(source, fixedSource, ReplaceWithPoolGetKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task ReplaceWithPoolGet_ForwardsConstructorArguments()
        {
            var source = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp) { }
}

public class EnemyPool
{
    public static Enemy Get(int hp) => null;
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = {|#0:new Enemy(10)|};
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp) { }
}

public class EnemyPool
{
    public static Enemy Get(int hp) => null;
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = EnemyPool.Get(10);
    }
}
";

            await VerifyFixAsync(source, fixedSource, ReplaceWithPoolGetKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task ReplaceWithPoolGet_ForwardsNamedAndOutArguments()
        {
            var source = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp, string name, out bool ok) { ok = true; }
}

public class EnemyPool
{
    public static Enemy Get(int hp, string name, out bool ok) { ok = true; return null; }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = {|#0:new Enemy(10, name: ""goblin"", ok: out var ok)|};
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp, string name, out bool ok) { ok = true; }
}

public class EnemyPool
{
    public static Enemy Get(int hp, string name, out bool ok) { ok = true; return null; }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = EnemyPool.Get(10, name: ""goblin"", ok: out var ok);
    }
}
";

            await VerifyFixAsync(source, fixedSource, ReplaceWithPoolGetKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task ReplaceWithPoolGet_ForwardsArgumentsOfTargetTypedNew()
        {
            var source = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp) { }
}

public class EnemyPool
{
    public static Enemy Get(int hp) => null;
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        Enemy enemy = {|#0:new(10)|};
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp) { }
}

public class EnemyPool
{
    public static Enemy Get(int hp) => null;
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        Enemy enemy = EnemyPool.Get(10);
    }
}
";

            await VerifyFixAsync(source, fixedSource, ReplaceWithPoolGetKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task ReplaceWithPoolGet_IsNotOfferedWhenAnInitializerIsPresent()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int> { 1, 2 };
    }
}
";

            var actions = await RegisterFixesAsync(source);

            Assert.DoesNotContain(actions, a => a.EquivalenceKey == ReplaceWithPoolGetKey);
            Assert.Contains(actions, a => a.EquivalenceKey == AddPoolingCommentKey);
        }

        [Fact]
        public async Task AddPoolingComment_FixedDocumentHasNoCompilerErrors()
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

            var fixedDocument = await ApplyAddPoolingCommentFixAsync(source);
            var text = (await fixedDocument.GetTextAsync()).ToString();

            Assert.Contains("// TODO: use an object pool to avoid per-frame allocation", text);

            var reparsed = fixedDocument.Project
                .RemoveDocument(fixedDocument.Id)
                .AddDocument(fixedDocument.Name, text)
                .Project;

            var compilation = await reparsed.GetCompilationAsync();

            var errors = compilation!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            Assert.Empty(errors);
        }

        private static async Task<Document> ApplyAddPoolingCommentFixAsync(string source)
        {
            var (document, actions) = await RegisterFixesCoreAsync(source);

            var commentAction = Assert.Single(actions, a => a.EquivalenceKey == AddPoolingCommentKey);

            var operations = await commentAction.GetOperationsAsync(CancellationToken.None);
            var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;

            return changedSolution.GetDocument(document.Id)!;
        }

        private static async Task<List<CodeAction>> RegisterFixesAsync(string source)
        {
            var (_, actions) = await RegisterFixesCoreAsync(source);
            return actions;
        }

        private static async Task<(Document Document, List<CodeAction> Actions)> RegisterFixesCoreAsync(string source)
        {
            using var workspace = new AdhocWorkspace();

            var project = workspace.CurrentSolution
                .AddProject("TestProject", "TestProject", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithMetadataReferences(await ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, CancellationToken.None));

            var document = project.AddDocument("Test0.cs", source);
            project = document.Project.AddDocument("UnityStub.cs", UnityStub).Project;
            document = project.GetDocument(document.Id)!;

            var compilation = await document.Project.GetCompilationAsync();
            var withAnalyzers = compilation!.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new ObjectPoolAnalyzer()));

            var diagnostic = Assert.Single(await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None));

            var actions = new List<CodeAction>();
            var context = new CodeFixContext(
                document,
                diagnostic,
                (action, _) => actions.Add(action),
                CancellationToken.None);

            await new ObjectPoolCodeFixProvider().RegisterCodeFixesAsync(context);

            return (document, actions);
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
                yield return new ObjectPoolAnalyzer();
            }

            protected override IEnumerable<CodeFixProvider> GetCodeFixProviders()
            {
                yield return new ObjectPoolCodeFixProvider();
            }
        }
    }
}
