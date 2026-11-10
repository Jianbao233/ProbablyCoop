namespace ProbablyCoop.Core;

// The queries are independent. In particular Can does not imply May.
internal static class ItemAbilityBits
{
    internal const int MaxValue=63;
    internal static int Pack(bool mayActivate,bool canActivate,bool mayToggle,bool canToggle,bool maySelect,bool canSelect) =>
        (mayActivate ? 1 : 0) | (canActivate ? 2 : 0) | (mayToggle ? 4 : 0) |
        (canToggle ? 8 : 0) | (maySelect ? 16 : 0) | (canSelect ? 32 : 0);

    internal static bool Has(int value,string query)
    {
        int bit=query switch {
            "MayActivateSlotItem"=>1,"CanActivateSlotItem"=>2,"MayToggleSlotItem"=>4,
            "CanToggleSlotItem"=>8,"MaySelectSlotItem"=>16,"CanSelectSlotItem"=>32,_=>0
        };
        return (uint)value<=MaxValue && (value & bit)!=0;
    }
}
