using UnityEngine;

public class PlayerBehaviour : MonoBehaviour
{
    public UnityEngine.Object prefab = null!;

    // HOT PATH - should trigger OPL001 warnings
    void Update()
    {
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