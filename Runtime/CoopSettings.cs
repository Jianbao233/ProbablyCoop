using ProbablyCoop.Core;
using Steamworks;
using UnityEngine;

namespace ProbablyCoop;

internal static class CoopSettings
{
    private enum PingBinding { MiddleMouse, MouseButton4, MouseButton5, R, T, Disabled }
    private static ConfigEntry<PingBinding> pingBinding;
    private static ConfigEntry<string> pointerName;
    private static ConfigEntry<bool> syncNotices;
    private static string Loc(string key)=>ModHelper.GetLocalized("probably_coop",key);

    internal static void Register()
    {
        if(syncNotices!=null) return;
        var config=ModConfig.For("probably_coop");
        syncNotices=config.Define("show_sync_notices",false,"Co-op",()=>Loc("settings_sync_notices"),
            ()=>Loc("settings_sync_notices_help"));
        pingBinding=config.DefineEnum("item_ping_binding",PingBinding.MiddleMouse,"Co-op",()=>Loc("settings_ping_binding"),
            ()=>Loc("settings_ping_binding_help"));
        pointerName=config.Define("pointer_name","","Co-op",()=>Loc("settings_pointer_name"),
            tooltip:()=>Loc("settings_pointer_name_help"));
    }

    internal static bool ShowSyncNotices => syncNotices?.Value==true;
    internal static void SetSyncNotices(bool value)=>syncNotices?.Set(value);
    internal static string PointerName
    {
        get
        {
            var configured=pointerName?.Value ?? "";
            if(!string.IsNullOrEmpty(configured)) return configured;
            try
            {
                if(SteamManager.Initialized) return PointerProfile.NormalizeName(SteamFriends.GetPersonaName());
            }
            catch { }
            return "";
        }
    }
    internal static void SetPointerName(string value)=>pointerName?.Set(PointerProfile.NormalizeName(value));
    internal static KeyCode ItemPingKey => pingBinding?.Value switch
    {
        PingBinding.MouseButton4=>KeyCode.Mouse3,
        PingBinding.MouseButton5=>KeyCode.Mouse4,
        PingBinding.R=>KeyCode.R,
        PingBinding.T=>KeyCode.T,
        PingBinding.Disabled=>KeyCode.None,
        _=>KeyCode.Mouse2
    };
}
