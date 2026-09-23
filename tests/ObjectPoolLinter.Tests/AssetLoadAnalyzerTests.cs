using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // F11 and F12: OPL008, Resources.Load and Addressables loads in hot paths.
    public class AssetLoadAnalyzerTests
    {
        private const string ResourcesAdvice = "Load it once in Awake or Start and keep it in a field";
        private const string AddressablesAdvice = "Load it once in Awake or Start and keep the handle or its result in a field";

        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object { }
    public class GameObject : Object { }
    public class Texture2D : Object { }
    public class MonoBehaviour : Object { }
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

namespace UnityEngine.ResourceManagement.AsyncOperations
{
    public struct AsyncOperationHandle<T> { }
    public struct AsyncOperationHandle { }
}

namespace UnityEngine.AddressableAssets
{
    using System.Collections.Generic;
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
";

        private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
        {
            return new HotPathAnalyzerTest<AssetLoadAnalyzer>(source, UnityStub, expected).RunAsync();
        }

        private static DiagnosticResult Diagnostic(string api, string advice, string method = "Update", int location = 0) =>
            new DiagnosticResult(AssetLoadAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(location)
                .WithArguments(api, method, advice);

        [Fact]
        public void Descriptor_IsInfoWithHelpLink()
        {
            var descriptor = Assert.Single(new AssetLoadAnalyzer().SupportedDiagnostics);

            Assert.Equal("OPL008", descriptor.Id);
            Assert.Equal(DiagnosticSeverity.Info, descriptor.DefaultSeverity);
            Assert.Equal("Performance", descriptor.Category);
            Assert.Equal(
                "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL008.md",
                descriptor.HelpLinkUri);
        }

        [Fact]
        public async Task ResourcesLoadInHotPath_Reports()
        {
            var source = @"
using UnityEngine;

public class Spawner : MonoBehaviour
{
    void Update()
    {
        var prefab = {|#0:Resources.Load<GameObject>(""Enemy"")|};
        var any = {|#1:Resources.Load(""Enemy"")|};
        var request = {|#2:Resources.LoadAsync<Texture2D>(""Sky"")|};
        var untyped = {|#3:Resources.LoadAsync(""Sky"")|};
    }

    void FixedUpdate()
    {
        var all = Resources.LoadAll(""Enemies"");
        Resources.UnloadAsset(null);
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Resources.Load", ResourcesAdvice),
                Diagnostic("Resources.Load", ResourcesAdvice, location: 1),
                Diagnostic("Resources.LoadAsync", ResourcesAdvice, location: 2),
                Diagnostic("Resources.LoadAsync", ResourcesAdvice, location: 3));
        }

        [Fact]
        public async Task AddressablesLoadInHotPath_Reports()
        {
            var source = @"
using UnityEngine;
using UnityEngine.AddressableAssets;

public class Spawner : MonoBehaviour
{
    public AssetReference icon;
    public AssetReferenceGameObject enemy;

    void Update()
    {
        var handle = {|#0:Addressables.LoadAssetAsync<GameObject>(""Enemy"")|};
        var many = {|#1:Addressables.LoadAssetsAsync<GameObject>(""Enemies"", null)|};
        var scene = {|#2:Addressables.LoadSceneAsync(""Level2"")|};
        var sprite = {|#3:icon.LoadAssetAsync<Texture2D>()|};
        var typed = {|#4:enemy.LoadAssetAsync()|};

        var spawned = Addressables.InstantiateAsync(""Enemy"");
        Addressables.Release(handle);
        icon.ReleaseAsset();
    }
}
";

            await VerifyAsync(
                source,
                Diagnostic("Addressables.LoadAssetAsync", AddressablesAdvice),
                Diagnostic("Addressables.LoadAssetsAsync", AddressablesAdvice, location: 1),
                Diagnostic("Addressables.LoadSceneAsync", AddressablesAdvice, location: 2),
                Diagnostic("AssetReference.LoadAssetAsync", AddressablesAdvice, location: 3),
                Diagnostic("AssetReferenceT.LoadAssetAsync", AddressablesAdvice, location: 4));
        }

        [Fact]
        public async Task LoadOutsideHotPath_DoesNotReport()
        {
            var source = @"
using UnityEngine;
using UnityEngine.AddressableAssets;

public class Spawner : MonoBehaviour
{
    GameObject _prefab;

    void Awake()
    {
        _prefab = Resources.Load<GameObject>(""Enemy"");
    }

    void Start()
    {
        Addressables.LoadAssetAsync<GameObject>(""Enemy"");
    }
}

public class Loader
{
    void Update()
    {
        var prefab = Resources.Load<GameObject>(""Enemy"");
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task LookalikeTypes_DoNotReport()
        {
            var source = @"
using UnityEngine;

namespace Game
{
    public static class Resources
    {
        public static Object Load(string path) => null;
    }

    public class Spawner : MonoBehaviour
    {
        void Update()
        {
            var prefab = Resources.Load(""Enemy"");
        }
    }
}
";

            await VerifyAsync(source);
        }

        [Fact]
        public async Task AdditionalHotMethodAndExcludedType_AreHonoured()
        {
            var source = @"
using UnityEngine;

public class Simulation
{
    public void Tick()
    {
        var prefab = {|#0:Resources.Load<GameObject>(""Enemy"")|};
    }
}

public class LoadingScreen : MonoBehaviour
{
    void Update()
    {
        var prefab = Resources.Load<GameObject>(""Enemy"");
    }
}
";

            await new HotPathAnalyzerTest<AssetLoadAnalyzer>(source, UnityStub, Diagnostic("Resources.Load", ResourcesAdvice, "Tick"))
                .WithEditorConfig("object_pool_linter.additional_hot_methods = Tick\nobject_pool_linter.excluded_types = LoadingScreen")
                .RunAsync();
        }

        [Fact]
        public async Task WithoutUnityAssetApis_DoesNotRun()
        {
            var source = @"
public class Spawner : UnityEngine.MonoBehaviour
{
    void Update() { }
}
";

            await new HotPathAnalyzerTest<AssetLoadAnalyzer>(source, "namespace UnityEngine { public class MonoBehaviour { } }").RunAsync();
        }
    }
}
