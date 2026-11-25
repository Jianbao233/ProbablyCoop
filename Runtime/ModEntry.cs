using UnityEngine;

namespace ProbablyCoop;

public sealed class ModEntry : IMod
{
    private CoopRuntime runtime;
    private RoomRuntime next;

    public void Init(ModManifest manifest) => CoopSettings.Register();

    public void OnEnable()
    {
        if (runtime != null || next != null) return;
        var root = new GameObject("ProbablyCoop");
        Object.DontDestroyOnLoad(root);
        // The rebuilt stack is opt-in until it can carry a shared shop.
        if (RoomRuntime.Selected) next = root.AddComponent<RoomRuntime>();
        else runtime = root.AddComponent<CoopRuntime>();
    }

    public void OnDisable()
    {
        if (runtime != null)
        {
            runtime.Stop();
            Object.Destroy(runtime.gameObject);
            runtime = null;
        }
        if (next != null)
        {
            next.Stop();
            Object.Destroy(next.gameObject);
            next = null;
        }
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
        NativePlatform.ResetAndroidHosting();
#endif
    }
}
