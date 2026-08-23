// Minimal Unity stubs so the analyzer can resolve UnityEngine types
// without needing the actual Unity assemblies.
namespace UnityEngine
{
    public class Object
    {
        public static Object Instantiate(Object original) => null;
        public static Object Instantiate(Object original, Vector3 position, Quaternion rotation) => null;
    }

    public struct Vector3 { }
    public struct Quaternion { }

    public class MonoBehaviour : Object
    {
    }
}