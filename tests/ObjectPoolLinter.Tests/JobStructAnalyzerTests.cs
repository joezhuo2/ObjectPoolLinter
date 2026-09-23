using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F9: OPL006, managed fields in Unity job structs.
    public class JobStructAnalyzerTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { }
    public struct Vector3 { public float x, y, z; }
}

namespace Unity.Jobs
{
    public interface IJob { void Execute(); }
    public interface IJobParallelFor { void Execute(int index); }
}

namespace Unity.Jobs.LowLevel.Unsafe
{
    [System.AttributeUsage(System.AttributeTargets.Interface)]
    public class JobProducerTypeAttribute : System.Attribute
    {
        public JobProducerTypeAttribute(System.Type producerType) { }
    }
}

namespace Unity.Collections.LowLevel.Unsafe
{
    [System.AttributeUsage(System.AttributeTargets.Struct)]
    public class NativeContainerAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Field)]
    public class NativeSetClassTypeToNullOnScheduleAttribute : System.Attribute { }

    public sealed class DisposeSentinel { }
}

namespace Unity.Collections
{
    using Unity.Collections.LowLevel.Unsafe;

    [NativeContainer]
    public struct NativeArray<T> where T : struct
    {
        [NativeSetClassTypeToNullOnSchedule]
        DisposeSentinel m_DisposeSentinel;
        int m_Length;
    }
}
";

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            return new HotPathAnalyzerTest<JobStructAnalyzer>(source, UnityStub, expected).RunAsync();
        }

        private static DiagnosticResult Diagnostic(string field, string job, string problem, int location = 0)
        {
            return new DiagnosticResult(JobStructAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(field, job, problem);
        }

        [Fact]
        public void Descriptor_IsWarningWithHelpLink()
        {
            var descriptor = Assert.Single(new JobStructAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL006", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL006.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task ReferenceTypeFields_Report()
        {
            var source = @"
using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

public struct MoveJob : IJob
{
    public string {|#0:Label|};
    public List<int> {|#1:Targets|};
    public float[] {|#2:Speeds|};
    public Action {|#3:Done|};
    public IComparable {|#4:Key|};
    public Transform2 {|#5:Target|} { get; set; }

    public int Count;
    public Vector3 Direction;
    public static string Shared;
    public const string Name = ""move"";
    public string Computed => Label;

    public void Execute() { }
}

public class Transform2 { }
";

            await VerifyAsync(
                source,
                Diagnostic("Label", "MoveJob", "has the reference type 'string'"),
                Diagnostic("Targets", "MoveJob", "has the reference type 'List<int>'", 1),
                Diagnostic("Speeds", "MoveJob", "has the reference type 'float[]'", 2),
                Diagnostic("Done", "MoveJob", "has the reference type 'Action'", 3),
                Diagnostic("Key", "MoveJob", "has the reference type 'IComparable'", 4),
                Diagnostic("Target", "MoveJob", "has the reference type 'Transform2'", 5));
        }

        [Fact]
        public async Task StructHoldingAReference_ReportsThePath()
        {
            var source = @"
using Unity.Jobs;

public struct Payload
{
    public int Id;
    public Inner Data;
}

public struct Inner
{
    public string Name;
}

public struct LoadJob : IJobParallelFor
{
    public Payload {|#0:Item|};
    public Inner? {|#1:Maybe|};

    public void Execute(int index) { }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Item", "LoadJob", "has the type 'Payload', which holds 'Data.Name' of reference type 'string'"),
                Diagnostic("Maybe", "LoadJob", "has the type 'Inner?', which holds 'value.Name' of reference type 'string'", 1));
        }

        [Fact]
        public async Task NativeContainersAndValueTypes_DoNotReport()
        {
            var source = @"
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

public enum Mode { A, B }

public unsafe struct SumJob : IJob
{
    public NativeArray<int> Values;
    public NativeArray<Vector3> Positions;
    public Mode Mode;
    public int* Cursor;
    public (int, float) Pair;

    [NativeSetClassTypeToNullOnSchedule]
    public object Scratch;

    public void Execute() { }
}
";

            var test = new HotPathAnalyzerTest<JobStructAnalyzer>(source, UnityStub);
            test.SolutionTransforms.Add((solution, projectId) =>
            {
                var project = solution.GetProject(projectId)!;
                var options = (Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions)project.CompilationOptions!;
                return solution.WithProjectCompilationOptions(projectId, options.WithAllowUnsafe(true));
            });
            await test.RunAsync();
        }

        [Fact]
        public async Task GenericFields_ReportOnlyWhenConstrainedToAClass()
        {
            var source = @"
using Unity.Jobs;

public struct CopyJob<TValue, TRef> : IJob
    where TRef : class
{
    public TValue Value;
    public TRef {|#0:Reference|};

    public void Execute() { }
}
";

            await VerifyAsync(source, Diagnostic("Reference", "CopyJob", "has the reference type 'TRef'"));
        }

        [Fact]
        public async Task CustomJobInterfaceMarkedJobProducerType_Reports()
        {
            var source = @"
using Unity.Jobs.LowLevel.Unsafe;

[JobProducerType(typeof(BatchJobProducer))]
public interface IBatchJob
{
    void Execute(int start, int count);
}

public static class BatchJobProducer { }

public struct CountJob : IBatchJob
{
    public string {|#0:Label|};

    public void Execute(int start, int count) { }
}
";

            await VerifyAsync(source, Diagnostic("Label", "CountJob", "has the reference type 'string'"));
        }

        [Fact]
        public async Task NonJobTypes_DoNotReport()
        {
            var source = @"
using Unity.Jobs;

public struct Settings
{
    public string Label;
}

public class ManagedJob : IJob
{
    public string Label;

    public void Execute() { }
}

namespace Other
{
    public interface IJob { }

    public struct FakeJob : IJob
    {
        public string Label;
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task WithoutUnityJobs_DoesNotRun()
        {
            var test = new HotPathAnalyzerTest<JobStructAnalyzer>(@"
public interface IJob { void Execute(); }

public struct MoveJob : IJob
{
    public string Label;
    public void Execute() { }
}
", @"
namespace UnityEngine
{
    public class MonoBehaviour { }
}
");
            await test.RunAsync();
        }
    }
}
