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
}