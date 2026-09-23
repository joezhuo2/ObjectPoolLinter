using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F10: OPL007, native containers that are not disposed.
    public class NativeContainerDisposeAnalyzerTests
    {
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { }
}

namespace Unity.Jobs
{
    public struct JobHandle
    {
        public void Complete() { }
    }

    public interface IJob { void Execute(); }
}

namespace Unity.Collections.LowLevel.Unsafe
{
    [System.AttributeUsage(System.AttributeTargets.Struct)]
    public class NativeContainerAttribute : System.Attribute { }
}

namespace Unity.Collections
{
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Jobs;

    public enum Allocator { Invalid = 0, None = 1, Temp = 2, TempJob = 3, Persistent = 4 }

    public static class AllocatorManager
    {
        public struct AllocatorHandle
        {
            public static implicit operator AllocatorHandle(Allocator allocator) => default;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Field)]
    public class DeallocateOnJobCompletionAttribute : System.Attribute { }

    [NativeContainer]
    public struct NativeArray<T> : System.IDisposable where T : struct
    {
        public NativeArray(int length, Allocator allocator) { Length = length; }
        public NativeArray(T[] array, Allocator allocator) { Length = array.Length; }
        public int Length { get; }
        public bool IsCreated => true;
        public T this[int index] { get => default; set { } }
        public void Dispose() { }
        public JobHandle Dispose(JobHandle dependency) => dependency;
    }

    [NativeContainer]
    public struct NativeList<T> : System.IDisposable where T : unmanaged
    {
        public NativeList(AllocatorManager.AllocatorHandle allocator) { }
        public NativeList(int capacity, AllocatorManager.AllocatorHandle allocator) { }
        public void Add(in T value) { }
        public void Dispose() { }
    }
}
";

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            return new HotPathAnalyzerTest<NativeContainerDisposeAnalyzer>(source, UnityStub, expected).RunAsync();
        }

        private static DiagnosticResult Never(string type, string allocator, string method = "Update", int location = 0) =>
            new DiagnosticResult(NativeContainerDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(type, allocator, "in '" + method + "' is never disposed", "Call Dispose() on it, or declare it with 'using'");

        private static DiagnosticResult SomePaths(string type, string allocator, string method = "Update", int location = 0) =>
            new DiagnosticResult(NativeContainerDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(type, allocator, "in '" + method + "' is not disposed on every path out of the method", "Call Dispose() on it before each return, or declare it with 'using'");

        private static DiagnosticResult Field(string type, string allocator, string member, string owner, int location = 0) =>
            new DiagnosticResult(NativeContainerDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(type, allocator, "into '" + member + "' is never disposed by '" + owner + "'", "Call Dispose() on it in OnDestroy, OnDisable or Dispose");

        [Fact]
        public void Descriptor_IsWarningWithHelpLink()
        {
            var descriptor = Assert.Single(new NativeContainerDisposeAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL007", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
            Assert.Equal("Reliability", descriptor.Category);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL007.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task LocalNeverDisposed_Reports()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        var positions = {|#0:new NativeArray<int>(64, Allocator.TempJob)|};
        positions[0] = 1;

        NativeList<int> hits = {|#1:new(Allocator.Persistent)|};
        hits.Add(1);

        {|#2:new NativeArray<float>(8, Allocator.TempJob)|};
    }
}

// Not a hot path, and not a MonoBehaviour: a leak is a leak anywhere.
public class Loader
{
    public int Count(int[] source)
    {
        var copy = {|#3:new NativeArray<int>(source, Allocator.Persistent)|};
        return copy.Length;
    }
}
";

            await VerifyAsync(
                source,
                Never("NativeArray<int>", "TempJob"),
                Never("NativeList<int>", "Persistent", location: 1),
                new DiagnosticResult(NativeContainerDisposeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                    .WithLocation(2)
                    .WithArguments("NativeArray<float>", "TempJob", "in 'Update' is never disposed", "Keep it in a variable and call Dispose() on it"),
                Never("NativeArray<int>", "Persistent", "Count", 3));
        }

        [Fact]
        public async Task DisposedLocals_DoNotReport()
        {
            var source = @"
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    JobHandle Schedule(NativeArray<int> data) => default;

    void Update()
    {
        var a = new NativeArray<int>(64, Allocator.TempJob);
        a[0] = 1;
        a.Dispose();

        using var b = new NativeArray<int>(64, Allocator.TempJob);

        using (var c = new NativeArray<int>(64, Allocator.TempJob))
        {
            var first = c[0];
        }

        var d = new NativeArray<int>(64, Allocator.TempJob);
        var handle = Schedule(d);
        d.Dispose(handle);

        var e = new NativeArray<int>(64, Allocator.TempJob);
        try
        {
            if (e.Length > 3) return;
            e[0] = 2;
        }
        finally
        {
            e.Dispose();
        }

        var f = new NativeList<int>(4, Allocator.TempJob);
        if (f.Equals(null))
            f.Dispose();
        else
            f.Dispose();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task SelfReleasingOrUnknownAllocator_DoesNotReport()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    Allocator _allocator = Allocator.TempJob;

    void Update()
    {
        var temp = new NativeArray<int>(64, Allocator.Temp);
        var list = new NativeList<int>(Allocator.Temp);
        var none = new NativeArray<int>(0, Allocator.None);
        var configured = new NativeArray<int>(64, _allocator);
        var array = new int[4];
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task EarlyReturn_ReportsSomePaths()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    public int count;

    void Update()
    {
        var buffer = {|#0:new NativeArray<int>(count, Allocator.TempJob)|};
        if (count == 0) return;

        buffer[0] = count;
        buffer.Dispose();
    }

    void LateUpdate()
    {
        for (var i = 0; i < count; i++)
        {
            var scratch = {|#1:new NativeArray<int>(4, Allocator.TempJob)|};
            if (i % 2 == 0) continue;
            scratch.Dispose();
        }
    }
}
";

            await VerifyAsync(
                source,
                SomePaths("NativeArray<int>", "TempJob"),
                SomePaths("NativeArray<int>", "TempJob", "LateUpdate", 1));
        }

        [Fact]
        public async Task LoopReallocatingWithoutDispose_Reports()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        while (true)
        {
            var scratch = {|#0:new NativeArray<int>(4, Allocator.TempJob)|};
            scratch[0] = 1;
        }
    }
}
";

            await VerifyAsync(source, Never("NativeArray<int>", "TempJob"));
        }

        [Fact]
        public async Task ThrowPath_DoesNotCount()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    public int count;

    void Update()
    {
        var buffer = new NativeArray<int>(4, Allocator.TempJob);
        if (count < 0) throw new System.InvalidOperationException();

        buffer.Dispose();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task HandedOn_DoesNotReport()
        {
            var source = @"
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public struct SumJob : IJob
{
    public NativeArray<int> Values;
    public void Execute() { }
}

public class Spawner : MonoBehaviour
{
    NativeArray<int> _kept;

    NativeArray<int> Create()
    {
        var created = new NativeArray<int>(4, Allocator.Persistent);
        return created;
    }

    void Release(NativeArray<int> data) => data.Dispose();

    void Update()
    {
        var released = new NativeArray<int>(4, Allocator.TempJob);
        Release(released);

        var stored = new NativeArray<int>(4, Allocator.TempJob);
        _kept = stored;

        var job = new SumJob { Values = new NativeArray<int>(4, Allocator.TempJob) };

        var captured = new NativeArray<int>(4, Allocator.TempJob);
        System.Action later = () => captured.Dispose();
    }

    void OnDestroy() => _kept.Dispose();
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task PassedToLibraryMethod_StillReports()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        var buffer = {|#0:new NativeArray<int>(4, Allocator.TempJob)|};
        System.Console.WriteLine(buffer);
    }
}
";

            await VerifyAsync(source, Never("NativeArray<int>", "TempJob"));
        }

        [Fact]
        public async Task LocalFunction_ReportsWithItsName()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        Fill();

        void Fill()
        {
            var buffer = {|#0:new NativeArray<int>(4, Allocator.TempJob)|};
            buffer[0] = 1;
        }
    }
}
";

            await VerifyAsync(source, Never("NativeArray<int>", "TempJob", "Fill"));
        }

        [Fact]
        public async Task FieldNeverDisposed_Reports()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public class Spawner : MonoBehaviour
{
    NativeArray<int> _buffer;
    NativeList<int> Hits { get; set; }
    static NativeArray<int> s_shared = {|#2:new NativeArray<int>(4, Allocator.Persistent)|};

    void Awake()
    {
        _buffer = {|#0:new NativeArray<int>(64, Allocator.Persistent)|};
        this.Hits = {|#1:new NativeList<int>(Allocator.Persistent)|};
    }
}
";

            await VerifyAsync(
                source,
                Field("NativeArray<int>", "Persistent", "_buffer", "Spawner"),
                Field("NativeList<int>", "Persistent", "Hits", "Spawner", 1),
                Field("NativeArray<int>", "Persistent", "s_shared", "Spawner", 2));
        }

        [Fact]
        public async Task FieldDisposedByItsType_DoesNotReport()
        {
            var source = @"
using Unity.Collections;
using UnityEngine;

public partial class Spawner : MonoBehaviour
{
    NativeArray<int> _buffer;
    NativeArray<int> _other = new NativeArray<int>(4, Allocator.Persistent);
    NativeArray<int> _released;
    NativeArray<int> _temp;

    void OnEnable()
    {
        _buffer = new NativeArray<int>(64, Allocator.Persistent);
        _released = new NativeArray<int>(64, Allocator.Persistent);
        _temp = new NativeArray<int>(64, Allocator.Temp);
    }

    void OnDisable()
    {
        if (_buffer.IsCreated) _buffer.Dispose();
        Release(ref _released);
    }

    static void Release(ref NativeArray<int> data) => data.Dispose();
}

public partial class Spawner
{
    void OnDestroy() => _other.Dispose();
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task DeallocateOnJobCompletionField_DoesNotReport()
        {
            var source = @"
using Unity.Collections;
using Unity.Jobs;

public struct CopyJob : IJob
{
    [DeallocateOnJobCompletion] public NativeArray<int> Source;

    public void Prepare() => Source = new NativeArray<int>(4, Allocator.TempJob);

    public void Execute() { }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task WithoutUnityCollections_DoesNotRun()
        {
            var source = @"
public struct NativeArray
{
    public NativeArray(int length) { }
}

public class Spawner
{
    void Update()
    {
        var buffer = new NativeArray(4);
    }
}
";

            await new HotPathAnalyzerTest<NativeContainerDisposeAnalyzer>(source, "namespace UnityEngine { public class MonoBehaviour { } }").RunAsync();
        }
    }
}
