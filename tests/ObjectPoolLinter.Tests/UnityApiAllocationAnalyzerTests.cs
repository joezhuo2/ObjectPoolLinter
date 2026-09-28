using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    public class UnityApiAllocationAnalyzerTests
    {
        private static HotPathAnalyzerTest<UnityApiAllocationAnalyzer> CreateTest(string source, params DiagnosticResult[] expected)
        {
            return new HotPathAnalyzerTest<UnityApiAllocationAnalyzer>(source, SharedUnityStub.Source, expected);
        }

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            return CreateTest(source, expected).RunAsync();
        }

        private static DiagnosticResult Diagnostic(string api, string returned, string method = "Update", int location = 0)
        {
            return new DiagnosticResult(UnityApiAllocationAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(location)
                .WithArguments(api, returned, method);
        }

        [Fact]
        public void Descriptor_IsWarningWithHelpLink()
        {
            var descriptor = Assert.Single(new UnityApiAllocationAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL003", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL003.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task ArrayReturningMethods_Report()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Ray ray;

    void Update()
    {
        var colliders = {|#0:GetComponentsInChildren<Collider>()|};
        var hits = {|#1:Physics.RaycastAll(ray)|};
        var all = {|#2:Object.FindObjectsOfType<Scanner>()|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Component.GetComponentsInChildren", "Collider[]"),
                Diagnostic("Physics.RaycastAll", "RaycastHit[]", location: 1),
                Diagnostic("Object.FindObjectsOfType", "Scanner[]", location: 2));
        }

        [Fact]
        public async Task NonAllocatingOverloads_DoNotReport()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Scanner : MonoBehaviour
{
    Ray ray;
    readonly RaycastHit[] hits = new RaycastHit[16];
    readonly List<Collider> colliders = new List<Collider>();

    void Update()
    {
        GetComponentsInChildren(colliders);
        var count = Physics.RaycastNonAlloc(ray, hits);
        var touches = Input.touchCount;
        var main = Camera.main;
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task ArrayReturningProperties_Report()
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    public Mesh mesh;

    void Update()
    {
        var cameras = {|#0:Camera.allCameras|};
        var touches = {|#1:Input.touches|};
        var vertices = {|#2:mesh.vertices|};
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Camera.allCameras", "Camera[]"),
                Diagnostic("Input.touches", "Touch[]", location: 1),
                Diagnostic("Mesh.vertices", "Vector3[]", location: 2));
        }

        [Fact]
        public async Task PropertyWrite_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class Deformer : MonoBehaviour
{
    public Mesh mesh;
    readonly Vector3[] buffer = new Vector3[8];

    void Update()
    {
        mesh.vertices = buffer;
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task NameAndTag_Report()
        {
            var source = @"
using UnityEngine;

public class Tagged : MonoBehaviour
{
    void Update()
    {
        var label = {|#0:name|};
        var isPlayer = {|#1:tag|} == ""Player"";
        var better = CompareTag(""Player"");
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Object.name", "string"),
                Diagnostic("Component.tag", "string", location: 1));
        }

        // F14: `sharedMaterials` does not instantiate materials the way `materials` does, but its getter
        // still returns a new copy of the array on every read. Only the singular `sharedMaterial` and
        // `GetSharedMaterials(List<Material>)` are free.
        [Fact]
        public async Task SharedMaterials_StillReports()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;

public class Tint : MonoBehaviour
{
    public Renderer target;
    readonly List<Material> buffer = new List<Material>();

    void Update()
    {
        var copies = {|#0:target.materials|};
        var shared = {|#1:target.sharedMaterials|};
        var first = target.sharedMaterial;
        target.GetSharedMaterials(buffer);
        target.sharedMaterials = shared;
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Renderer.materials", "Material[]"),
                Diagnostic("Renderer.sharedMaterials", "Material[]", location: 1));
        }

        // F15: `Animator.parameters` is an array property and was already matched; `GetParameter(i)`
        // reads it internally and `GetLayerName` builds a string. `layerCount` and `parameterCount` are
        // plain ints.
        [Fact]
        public async Task AnimatorMembers_Report()
        {
            var source = @"
using UnityEngine;

public class Rig : MonoBehaviour
{
    public Animator animator;

    void Update()
    {
        var all = {|#0:animator.parameters|};
        var first = {|#1:animator.GetParameter(0)|};
        var layer = {|#2:animator.GetLayerName(0)|};
        var layers = animator.layerCount;
        var count = animator.parameterCount;
        var index = animator.GetLayerIndex(""Base"");
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Animator.parameters", "AnimatorControllerParameter[]"),
                Diagnostic("Animator.GetParameter", "AnimatorControllerParameter", location: 1),
                Diagnostic("Animator.GetLayerName", "string", location: 2));
        }

        [Fact]
        public async Task ApplicationPathsAndJsonUtility_Report()
        {
            var source = @"
using UnityEngine;

public class Saver : MonoBehaviour
{
    public Saver state;

    void Update()
    {
        var data = {|#0:Application.dataPath|};
        var saves = {|#1:Application.persistentDataPath|};
        var streaming = {|#2:Application.streamingAssetsPath|};
        var temp = {|#3:Application.temporaryCachePath|};
        var playing = Application.isPlaying;

        var json = {|#4:JsonUtility.ToJson(state)|};
        var copy = {|#5:JsonUtility.FromJson<Saver>(json)|};
        JsonUtility.FromJsonOverwrite(json, state);
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Application.dataPath", "string"),
                Diagnostic("Application.persistentDataPath", "string", location: 1),
                Diagnostic("Application.streamingAssetsPath", "string", location: 2),
                Diagnostic("Application.temporaryCachePath", "string", location: 3),
                Diagnostic("JsonUtility.ToJson", "string", location: 4),
                Diagnostic("JsonUtility.FromJson", "Saver", location: 5));
        }

        // F15: UnityEngine.SceneManagement and UnityEngine.AI are native bindings too.
        [Fact]
        public async Task SceneManagementAndNavMeshMembers_Report()
        {
            var source = @"
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public class Navigator : MonoBehaviour
{
    public NavMeshAgent agent;
    readonly NavMeshPath reused = new NavMeshPath();
    readonly Vector3[] corners = new Vector3[16];
    readonly List<GameObject> roots = new List<GameObject>();

    void Update()
    {
        var scene = SceneManager.GetActiveScene();
        var sceneName = {|#0:scene.name|};
        var scenePath = {|#1:scene.path|};
        var all = {|#2:scene.GetRootGameObjects()|};
        scene.GetRootGameObjects(roots);
        var index = scene.buildIndex;

        var path = {|#3:agent.path|};
        var points = {|#4:reused.corners|};
        agent.CalculatePath(default, reused);
        var count = reused.GetCornersNonAlloc(corners);
        agent.path = reused;
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Scene.name", "string"),
                Diagnostic("Scene.path", "string", location: 1),
                Diagnostic("Scene.GetRootGameObjects", "GameObject[]", location: 2),
                Diagnostic("NavMeshAgent.path", "NavMeshPath", location: 3),
                Diagnostic("NavMeshPath.corners", "Vector3[]", location: 4));
        }

        [Fact]
        public async Task ManagedUnityPackageMembers_DoNotReport()
        {
            var source = @"
using UnityEngine;
using UnityEngine.UI;

public class Menu : MonoBehaviour
{
    public Dropdown dropdown;

    void Update()
    {
        var labels = dropdown.labels;
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task LookAlikeTypeOutsideUnityEngine_DoesNotReport()
        {
            var source = @"
namespace Game.UnityEngine
{
    public static class Physics
    {
        public static int[] RaycastAll() => null;
    }
}

public class Scanner : UnityEngine.MonoBehaviour
{
    void Update()
    {
        var hits = Game.UnityEngine.Physics.RaycastAll();
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

public class Scanner : MonoBehaviour
{
    void Start()
    {
        var colliders = GetComponentsInChildren<Collider>();
        var cameras = Camera.allCameras;
    }
}
";

            await VerifyAsync(source);
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
        var cameras = Camera.allCameras;
    }
}
";

            await CreateTest(source)
                .WithEditorConfig("object_pool_linter.excluded_types = LoadingScreen")
                .RunAsync();
        }

        // T27: an allocating property read is reported only in a hot path. Startup and teardown
        // messages, a trigger message that runs once per contact, a helper that is not a Unity message,
        // and an `Update` overload whose signature Unity does not call all read it without a report.
        [Theory]
        [InlineData("void Awake()")]
        [InlineData("void Start()")]
        [InlineData("void OnEnable()")]
        [InlineData("void OnDisable()")]
        [InlineData("void OnDestroy()")]
        [InlineData("void OnTriggerEnter(Collider other)")]
        [InlineData("void Refresh()")]
        [InlineData("void Update(int frame)")]
        public async Task ColdPathPropertyRead_DoesNotReport(string signature)
        {
            var source = @"
using UnityEngine;

public class Scanner : MonoBehaviour
{
    public Mesh mesh;

    " + signature + @"
    {
        var cameras = Camera.allCameras;
        var touches = Input.touches;
        var vertices = mesh.vertices;
        var isPlayer = tag == ""Player"";
    }
}
";

            await VerifyAsync(source);
        }

        // T28: `gameObject` and `transform` return an existing object, not a new array or string, and
        // are not in the list of known allocating getters.
        [Theory]
        [InlineData("var go = gameObject;")]
        [InlineData("var t = transform;")]
        [InlineData("var t = gameObject.transform;")]
        [InlineData("var go = transform.gameObject;")]
        [InlineData("var t = other.transform;")]
        [InlineData("var go = other.gameObject;")]
        [InlineData("var p = transform.parent;")]
        [InlineData("var main = Camera.main;")]
        public async Task SingleObjectProperty_DoesNotReport(string statement)
        {
            var source = @"
using UnityEngine;

public class Follower : MonoBehaviour
{
    public Collider other;

    void Update()
    {
        " + statement + @"
    }
}
";

            await VerifyAsync(source);
        }

        // T29: `GameObject.tag` is marshalled from native code like `Component.tag`, and reported under
        // its own type name. Writing it and `CompareTag` are not reported.
        [Fact]
        public async Task GameObjectTag_Reports()
        {
            var source = @"
using UnityEngine;

public class Tagged : MonoBehaviour
{
    public GameObject target;

    void Update()
    {
        var mine = {|#0:gameObject.tag|};
        var isEnemy = {|#1:target.tag|} == ""Enemy"";
        var better = target.CompareTag(""Enemy"");
        target.tag = ""Untagged"";
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("GameObject.tag", "string"),
                Diagnostic("GameObject.tag", "string", location: 1));
        }

        [Fact]
        public async Task GameObjectTag_InColdPath_DoesNotReport()
        {
            var source = @"
using UnityEngine;

public class Tagged : MonoBehaviour
{
    void Start()
    {
        var mine = gameObject.tag;
    }
}
";

            await VerifyAsync(source);
        }
    }
}
