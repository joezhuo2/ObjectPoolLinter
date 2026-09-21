using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // The generator added in 1.5.3 (F30). Every test that expects a pool compiles the generated
    // source together with code that calls it, because "produces compilable pool code" is the whole
    // acceptance criterion - asserting on the generated text alone would pass on code that does not
    // build.
    public class ObjectPoolGeneratorTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { }
}
";

        private static readonly MetadataReference[] References = LoadRuntimeReferences();

        private static MetadataReference[] LoadRuntimeReferences()
        {
            // Only the shared framework, not the test host's own dependencies: two copies of the same
            // assembly in one compilation is an error, and the generated pool needs nothing else.
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

            return trusted
                .Split(Path.PathSeparator)
                .Where(path => path.Length > 0 && string.Equals(Path.GetDirectoryName(path), runtimeDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();
        }

        private sealed class GeneratorRun
        {
            internal Compilation Output { get; set; } = null!;

            internal ImmutableArray<Diagnostic> GeneratorDiagnostics { get; set; }

            internal IReadOnlyList<GeneratedSourceResult> GeneratedSources { get; set; } = Array.Empty<GeneratedSourceResult>();

            internal string Source(string hintName) =>
                GeneratedSources.Single(source => source.HintName == hintName).SourceText.ToString();

            internal bool Generated(string hintName) =>
                GeneratedSources.Any(source => source.HintName == hintName);

            internal void AssertCompiles()
            {
                var errors = Output.GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .ToArray();

                Assert.True(
                    errors.Length == 0,
                    "Generated code did not compile:\n" + string.Join("\n", errors.Select(error => error.ToString())));
            }
        }

        private static GeneratorRun Run(string source, LanguageVersion languageVersion = LanguageVersion.Latest, params string[] additionalSources)
        {
            var parseOptions = new CSharpParseOptions(languageVersion);

            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, parseOptions) };
            foreach (var additional in additionalSources)
                trees.Add(CSharpSyntaxTree.ParseText(additional, parseOptions));

            var compilation = CSharpCompilation.Create(
                "ObjectPoolGeneratorTests",
                trees,
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver
                .Create(new ISourceGenerator[] { new ObjectPoolGenerator() }, parseOptions: parseOptions)
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

            return new GeneratorRun
            {
                Output = output,
                GeneratorDiagnostics = diagnostics,
                GeneratedSources = driver.GetRunResult().Results.Single().GeneratedSources
            };
        }

        private static void AssertRejected(GeneratorRun run, string reasonFragment)
        {
            var diagnostic = Assert.Single(run.GeneratorDiagnostics.Where(d => d.Id == ObjectPoolGenerator.DiagnosticId));
            Assert.Contains(reasonFragment, diagnostic.GetMessage(), StringComparison.Ordinal);

            // The attribute is always emitted; nothing else should be.
            Assert.Single(run.GeneratedSources);
        }

        // --- The happy path ---

        [Fact]
        public void GeneratesPoolWithGetAndReturn()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Enemy
{
    public int Hp;
    public Enemy(int hp) { Hp = hp; }
}

public static class Caller
{
    public static void Use()
    {
        Enemy enemy = EnemyPool.Get(7);
        EnemyPool.Return(enemy);
        int waiting = EnemyPool.CountInactive;
        EnemyPool.Clear();
    }
}
");

            run.AssertCompiles();
            Assert.Empty(run.GeneratorDiagnostics);
            Assert.True(run.Generated("EnemyPool.g.cs"));
            Assert.True(run.Generated(ObjectPoolGenerator.AttributeHintName));
        }

        [Fact]
        public void PoolNameAndGetArityMatchTheOpl001Contract()
        {
            // OPL001's code fix rewrites `new Enemy(hp)` to `EnemyPool.Get(hp)`: a static Get on a
            // type named {TypeName}Pool, reachable by simple name, taking the constructor's arguments.
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Enemy
{
    public Enemy(int hp) { }
}
");

            run.AssertCompiles();

            var pool = run.Output.GetTypeByMetadataName("EnemyPool");
            Assert.NotNull(pool);
            Assert.True(pool!.IsStatic);

            var get = Assert.Single(pool.GetMembers("Get").OfType<IMethodSymbol>());
            Assert.True(get.IsStatic);
            Assert.Equal("Enemy", get.ReturnType.Name);
            Assert.Single(get.Parameters);
        }

        [Fact]
        public void GeneratesOneGetPerConstructor()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Bullet
{
    public Bullet() { }
    public Bullet(int damage) { }
    internal Bullet(string tag) { }
    private Bullet(double unreachable) { }
}

public static class Caller
{
    public static void Use()
    {
        BulletPool.Return(BulletPool.Get());
        BulletPool.Return(BulletPool.Get(1));
        BulletPool.Return(BulletPool.Get(""boss""));
    }
}
");

            run.AssertCompiles();

            var pool = run.Output.GetTypeByMetadataName("BulletPool")!;
            Assert.Equal(3, pool.GetMembers("Get").OfType<IMethodSymbol>().Count());
        }

        [Fact]
        public void ConsumerCanImplementTheResetHooks()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Enemy
{
    public int Hp;
    public object Target;

    public Enemy(int hp) { Hp = hp; }
}

static partial class EnemyPool
{
    static partial void Reinitialize(Enemy instance, int hp)
    {
        instance.Hp = hp;
    }

    static partial void OnReturn(Enemy instance)
    {
        instance.Target = null;
    }
}
");

            run.AssertCompiles();
            Assert.Empty(run.GeneratorDiagnostics);
        }

        [Fact]
        public void GeneratesIntoTheTypesNamespace()
        {
            var run = Run(@"
using ObjectPoolLinter;

namespace Game.Combat
{
    [ObjectPool]
    public class Enemy
    {
        public Enemy() { }
    }

    public static class Caller
    {
        public static void Use() { EnemyPool.Return(EnemyPool.Get()); }
    }
}
");

            run.AssertCompiles();
            Assert.True(run.Generated("Game.Combat.EnemyPool.g.cs"));
            Assert.NotNull(run.Output.GetTypeByMetadataName("Game.Combat.EnemyPool"));
        }

        [Fact]
        public void GeneratesGenericPoolWithConstraints()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Box<T> where T : class, System.IDisposable, new()
{
    public T Value;
    public Box(T value) { Value = value; }
}

public sealed class Payload : System.IDisposable
{
    public void Dispose() { }
}

public static class Caller
{
    public static void Use()
    {
        Box<Payload> box = BoxPool<Payload>.Get(new Payload());
        BoxPool<Payload>.Return(box);
    }
}
");

            run.AssertCompiles();
            Assert.True(run.Generated("BoxPool_1.g.cs"));
            Assert.Contains("where T : class, global::System.IDisposable, new()", run.Source("BoxPool_1.g.cs"), StringComparison.Ordinal);
        }

        [Fact]
        public void GeneratesForAnInternalNestedClass()
        {
            var run = Run(@"
using ObjectPoolLinter;

namespace Game
{
    public class Spawner
    {
        [ObjectPool]
        internal class Slot
        {
            public Slot() { }
        }

        internal void Use() { SlotPool.Return(SlotPool.Get()); }
    }
}
");

            run.AssertCompiles();
            Assert.True(run.Generated("Game.SlotPool.g.cs"));
        }

        [Fact]
        public void HonoursPoolNameAndInitialCapacity()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool(PoolName = ""Bullets"", InitialCapacity = 64)]
public class Bullet
{
    public Bullet() { }
}

public static class Caller
{
    public static void Use() { Bullets.Return(Bullets.Get()); }
}
");

            run.AssertCompiles();
            Assert.Contains("Stack<global::Bullet>(64)", run.Source("Bullets.g.cs"), StringComparison.Ordinal);
        }

        [Fact]
        public void ForwardsDefaultValuesParamsAndKeywordNames()
        {
            var run = Run(@"
using ObjectPoolLinter;

public enum Team { None = 0, Red = 1 }

[ObjectPool]
public class Enemy
{
    public Enemy(int hp = 100, float speed = 1.5f, string name = ""grunt"", Team team = Team.Red, object owner = null) { }
    public Enemy(params int[] waypoints) { }
    public Enemy(bool @ref, char @class) { }
}

public static class Caller
{
    public static void Use()
    {
        EnemyPool.Return(EnemyPool.Get());
        EnemyPool.Return(EnemyPool.Get(1, 2, 3));
        EnemyPool.Return(EnemyPool.Get(true, 'x'));
    }
}
");

            run.AssertCompiles();

            var pool = run.Source("EnemyPool.g.cs");
            Assert.Contains("int hp = 100", pool, StringComparison.Ordinal);
            Assert.Contains("float speed = 1.5F", pool, StringComparison.Ordinal);
            Assert.Contains("string name = \"grunt\"", pool, StringComparison.Ordinal);
            Assert.Contains("global::Team team = (global::Team)(1)", pool, StringComparison.Ordinal);
            Assert.Contains("object owner = null", pool, StringComparison.Ordinal);
            Assert.Contains("params int[] waypoints", pool, StringComparison.Ordinal);
            Assert.Contains("bool @ref, char @class", pool, StringComparison.Ordinal);
        }

        [Fact]
        public void GeneratedCodeCompilesAsCSharp73()
        {
            // Unity 2021.3 compiles user code as C# 9, but the generated pool deliberately stays
            // inside 7.3 so nothing in it depends on the host's language version.
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool(InitialCapacity = 4)]
public class Enemy
{
    public Enemy() { }
    public Enemy(int hp, ref int seed, in double weight) { }
}
", LanguageVersion.CSharp7_3);

            run.AssertCompiles();
        }

        [Fact]
        public void AttributeIsGeneratedEvenWithNoPools()
        {
            var run = Run(@"
public class Empty { }
");

            run.AssertCompiles();
            var only = Assert.Single(run.GeneratedSources);
            Assert.Equal(ObjectPoolGenerator.AttributeHintName, only.HintName);
        }

        [Fact]
        public void IgnoresAnUnrelatedObjectPoolAttribute()
        {
            var run = Run(@"
namespace Other
{
    public sealed class ObjectPoolAttribute : System.Attribute { }
}

[Other.ObjectPool]
public class Enemy
{
    public Enemy() { }
}
");

            run.AssertCompiles();
            Assert.Empty(run.GeneratorDiagnostics);
            Assert.Single(run.GeneratedSources);
        }

        // --- OPL005 ---

        [Fact]
        public void ReportsAbstractClass()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public abstract class Actor
{
    protected Actor() { }
}
");

            AssertRejected(run, "it is abstract");
        }

        [Fact]
        public void ReportsStaticClass()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public static class Helpers { }
");

            AssertRejected(run, "it is a static class");
        }

        [Fact]
        public void ReportsUnityObject()
        {
            var run = Run(@"
using ObjectPoolLinter;
using UnityEngine;

[ObjectPool]
public class Enemy : MonoBehaviour
{
    public Enemy() { }
}
", LanguageVersion.Latest, UnityStub);

            AssertRejected(run, "Instantiate");
        }

        [Fact]
        public void ReportsPrivateNestedClass()
        {
            var run = Run(@"
using ObjectPoolLinter;

public class Spawner
{
    [ObjectPool]
    private class Slot
    {
        public Slot() { }
    }
}
");

            AssertRejected(run, "not visible");
        }

        [Fact]
        public void ReportsClassWithNoReachableConstructor()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Enemy
{
    private Enemy() { }
    protected Enemy(int hp) { }
    public Enemy(out int spawned) { spawned = 0; }
}
");

            AssertRejected(run, "no constructor the pool can call");
        }

        [Fact]
        public void ReportsInvalidPoolName()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool(PoolName = ""not a name"")]
public class Enemy
{
    public Enemy() { }
}
");

            AssertRejected(run, "is not a valid C# identifier");
        }

        [Fact]
        public void ReportsNegativeInitialCapacity()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool(InitialCapacity = -1)]
public class Enemy
{
    public Enemy() { }
}
");

            AssertRejected(run, "InitialCapacity is negative");
        }

        [Fact]
        public void ReportsAnExistingPoolTypeThatIsNotPartial()
        {
            var run = Run(@"
using ObjectPoolLinter;

[ObjectPool]
public class Enemy
{
    public Enemy() { }
}

public class EnemyPool { }
");

            AssertRejected(run, "already exists here and is not a static partial class");
        }

        [Fact]
        public void ReportsTwoTypesCompetingForOnePoolName()
        {
            var run = Run(@"
using ObjectPoolLinter;

namespace Game
{
    public class Red
    {
        [ObjectPool]
        internal class Slot { public Slot() { } }
    }

    public class Blue
    {
        [ObjectPool]
        internal class Slot { public Slot() { } }
    }
}
");

            var reported = run.GeneratorDiagnostics.Where(d => d.Id == ObjectPoolGenerator.DiagnosticId).ToArray();
            Assert.Equal(2, reported.Length);
            Assert.All(reported, d => Assert.Contains("also generates 'SlotPool'", d.GetMessage(), StringComparison.Ordinal));
            Assert.Single(run.GeneratedSources);
        }
    }
}
