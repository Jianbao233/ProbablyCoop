using UnityEngine;

namespace ProbablyCoop;

internal static class NativePlatform
{
    internal static bool IsAndroid => Application.platform == RuntimePlatform.Android;

    // The Android port can consume host state. Its rewritten game assembly
    // cannot install the validated desktop actor and inventory authority hooks.
    internal static bool SupportsHosting => !IsAndroid;
}
