using UnityEngine;

namespace ProbablyCoop;

public sealed class ModEntry : IMod
{
    private CoopRuntime runtime;

    public void Init(ModManifest manifest) { }

    public void OnEnable()
    {
        if (runtime != null) return;
        var root = new GameObject("ProbablyCoop");
        Object.DontDestroyOnLoad(root);
        runtime = root.AddComponent<CoopRuntime>();
    }

    public void OnDisable()
    {
        if (runtime == null) return;
        runtime.Stop();
        Object.Destroy(runtime.gameObject);
        runtime = null;
    }
}
