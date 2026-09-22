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
    // F23 (generate the pool class) and F24 (rent the array from ArrayPool<T>.Shared), the two OPL001
    // fixes added in 1.5.4.
    public class PoolGenerationCodeFixTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }

    public class MonoBehaviour : Object { }
}
";

        private const string GeneratePoolKey = "ObjectPoolLinterGeneratePool";
        private const string RentFromArrayPoolKey = "ObjectPoolLinterRentFromArrayPool";

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
            if (diagnosticRemains) test.FixedState.ExpectedDiagnostics.Add(Expected());

            return test.RunAsync();
        }

        // The fix is not offered: the code is left exactly as it was, diagnostic and all.
        private static Task VerifyNoFixAsync(string source, string equivalenceKey) =>
            VerifyFixAsync(source, source, equivalenceKey, diagnosticRemains: true);

        private static DiagnosticResult Expected() =>
            new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0);

        // --- F23: generate the pool ---

        [Fact]
        public async Task GeneratePool_WritesThePoolAndRoutesTheAllocationThroughIt()
        {
            var source = @"
using UnityEngine;

public class Enemy
{
    public int Hp;

    public Enemy(int hp)
    {
        Hp = hp;
    }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = {|#0:new Enemy(5)|};
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class Enemy
{
    public int Hp;

    public Enemy(int hp)
    {
        Hp = hp;
    }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = EnemyPool.Get(5);
    }
}

/// <summary>
/// Object pool for <c>Enemy</c>. Not thread-safe, which is enough for
/// Unity's main-thread messages. Every instance taken out has to be handed back once.
/// </summary>
public static class EnemyPool
{
    private static readonly System.Collections.Generic.Stack<Enemy> s_free = new System.Collections.Generic.Stack<Enemy>();

    /// <summary>How many instances are waiting in the pool.</summary>
    public static int CountInactive
    {
        get { return s_free.Count; }
    }

    /// <summary>Takes an instance out of the pool, or constructs one when the pool is empty.</summary>
    public static Enemy Get(int hp)
    {
        if (s_free.Count == 0) return new Enemy(hp);

        Enemy instance = s_free.Pop();
        // TODO: put instance back into the state new Enemy(int hp) would leave it in.
        return instance;
    }

    /// <summary>Hands an instance back to the pool. Return each instance exactly once.</summary>
    public static void Return(Enemy instance)
    {
        if (instance == null) throw new System.ArgumentNullException(""instance"");

        // TODO: release whatever instance holds on to before it waits in the pool.
        s_free.Push(instance);
    }

    /// <summary>Drops every instance waiting in the pool.</summary>
    public static void Clear()
    {
        s_free.Clear();
    }
}
";

            await VerifyFixAsync(source, fixedSource, GeneratePoolKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task GeneratePool_GenericType_WritesAGenericPool()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = {|#0:new List<int>()|};
    }
}
";

            var fixedSource = @"
using System.Collections.Generic;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var list = ListPool<int>.Get();
    }
}

/// <summary>
/// Object pool for <c>List<T></c>. Not thread-safe, which is enough for
/// Unity's main-thread messages. Every instance taken out has to be handed back once.
/// </summary>
public static class ListPool<T>
{
    private static readonly System.Collections.Generic.Stack<List<T>> s_free = new System.Collections.Generic.Stack<List<T>>();

    /// <summary>How many instances are waiting in the pool.</summary>
    public static int CountInactive
    {
        get { return s_free.Count; }
    }

    /// <summary>Takes an instance out of the pool, or constructs one when the pool is empty.</summary>
    public static List<T> Get()
    {
        if (s_free.Count == 0) return new List<T>();

        List<T> instance = s_free.Pop();
        // TODO: put instance back into the state new List<T>() would leave it in.
        return instance;
    }

    /// <summary>Hands an instance back to the pool. Return each instance exactly once.</summary>
    public static void Return(List<T> instance)
    {
        if (instance == null) throw new System.ArgumentNullException(""instance"");

        // TODO: release whatever instance holds on to before it waits in the pool.
        s_free.Push(instance);
    }

    /// <summary>Drops every instance waiting in the pool.</summary>
    public static void Clear()
    {
        s_free.Clear();
    }
}
";

            await VerifyFixAsync(source, fixedSource, GeneratePoolKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task GeneratePool_NotOfferedWhenAPoolAlreadyExists()
        {
            var source = @"
using UnityEngine;

public class Enemy
{
}

public static class EnemyPool
{
    public static Enemy Get() => new Enemy();
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = {|#0:new Enemy()|};
    }
}
";

            await VerifyNoFixAsync(source, GeneratePoolKey);
        }

        [Fact]
        public async Task GeneratePool_NotOfferedForAUnityObject()
        {
            var source = @"
using UnityEngine;

public class Marker : MonoBehaviour
{
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var marker = {|#0:new Marker()|};
    }
}
";

            await VerifyNoFixAsync(source, GeneratePoolKey);
        }

        [Fact]
        public async Task GeneratePool_NotOfferedWhenAnOptionalArgumentIsOmitted()
        {
            var source = @"
using UnityEngine;

public class Enemy
{
    public Enemy(int hp = 1)
    {
    }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var enemy = {|#0:new Enemy()|};
    }
}
";

            await VerifyNoFixAsync(source, GeneratePoolKey);
        }

        [Fact]
        public async Task GeneratePool_NotOfferedForAnArrayAllocation()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new int[4]|};
        buffer[0] = 1;
    }
}
";

            await VerifyNoFixAsync(source, GeneratePoolKey);
        }

        // --- F24: rent the array ---

        [Fact]
        public async Task RentFromArrayPool_WrapsTheUseInTryFinally()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new int[4]|};
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = i;
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
        int bufferLength = 4;
        var buffer = System.Buffers.ArrayPool<int>.Shared.Rent(bufferLength);
        try
        {
            for (int i = 0; i < bufferLength; i++)
            {
                buffer[i] = i;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(buffer);
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, RentFromArrayPoolKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task RentFromArrayPool_ReferenceElements_AreClearedOnReturn()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var names = {|#0:new string[2]|};
        names[0] = ""a"";
        names[1] = ""b"";
    }
}
";

            var fixedSource = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var names = System.Buffers.ArrayPool<string>.Shared.Rent(2);
        try
        {
            names[0] = ""a"";
            names[1] = ""b"";
        }
        finally
        {
            System.Buffers.ArrayPool<string>.Shared.Return(names, clearArray: true);
        }
    }
}
";

            await VerifyFixAsync(source, fixedSource, RentFromArrayPoolKey, diagnosticRemains: false);
        }

        [Fact]
        public async Task RentFromArrayPool_NotOfferedWhenTheBufferIsPassedOn()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new int[4]|};
        buffer[0] = 1;
        Keep(buffer);
    }

    void Keep(int[] values)
    {
    }
}
";

            await VerifyNoFixAsync(source, RentFromArrayPoolKey);
        }

        [Fact]
        public async Task RentFromArrayPool_NotOfferedWhenTheBufferIsCapturedByALambda()
        {
            var source = @"
using System;
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new int[4]|};
        Action later = () => buffer[0] = 1;
    }
}
";

            await VerifyNoFixAsync(source, RentFromArrayPoolKey);
        }

        [Fact]
        public async Task RentFromArrayPool_NotOfferedForAnArrayWithAnInitializer()
        {
            var source = @"
using UnityEngine;

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new int[2] { 1, 2 }|};
        buffer[0] = 3;
    }
}
";

            await VerifyNoFixAsync(source, RentFromArrayPoolKey);
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
