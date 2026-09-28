namespace ObjectPoolLinter.Tests
{
    // The Unity API surface every test compiles against. It is the union of what the tests need: the
    // UnityEngine core types, the array-returning members OPL003 knows, the lookups OPL009 knows, the asset
    // loaders OPL008 knows, Burst, the job system and the native containers. Only signatures matter, so
    // bodies return null or default. Some tests compile it under C# 9, so it uses no newer syntax.
    //
    // A test that needs a different Unity surface on purpose (no UnityEngine.Object, a MonoBehaviour that
    // is not in UnityEngine, a look-alike type in another namespace) passes its own minimal stub or
    // declares the look-alike in its source; it never adds members to this one's types.
    internal static class SharedUnityStub
    {
        public const string Source = @"
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get => null; set { } }
        public static Object Instantiate(Object original) => null;
        public static Object Instantiate(Object original, Vector3 position, Quaternion rotation) => null;
        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : Object => null;
        public static T FindObjectOfType<T>() where T : Object => null;
        public static T FindFirstObjectByType<T>() where T : Object => null;
        public static T FindAnyObjectByType<T>() where T : Object => null;
        public static T[] FindObjectsOfType<T>() where T : Object => null;
    }

    public class Component : Object
    {
        public string tag { get => null; set { } }
        public bool CompareTag(string tag) => false;
        public GameObject gameObject => null;
        public Transform transform => null;
        public T GetComponent<T>() => default;
        public Component GetComponent(System.Type type) => null;
        public bool TryGetComponent<T>(out T component) { component = default; return false; }
        public T GetComponentInChildren<T>() => default;
        public T GetComponentInParent<T>() => default;
        public T[] GetComponents<T>() => null;
        public void GetComponents<T>(List<T> results) { }
        public T[] GetComponentsInChildren<T>() => null;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => null;
        public void GetComponentsInChildren<T>(List<T> results) { }
        public void GetComponentsInChildren<T>(bool includeInactive, List<T> results) { }
        public T[] GetComponentsInParent<T>() => null;
        public void GetComponentsInParent<T>(bool includeInactive, List<T> results) { }
    }

    public class GameObject : Object
    {
        public string tag { get => null; set { } }
        public bool CompareTag(string tag) => false;
        public GameObject gameObject => this;
        public Transform transform => null;
        public T GetComponent<T>() => default;
        public static GameObject Find(string name) => null;
        public static GameObject FindWithTag(string tag) => null;
        public static GameObject FindGameObjectWithTag(string tag) => null;
        public static GameObject[] FindGameObjectsWithTag(string tag) => null;
    }

    public class Transform : Component
    {
        public Transform parent => null;
    }

    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class Collider : Component { }
    public class Collider2D : Behaviour { }
    public class Rigidbody : Component { }
    public class Collision { }
    public class Collision2D { }

    public struct Vector2 { public float x, y; }
    public struct Vector3 { public float x, y, z; }
    public struct Quaternion { }
    public struct Ray { }
    public struct Touch { public Vector2 position; }

    public struct RaycastHit
    {
        public Collider collider => null;
    }

    public static class Physics
    {
        public static RaycastHit[] RaycastAll(Ray ray) => null;
        public static int RaycastNonAlloc(Ray ray, RaycastHit[] results) => 0;
    }

    public class Camera : Behaviour
    {
        public static Camera[] allCameras => null;
        public static Camera main => null;
    }

    public static class Input
    {
        public static Touch[] touches => null;
        public static int touchCount => 0;
        public static Touch GetTouch(int index) => default;
    }

    public static class Time
    {
        public static int frameCount => 0;
    }

    public class Mesh : Object
    {
        public Vector3[] vertices { get => null; set { } }
    }

    public class Texture2D : Object { }
    public class Material : Object { }

    public class Renderer : Component
    {
        public Material[] materials { get => null; set { } }
        public Material[] sharedMaterials { get => null; set { } }
        public Material sharedMaterial { get => null; set { } }
        public void GetSharedMaterials(List<Material> m) { }
    }

    public class AnimatorControllerParameter { }

    public class Animator : Behaviour
    {
        public AnimatorControllerParameter[] parameters => null;
        public int parameterCount => 0;
        public int layerCount => 0;
        public AnimatorControllerParameter GetParameter(int index) => null;
        public string GetLayerName(int layerIndex) => null;
        public int GetLayerIndex(string layerName) => 0;
    }

    public static class Application
    {
        public static string dataPath => null;
        public static string persistentDataPath => null;
        public static string streamingAssetsPath => null;
        public static string temporaryCachePath => null;
        public static bool isPlaying => false;
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj) => null;
        public static T FromJson<T>(string json) => default;
        public static void FromJsonOverwrite(string json, object objectToOverwrite) { }
    }

    public class ResourceRequest { }

    public static class Resources
    {
        public static Object Load(string path) => null;
        public static T Load<T>(string path) where T : Object => null;
        public static ResourceRequest LoadAsync(string path) => null;
        public static ResourceRequest LoadAsync<T>(string path) where T : Object => null;
        public static Object[] LoadAll(string path) => null;
        public static void UnloadAsset(Object asset) { }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name => null;
        public string path => null;
        public int buildIndex => 0;
        public UnityEngine.GameObject[] GetRootGameObjects() => null;
        public void GetRootGameObjects(List<UnityEngine.GameObject> rootGameObjects) { }
    }

    public static class SceneManager
    {
        public static Scene GetActiveScene() => default;
    }
}

namespace UnityEngine.AI
{
    public class NavMeshPath
    {
        public UnityEngine.Vector3[] corners => null;
        public int GetCornersNonAlloc(UnityEngine.Vector3[] results) => 0;
    }

    public class NavMeshAgent : UnityEngine.Behaviour
    {
        public NavMeshPath path { get => null; set { } }
        public bool CalculatePath(UnityEngine.Vector3 target, NavMeshPath path) => false;
    }
}

namespace UnityEngine.UI
{
    public class Dropdown : UnityEngine.MonoBehaviour
    {
        public string[] labels => null;
    }
}

namespace UnityEngine.ResourceManagement.AsyncOperations
{
    public struct AsyncOperationHandle<T> { }
    public struct AsyncOperationHandle { }
}

namespace UnityEngine.AddressableAssets
{
    using UnityEngine.ResourceManagement.AsyncOperations;

    public static class Addressables
    {
        public static AsyncOperationHandle<T> LoadAssetAsync<T>(object key) => default;
        public static AsyncOperationHandle<IList<T>> LoadAssetsAsync<T>(object key, System.Action<T> callback) => default;
        public static AsyncOperationHandle LoadSceneAsync(object key) => default;
        public static AsyncOperationHandle<GameObject> InstantiateAsync(object key) => default;
        public static void Release<T>(AsyncOperationHandle<T> handle) { }
    }

    public class AssetReference
    {
        public virtual AsyncOperationHandle<T> LoadAssetAsync<T>() => default;
        public virtual void ReleaseAsset() { }
    }

    public class AssetReferenceT<TObject> : AssetReference where TObject : Object
    {
        public AsyncOperationHandle<TObject> LoadAssetAsync() => default;
    }

    public class AssetReferenceGameObject : AssetReferenceT<GameObject> { }
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
    public struct JobHandle
    {
        public void Complete() { }
    }

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
        [NativeSetClassTypeToNullOnSchedule]
        DisposeSentinel m_DisposeSentinel;
        int m_Length;

        public NativeArray(int length, Allocator allocator) { m_DisposeSentinel = null; m_Length = length; }
        public NativeArray(T[] array, Allocator allocator) { m_DisposeSentinel = null; m_Length = array.Length; }
        public int Length => m_Length;
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
    }
}
