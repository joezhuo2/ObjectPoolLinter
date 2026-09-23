using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F8: code Burst compiles cannot allocate managed memory, so no hot-path rule reports inside it.
    public class BurstCompileTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { }
}

namespace Unity.Burst
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Method)]
    public class BurstCompileAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class BurstDiscardAttribute : System.Attribute { }
}

namespace Unity.Jobs
{
    public interface IJob { void Execute(); }
}
";

        private static HotPathAnalyzerTest<TAnalyzer> CreateTest<TAnalyzer>(string source, string editorConfig, params DiagnosticResult[] expected)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            return new HotPathAnalyzerTest<TAnalyzer>(source, UnityStub, expected).WithEditorConfig(editorConfig);
        }

        private static DiagnosticResult Allocation(string allocation, string method, int location = 0)
        {
            return new DiagnosticResult(ObjectPoolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(allocation, method);
        }

        private static DiagnosticResult Hidden(string allocation, string method, int location = 0)
        {
            return new DiagnosticResult(HiddenAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(allocation, method);
        }

        [Fact]
        public async Task BurstJobStruct_DoesNotReport()
        {
            var source = @"
using Unity.Burst;
using Unity.Jobs;

[BurstCompile]
public struct MoveJob : IJob
{
    public int Count;

    public void Execute()
    {
        var buffer = new int[Count];
        var label = ""n"" + Count;
    }
}

public struct PlainJob : IJob
{
    public int Count;

    public void Execute()
    {
        var buffer = {|#0:new int[Count]|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, "object_pool_linter.additional_hot_methods = Execute",
                    Allocation("new int[]", "Execute"))
                .RunAsync();

            await CreateTest<HiddenAllocationAnalyzer>(source.Replace("{|#0:", "").Replace("]|}", "]"),
                    "object_pool_linter.additional_hot_methods = Execute")
                .RunAsync();
        }

        [Fact]
        public async Task BurstStaticMethodAndSystemStruct_DoNotReport()
        {
            var source = @"
using Unity.Burst;

[BurstCompile]
public static class Physics2
{
    [BurstCompile]
    public static void Simulate(int count)
    {
        var buffer = new float[count];
    }

    public static void Tick(int count)
    {
        var buffer = new float[count];
    }
}

public struct SpawnSystem
{
    [BurstCompile]
    public void OnUpdate()
    {
        var buffer = new int[4];
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source,
                    "object_pool_linter.additional_hot_methods = Simulate, Tick, OnUpdate")
                .RunAsync();
        }

        [Fact]
        public async Task BurstCompileOnMonoBehaviour_StillReports()
        {
            var source = @"
using Unity.Burst;
using UnityEngine;

[BurstCompile]
public class Mover : MonoBehaviour
{
    [BurstCompile]
    void Update()
    {
        var buffer = {|#0:new int[4]|};
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, string.Empty, Allocation("new int[]", "Update"))
                .RunAsync();
        }

        [Fact]
        public async Task BurstDiscardMethod_StillReports()
        {
            var source = @"
using Unity.Burst;
using Unity.Jobs;

[BurstCompile]
public struct LogJob : IJob
{
    public int Count;

    public void Execute() { }

    [BurstDiscard]
    public void Log()
    {
        var text = {|#0:""n"" + Count|};
    }
}
";

            await CreateTest<HiddenAllocationAnalyzer>(source, "object_pool_linter.additional_hot_methods = Log",
                    Hidden("string concatenation", "Log"))
                .RunAsync();
        }

        [Fact]
        public async Task LocalFunctionInsideBurstMethod_DoesNotReport()
        {
            var source = @"
using Unity.Burst;
using Unity.Jobs;

[BurstCompile]
public struct SumJob : IJob
{
    public void Execute()
    {
        int[] Make() => new int[2];
        Make();
    }
}
";

            await CreateTest<ObjectPoolAnalyzer>(source, "object_pool_linter.additional_hot_methods = Execute")
                .RunAsync();
        }
    }
}
