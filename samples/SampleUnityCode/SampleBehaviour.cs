using UnityEngine;

public class PlayerBehaviour : MonoBehaviour
{
    public UnityEngine.Object prefab = null!;
    public int hp = 100;

    // HOT PATH - should trigger OPL001, OPL002 and OPL003 warnings
    void Update()
    {
        // OPL002 (raised to a warning in .editorconfig): string interpolation in Update
        var label = $"hp {hp}";

        // OPL003: Camera.allCameras returns a new array in Update
        var cameras = Camera.allCameras;

        // Warning: List<int> allocated in Update
        var list = new System.Collections.Generic.List<int>();
        List<string> s = new();

        // Warning: Instantiate in Update
        UnityEngine.Object.Instantiate(prefab);

        // Warning: struct boxed to object in Update
        object boxed = new Vector3();
        _ = boxed;
    }

    void FixedUpdate()
    {
        // Warning: array allocated in FixedUpdate
        var arr = new int[10];
    }

    // NOT a hot path - should NOT warn
    void Start()
    {
        var list = new System.Collections.Generic.List<int>();
    }

    // Struct allocation - should NOT warn (value type, not boxed)
    void Update2()
    {
        var v = new Vector3();
        _ = v;
    }
}

// NOT a MonoBehaviour - should NOT warn
public class PlainClass
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}

// Custom update loop listed in .editorconfig (additional_hot_methods = Tick) - should warn
public class Simulation
{
    public void Tick(float deltaTime)
    {
        // Warning: List<float> allocated in Tick
        var samples = new System.Collections.Generic.List<float>();
    }
}

// Listed in .editorconfig (excluded_types = LoadingScreen) - should NOT warn
public class LoadingScreen : MonoBehaviour
{
    void Update()
    {
        var list = new System.Collections.Generic.List<int>();
    }
}

// [ObjectPool] generates BulletPool with Get(float) and Return(Bullet) - the same shape OPL001's
// code fix rewrites `new Bullet(speed)` into, so a hot path that goes through the pool is quiet.
[ObjectPoolLinter.ObjectPool(InitialCapacity = 16)]
public class Bullet
{
    public float Speed;

    public Bullet(float speed)
    {
        Speed = speed;
    }
}

// The reset hook for the generated pool lives in the consumer's own partial.
public static partial class BulletPool
{
    static partial void Reinitialize(Bullet instance, float speed)
    {
        instance.Speed = speed;
    }
}

// Every allocation below is one ObjectPoolSuppressionAnalyzer suppresses, so none of them is
// reported even though they all sit in Update.
public class SuppressedPatterns : MonoBehaviour
{
    private static bool s_warmed;

    private System.Collections.Generic.List<int> _cached = null!;

    void Update()
    {
        // Suppressed (OPLS001): runs on the first frame only.
        if (Time.frameCount == 0)
        {
            var warmup = new System.Collections.Generic.List<int>();
            _ = warmup;
        }

        // Suppressed (OPLS003): the latch the branch sets makes this run once.
        if (!s_warmed)
        {
            var once = new System.Collections.Generic.List<int>();
            _ = once;
            s_warmed = true;
        }

        // Suppressed (OPLS004): the object is cached in a field rather than thrown away.
        _cached = new System.Collections.Generic.List<int>();

#if UNITY_EDITOR
        // Suppressed (OPLS002): editor-only, so it is not in a player build.
        var editorOnly = new System.Collections.Generic.List<int>();
        _ = editorOnly;
#endif
    }
}

public class Turret : MonoBehaviour
{
    void Update()
    {
        // No OPL001: BulletPool.Get is a call, not an allocation.
        var bullet = BulletPool.Get(12f);
        BulletPool.Return(bullet);
    }
}

// OPL002 also covers state machines and LINQ-style extensions (1.5.5). Each call below allocates
// every frame even though no `new` appears in Update.
public class Wave : MonoBehaviour
{
    readonly System.Collections.Generic.List<int> _hp = new();

    System.Collections.IEnumerator Spawn()
    {
        yield return null;
    }

    async System.Threading.Tasks.Task SaveAsync()
    {
        await System.Threading.Tasks.Task.Yield();
    }

    void Update()
    {
        // OPL002: calling an iterator creates its state machine
        var routine = Spawn();

        // OPL002: so does calling an async method
        _ = SaveAsync();

        // OPL002: a LINQ-style extension on IEnumerable<T> enumerates through the interface
        var alive = _hp.CountAlive();
    }
}

public static class SequenceExtensions
{
    public static int CountAlive(this System.Collections.Generic.IEnumerable<int> source)
    {
        var count = 0;
        foreach (var hp in source)
            if (hp > 0) count++;
        return count;
    }
}
