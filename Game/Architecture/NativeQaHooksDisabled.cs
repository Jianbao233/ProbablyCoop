#if !PROBABLY_COOP_GRID_QA
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// The normal build has no fixture runner. Shared gameplay code can still call
// these observation hooks without compiling fixture code into the Mod.
internal static class NativeBackgroundQa
{
    internal static bool Enabled => false;
    internal static bool QaStopping => false;
    internal static bool SynchronizationProfile => false;
    internal static bool IsHostProduct => NativeQaDisplay.IsProduct("Host");
    internal static bool IsGuestProduct => NativeQaDisplay.IsProduct("Guest");

    internal static void PerturbRootGrid(GameGridInventory grid, PropertyInfo property, NativeSnapshotIndex.RootGrid shape) { }
    internal static void RememberAppraisalCommand(JObject command) { }
    internal static void RememberBubbleSkip(bool phone, JObject command) { }
    internal static void RememberDocumentRead(JObject command) { }
    internal static void RememberFirstLabelCommand(JObject command) { }
    internal static void RememberFrontendCommand(JObject command) { }
    internal static void RememberLogisticsCommand(JObject command) { }
    internal static void RememberMindCommand(JObject command) { }
    internal static void RememberScannerCommand(JObject command) { }
    internal static void RememberScratchCommand(JObject command) { }
    internal static void RememberStorageTabCommand(JObject command) { }
    internal static void RememberWaterStripCommand(JObject command) { }
}

internal static class NativeArchitectureQa
{
    internal static bool Active => false;
    internal static bool GraphicsRequested => false;
    internal static void CaptureFrame(string name) { }
}
#endif
