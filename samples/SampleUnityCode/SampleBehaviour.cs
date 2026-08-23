using UnityEngine;

public class PlayerBehaviour : MonoBehaviour
{
    public UnityEngine.Object prefab;

    // HOT PATH - should trigger OPL001 warnings
    void Update()
    {
        // Warning: List<int> allocated in Update
        var list = new System.Collections.Generic.List<int>();

        // Warning: Instantiate in Update
        UnityEngine.Object.Instantiate(prefab);
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

    // Struct allocation - should NOT warn (value type)
    void Update2()
    {
        var v = new Vector3();
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