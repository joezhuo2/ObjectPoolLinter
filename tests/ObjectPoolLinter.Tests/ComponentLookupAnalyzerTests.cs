using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F13: OPL009, GetComponent and scene searches in hot paths.
    public class ComponentLookupAnalyzerTests
    {
        private const string ComponentLookup = "looks up a component";
        private const string SceneSearch = "searches the scene";
        private const string ComponentAdvice = "Get it once in Awake or Start and keep it in a field";
        private const string SceneAdvice = "Find it once in Awake or Start and keep it in a field";

        private const string UnityStub = @"
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public static T FindObjectOfType<T>() where T : Object => null;
        public static T FindFirstObjectByType<T>() where T : Object => null;
        public static T FindAnyObjectByType<T>() where T : Object => null;
        public static T[] FindObjectsOfType<T>() where T : Object => null;
    }

    public class Component : Object
    {
        public GameObject gameObject => null;
        public Transform transform => null;
        public T GetComponent<T>() => default;
        public Component GetComponent(System.Type type) => null;
        public bool TryGetComponent<T>(out T component) { component = default; return false; }
        public T GetComponentInChildren<T>() => default;
        public T GetComponentInParent<T>() => default;
        public T[] GetComponents<T>() => null;
        public void GetComponents<T>(List<T> results) { }
    }

    public class GameObject : Object
    {
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
    public class Rigidbody : Component { }

    public struct RaycastHit
    {
        public Collider collider => null;
    }
}
";

        private static HotPathAnalyzerTest<ComponentLookupAnalyzer> CreateTest(string source, params DiagnosticResult[] expected)
        {
            return new HotPathAnalyzerTest<ComponentLookupAnalyzer>(source, UnityStub, expected);
        }

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            return CreateTest(source, expected).RunAsync();
        }

        private static DiagnosticResult Lookup(string api, int location = 0, string method = "Update") =>
            new DiagnosticResult(ComponentLookupAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(api, ComponentLookup, method, ComponentAdvice);

        private static DiagnosticResult Search(string api, int location = 0, string method = "Update") =>
            new DiagnosticResult(ComponentLookupAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(api, SceneSearch, method, SceneAdvice);

        [Fact]
        public void Descriptor_IsInfoWithHelpLink()
        {
            var descriptor = Assert.Single(new ComponentLookupAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL009", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Info, descriptor.DefaultSeverity);
            Assert.Equal("Performance", descriptor.Category);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL009.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task GetComponentOnThisObject_Reports()
        {
            var source = @"
using UnityEngine;

public class Mover : MonoBehaviour
{
    void Update()
    {
        var body = {|#0:GetComponent<Rigidbody>()|};
        var same = {|#1:this.GetComponent<Rigidbody>()|};
        var byType = {|#2:GetComponent(typeof(Rigidbody))|};
        var child = {|#3:GetComponentInChildren<Collider>()|};
        var parent = {|#4:GetComponentInParent<Rigidbody>()|};
        if ({|#5:TryGetComponent(out Rigidbody found)|}) { }
    }
}
";

            await VerifyAsync(
                source,
                Lookup("Component.GetComponent"),
                Lookup("Component.GetComponent", 1),
                Lookup("Component.GetComponent", 2),
                Lookup("Component.GetComponentInChildren", 3),
                Lookup("Component.GetComponentInParent", 4),
                Lookup("Component.TryGetComponent", 5));
        }

        [Fact]
        public async Task GetComponentThroughFieldsAndProperties_Reports()
        {
            var source = @"
using UnityEngine;

public class Follower : MonoBehaviour
{
    public Transform target;
    public static Follower Leader;

    void FixedUpdate()
    {
        var own = {|#0:gameObject.GetComponent<Rigidbody>()|};
        var up = {|#1:transform.parent.GetComponent<Rigidbody>()|};
        var targeted = {|#2:target.GetComponent<Collider>()|};
        var guarded = target?{|#3:.GetComponent<Collider>()|};
        var leader = {|#4:Leader.GetComponent<Rigidbody>()|};
    }
}
";

            await VerifyAsync(
                source,
                Lookup("GameObject.GetComponent", method: "FixedUpdate"),
                Lookup("Component.GetComponent", 1, "FixedUpdate"),
                Lookup("Component.GetComponent", 2, "FixedUpdate"),
                Lookup("Component.GetComponent", 3, "FixedUpdate"),
                Lookup("Component.GetComponent", 4, "FixedUpdate"));
        }

        // The object looked up changes from call to call, so there is nothing to cache.
        [Fact]
        public async Task GetComponentOnParameterOrLocal_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class Trigger : MonoBehaviour
{
    public Collider[] others;
    RaycastHit hit;

    void OnTriggerStay(Collider other)
    {
        var body = other.GetComponent<Rigidbody>();
        other.TryGetComponent(out Rigidbody found);
    }

    void Update()
    {
        var local = others[0];
        var fromLocal = local.GetComponent<Rigidbody>();
        var fromElement = others[1].GetComponent<Rigidbody>();
        var fromHit = hit.collider.GetComponent<Rigidbody>();
        var fromCall = Pick().GetComponent<Rigidbody>();
    }

    Collider Pick() => null;
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task SceneSearches_Report()
        {
            var source = @"
using UnityEngine;

public class Seeker : MonoBehaviour
{
    void Update()
    {
        var player = {|#0:GameObject.Find(""Player"")|};
        var tagged = {|#1:GameObject.FindWithTag(""Player"")|};
        var tagged2 = {|#2:GameObject.FindGameObjectWithTag(""Player"")|};
        var seeker = {|#3:FindObjectOfType<Seeker>()|};
        var first = {|#4:Object.FindFirstObjectByType<Seeker>()|};
        var any = {|#5:FindAnyObjectByType<Seeker>()|};
    }
}
";

            await VerifyAsync(
                source,
                Search("GameObject.Find"),
                Search("GameObject.FindWithTag", 1),
                Search("GameObject.FindGameObjectWithTag", 2),
                Search("Object.FindObjectOfType", 3),
                Search("Object.FindFirstObjectByType", 4),
                Search("Object.FindAnyObjectByType", 5));
        }

        // The array-returning lookups allocate and are OPL003's.
        [Fact]
        public async Task ArrayReturningLookups_DoNotReport()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    readonly List<Collider> buffer = new List<Collider>();

    void Update()
    {
        var all = GetComponents<Collider>();
        GetComponents(buffer);
        var tagged = GameObject.FindGameObjectsWithTag(""Enemy"");
        var scanners = FindObjectsOfType<Scanner>();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task ColdPathMethod_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class Mover : MonoBehaviour
{
    Rigidbody body;
    GameObject player;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        player = GameObject.Find(""Player"");
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task LookAlikeTypeOutsideUnityEngine_DoesNotReport()
        {
            var source = @"
namespace Game
{
    public static class GameObject
    {
        public static object Find(string name) => null;
    }

    public class Registry
    {
        public T GetComponent<T>() => default;
    }
}

public class Mover : UnityEngine.MonoBehaviour
{
    readonly Game.Registry registry = new Game.Registry();

    void Update()
    {
        var found = Game.GameObject.Find(""Player"");
        var entry = registry.GetComponent<int>();
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task AdditionalHotMethod_Reports()
        {
            var source = @"
using UnityEngine;

public class Mover : MonoBehaviour
{
    public void Tick()
    {
        var body = {|#0:GetComponent<Rigidbody>()|};
    }
}
";

            await CreateTest(source, Lookup("Component.GetComponent", method: "Tick"))
                .WithEditorConfig("object_pool_linter.additional_hot_methods = Tick")
                .RunAsync();
        }

        [Fact]
        public async Task ExcludedType_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class LoadingScreen : MonoBehaviour
{
    void Update()
    {
        var body = GetComponent<Rigidbody>();
        var player = GameObject.Find(""Player"");
    }
}
";

            await CreateTest(source)
                .WithEditorConfig("object_pool_linter.excluded_types = LoadingScreen")
                .RunAsync();
        }
    }
}
