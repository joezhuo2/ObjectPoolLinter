using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F36 / T101 (1.9.2): the hot flag propagates from a hot method to the helpers it calls, up to
    // object_pool_linter.max_call_depth calls away (3 by default).
    public class CallGraphTests
    {
        private static HotPathAnalyzerTest<TAnalyzer> CreateTest<TAnalyzer>(string source, params DiagnosticResult[] expected)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            return new HotPathAnalyzerTest<TAnalyzer>(source, SharedUnityStub.Source, expected);
        }

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
            CreateTest<ObjectPoolAnalyzer>(source, expected).RunAsync();

        private static Task VerifyWithEditorConfigAsync(string source, string editorConfig, params DiagnosticResult[] expected) =>
            CreateTest<ObjectPoolAnalyzer>(source, expected).WithEditorConfig(editorConfig).RunAsync();

        private static DiagnosticResult Allocation(string method, int location = 0, string allocation = "new List<int>")
        {
            return new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(allocation, method);
        }

        // Update -> A -> B -> C -> D, each allocating.
        private const string Chain = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Update() { A(); }

    void A() { var a = {|#0:new List<int>()|}; B(); }
    void B() { var b = {|#1:new List<int>()|}; C(); }
    void C() { var c = {|#2:new List<int>()|}; D(); }
    void D() { var d = {|#3:new List<int>()|}; }
}
";

        [Fact]
        public async Task HelperCalledFromUpdate_ReportsInTheHelper()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Update()
    {
        Spawn();
    }

    void Spawn()
    {
        var targets = {|#0:new List<int>()|};
    }
}
";

            await VerifyAsync(source, Allocation("Spawn"));
        }

        [Fact]
        public async Task DefaultDepth_ReachesThreeCallsDeep()
        {
            var source = Chain.Replace("{|#3:new List<int>()|}", "new List<int>()");

            await VerifyAsync(source, Allocation("A"), Allocation("B", 1), Allocation("C", 2));
        }

        [Fact]
        public async Task MaxCallDepth_LimitsHowFarTheFlagTravels()
        {
            var source = Chain
                .Replace("{|#1:new List<int>()|}", "new List<int>()")
                .Replace("{|#2:new List<int>()|}", "new List<int>()")
                .Replace("{|#3:new List<int>()|}", "new List<int>()");

            await VerifyWithEditorConfigAsync(source, "object_pool_linter.max_call_depth = 1", Allocation("A"));
        }

        [Fact]
        public async Task MaxCallDepth_Larger_ReachesFurther()
        {
            await VerifyWithEditorConfigAsync(
                Chain,
                "object_pool_linter.max_call_depth = 4",
                Allocation("A"), Allocation("B", 1), Allocation("C", 2), Allocation("D", 3));
        }

        [Fact]
        public async Task MaxCallDepth_Zero_TurnsPropagationOff()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Update()
    {
        var own = {|#0:new List<int>()|};
        Spawn();
    }

    void Spawn()
    {
        var targets = new List<int>();
    }
}
";

            await VerifyWithEditorConfigAsync(source, "object_pool_linter.max_call_depth = 0", Allocation("Update"));
        }

        [Fact]
        public async Task Recursion_Terminates()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Tree : MonoBehaviour
{
    void Update() { Walk(3); }

    void Walk(int depth)
    {
        var children = {|#0:new List<int>()|};
        if (depth > 0) { Walk(depth - 1); Visit(); }
    }

    void Visit()
    {
        var seen = {|#1:new List<int>()|};
        Walk(0);
    }
}
";

            await VerifyAsync(source, Allocation("Walk"), Allocation("Visit", 1));
        }

        [Fact]
        public async Task HelperInAnotherClassAndFile_Reports()
        {
            var source = @"
using UnityEngine;

public class Enemy : MonoBehaviour
{
    readonly PathFinder _paths = new PathFinder();

    void Update()
    {
        _paths.Build();
    }
}
";

            var pathFinder = @"
using System.Collections.Generic;

public class PathFinder
{
    public void Build()
    {
        var open = new List<int>();
    }
}
";

            var test = CreateTest<ObjectPoolAnalyzer>(
                source,
                new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                    .WithSpan("/0/Test2.cs", 8, 20, 8, 35)
                    .WithArguments("new List<int>", "Build"));
            test.TestState.Sources.Add(pathFinder);
            await test.RunAsync();
        }

        [Fact]
        public async Task AdditionalHotMethod_PropagatesToItsHelpers()
        {
            var source = @"
using System.Collections.Generic;

public class Simulation
{
    public void Tick()
    {
        Step();
    }

    void Step()
    {
        var bodies = {|#0:new List<int>()|};
    }
}
";

            await VerifyWithEditorConfigAsync(source, "object_pool_linter.additional_hot_methods = Tick", Allocation("Step"));
        }

        [Fact]
        public async Task HelperCalledOnlyFromStart_NoDiagnostic()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Start()
    {
        Spawn();
    }

    void Spawn()
    {
        var targets = new List<int>();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task VirtualCall_ReachesEveryOverride()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public abstract class Weapon
{
    public abstract void Fire();

    public virtual void Reload()
    {
        var clip = {|#0:new List<int>()|};
    }
}

public class Sword : Weapon
{
    public override void Fire() { var arc = {|#1:new List<int>()|}; }
}

public class Bow : Weapon
{
    public override void Fire() { var arrows = {|#2:new List<int>()|}; }

    public override void Reload() { var quiver = {|#3:new List<int>()|}; }
}

public class Player : MonoBehaviour
{
    Weapon _weapon;

    void Update()
    {
        _weapon.Fire();
        _weapon.Reload();
    }
}
";

            await VerifyAsync(
                source,
                Allocation("Reload"), Allocation("Fire", 1), Allocation("Fire", 2), Allocation("Reload", 3));
        }

        [Fact]
        public async Task BaseCall_ReachesOnlyTheBaseImplementation()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Unit : MonoBehaviour
{
    protected virtual void Think() { var plan = {|#0:new List<int>()|}; }
}

public class Archer : Unit
{
    void Update() { base.Think(); }
}

public class Knight : Unit
{
    protected override void Think() { var plan = new List<int>(); }
}
";

            await VerifyAsync(source, Allocation("Think"));
        }

        [Fact]
        public async Task InterfaceCall_ReachesEveryImplementation()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public interface IBehaviour { void Tick(); }

public interface IHandler<T> { void Handle(T value); }

public class Patrol : IBehaviour
{
    public void Tick() { var waypoints = {|#0:new List<int>()|}; }
}

public class Chase : IBehaviour
{
    void IBehaviour.Tick() { var path = {|#1:new List<int>()|}; }
}

public class Damage : IHandler<int>
{
    public void Handle(int value) { var hits = {|#2:new List<int>()|}; }
}

public class Unrelated
{
    public void Tick() { var nothing = new List<int>(); }
}

public class Brain : MonoBehaviour
{
    IBehaviour _behaviour;
    IHandler<int> _handler;

    void Update()
    {
        _behaviour.Tick();
        _handler.Handle(1);
    }
}
";

            // An explicit implementation is named the way C# declares it.
            await VerifyAsync(source, Allocation("Tick"), Allocation("IBehaviour.Tick", 1), Allocation("Handle", 2));
        }

        [Fact]
        public async Task GenericAndExtensionHelpers_Report()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public static class VectorExtensions
{
    public static Vector3 Snap(this Vector3 value)
    {
        var steps = {|#0:new List<int>()|};
        return value;
    }
}

public class Grid : MonoBehaviour
{
    void Update()
    {
        var snapped = default(Vector3).Snap();
        Fill<string>();
    }

    void Fill<T>()
    {
        var cells = {|#1:new List<T>()|};
    }
}
";

            await VerifyAsync(source, Allocation("Snap"), Allocation("Fill", 1, "new List<T>"));
        }

        [Fact]
        public async Task PartialMethod_ReportsInTheImplementation()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public partial class Enemy : MonoBehaviour
{
    partial void Think();

    void Update() { Think(); }
}

public partial class Enemy
{
    partial void Think() { var plan = {|#0:new List<int>()|}; }
}
";

            await VerifyAsync(source, Allocation("Think"));
        }

        [Fact]
        public async Task CallsNotMadeEveryFrame_AreNotFollowed()
        {
            var source = @"
using System;
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    static bool s_warmed;
    Action _later;

    void Update()
    {
        if (Time.frameCount == 0) { FirstFrame(); }

        if (!s_warmed)
        {
            Warm();
            s_warmed = true;
        }

#if UNITY_EDITOR
        EditorOnly();
#endif

        _later = () => Deferred();
    }

    void FirstFrame() { var a = new List<int>(); }
    void Warm() { var b = new List<int>(); }
    void EditorOnly() { var c = new List<int>(); }
    void Deferred() { var d = new List<int>(); }
}
";

            var test = CreateTest<ObjectPoolAnalyzer>(source);
            test.SolutionTransforms.Add((solution, projectId) =>
            {
                var parseOptions = (CSharpParseOptions)solution.GetProject(projectId)!.ParseOptions!;
                return solution.WithProjectParseOptions(projectId, parseOptions.WithPreprocessorSymbols("UNITY_EDITOR"));
            });
            await test.RunAsync();
        }

        [Fact]
        public async Task GuardedCall_FollowedWhenTheSuppressionIsOff()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    void Update()
    {
        if (Time.frameCount == 0) { FirstFrame(); }
    }

    void FirstFrame() { var a = {|#0:new List<int>()|}; }
}
";

            await VerifyWithEditorConfigAsync(source, "object_pool_linter.suppressions = none", Allocation("FirstFrame"));
        }

        [Fact]
        public async Task ExcludedTypesAndPools_AreNotFollowed()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class DebugOverlay
{
    public void Draw() { var lines = new List<int>(); }
}

public static class BulletPool
{
    static readonly Stack<List<int>> s_free = new Stack<List<int>>();

    public static List<int> Get() => s_free.Count == 0 ? new List<int>() : s_free.Pop();
}

public class Gun : MonoBehaviour
{
    readonly DebugOverlay _overlay = new DebugOverlay();

    void Update()
    {
        _overlay.Draw();
        var bullet = BulletPool.Get();
    }
}
";

            await VerifyWithEditorConfigAsync(source, "object_pool_linter.excluded_types = DebugOverlay");
        }

        [Fact]
        public async Task StateMachinesAndLinqStyleHelpers_ReportOnlyTheCall()
        {
            var source = @"
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public static class Sequences
{
    public static int CountAll(this IEnumerable<int> source)
    {
        var count = 0;
        foreach (var item in source) count++;
        return count;
    }
}

public class Spawner : MonoBehaviour
{
    readonly List<int> _ids = new List<int>();

    void Update()
    {
        var waves = {|#0:Waves()|};
        var count = {|#1:_ids.CountAll()|};
        _ = {|#2:SaveAsync()|};
    }

    IEnumerator Waves()
    {
        var spawned = new List<int>();
        yield return null;
    }

    async Task SaveAsync()
    {
        await Task.Yield();
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(
                    source,
                    Hidden("iterator state machine for Waves()", 0),
                    Hidden("LINQ CountAll()", 1),
                    Hidden("async state machine for SaveAsync()", 2))
                .RunAsync();

            await VerifyAsync(source);
        }

        [Fact]
        public async Task Opl002AndOpl003_ReportInTheHelper()
        {
            var source = @"
using UnityEngine;

public class Hud : MonoBehaviour
{
    int _score;

    void LateUpdate()
    {
        Refresh();
    }

    void Refresh()
    {
        var label = {|#0:$""Score: {_score}""|};
        var cameras = {|#1:Camera.allCameras|};
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(source, Hidden("string interpolation", 0, "Refresh")).RunAsync();

            await CreateTest<UnityApiAllocationAnalyzer>(
                    source,
                    new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                        .WithLocation(1)
                        .WithArguments("Camera.allCameras", "Camera[]", "Refresh"))
                .RunAsync();
        }

        [Fact]
        public async Task InvalidMaxCallDepth_ReportsOpl004()
        {
            const string source = @"
public class Empty
{
}
";

            foreach (var value in new[] { "-1", "three", "2.5" })
            {
                await CreateTest<OptionsValidationAnalyzer>(
                        source,
                        new DiagnosticResult(OptionsValidationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                            .WithArguments($"'{value}' in 'object_pool_linter.max_call_depth' is not a whole number of 0 or more."))
                    .WithEditorConfig("object_pool_linter.max_call_depth = " + value)
                    .RunAsync();
            }
        }

        [Fact]
        public async Task Diagnostic_CarriesTheCallChain()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Enemy : MonoBehaviour
{
    readonly PathFinder _paths = new PathFinder();

    void Update()
    {
        var own = new List<int>();
        Spawn();
    }

    void Spawn() { _paths.Build(); }
}

public class PathFinder
{
    public void Build() { var open = new List<int>(); }
}
";

            var references = await TestReferenceAssemblies.Default.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);
            var compilation = CSharpCompilation.Create(
                "CallChain",
                new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(SharedUnityStub.Source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var diagnostics = await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ObjectPoolAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();

            var byMethod = diagnostics.ToDictionary(
                d => d.GetMessage().Split('\'')[3],
                d => d.Properties.TryGetValue("CallChain", out var chain) ? chain : null);

            Assert.Equal(2, byMethod.Count);
            Assert.Null(byMethod["Update"]);
            Assert.Equal("Enemy.Update -> Enemy.Spawn -> PathFinder.Build", byMethod["Build"]);
        }

        private static DiagnosticResult Hidden(string allocation, int location, string method = "Update")
        {
            return new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(allocation, method);
        }
    }
}
