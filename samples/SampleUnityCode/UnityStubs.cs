// Minimal Unity stubs so the analyzer can resolve UnityEngine types
// without needing the actual Unity assemblies.
namespace UnityEngine
{
    public class Object
    {
        public static Object Instantiate(Object original) => null!;
        public static Object Instantiate(Object original, Vector3 position, Quaternion rotation) => null!;
    }

    public struct Vector3 { }
    public struct Quaternion { }

    public class Component : Object
    {
    }

    public class Behaviour : Component
    {
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public class Camera : Behaviour
    {
        public static Camera[] allCameras => System.Array.Empty<Camera>();
    }

    public static class Time
    {
        public static int frameCount => 0;
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null!;
    }
}

namespace Unity.Collections.LowLevel.Unsafe
{
    [System.AttributeUsage(System.AttributeTargets.Struct)]
    public class NativeContainerAttribute : System.Attribute
    {
    }
}

namespace Unity.Collections
{
    public enum Allocator
    {
        Invalid = 0,
        None = 1,
        Temp = 2,
        TempJob = 3,
        Persistent = 4,
    }

    [Unity.Collections.LowLevel.Unsafe.NativeContainer]
    public struct NativeArray<T> : System.IDisposable where T : struct
    {
        public NativeArray(int length, Allocator allocator)
        {
            Length = length;
        }

        public int Length { get; }

        public T this[int index]
        {
            get => default;
            set { }
        }

        public void Dispose()
        {
        }
    }
}

namespace Unity.Burst
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Method)]
    public class BurstCompileAttribute : System.Attribute
    {
    }
}

namespace Unity.Jobs
{
    public interface IJob
    {
        void Execute();
    }
}
