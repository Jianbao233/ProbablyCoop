namespace ProbablyCoop.Sync;

internal enum ActivityAction : byte
{
    Dial = 1, QuickDial, HangUp, PhoneAdvance, PhoneChoice,
    BrowseUpgrades, BuyUpgrade, CloseUpgrades,
    CommissaryBuy, RedeemStamps, RedeemLottery, RequestDelivery, ToggleService, ReadNewspaper,
    HopperRun, LogisticsSetting, ProductionTarget,
}

internal readonly struct ActivityIntent
{
    internal const int MaxTextBytes = 8192;
    internal readonly ActivityAction Action;
    internal readonly ulong Proof;
    internal readonly int Owner;
    internal readonly long Value;
    internal readonly string Text;

    internal ActivityIntent(ActivityAction action, ulong proof, int owner = 0, long value = 0, string text = "")
    {
        Action = action;
        Proof = proof;
        Owner = owner;
        Value = value;
        Text = text;
    }

    internal bool Valid => Proof != 0 && Text != null && (Action switch
    {
        ActivityAction.Dial or ActivityAction.QuickDial =>
            Owner == 0 && Text.Length == 0 && Value is >= 1 and <= 999999999,
        ActivityAction.PhoneChoice => Owner == 0 && Text.Length == 0 && Value is >= 0 and < 16,
        ActivityAction.CommissaryBuy or ActivityAction.RequestDelivery =>
            Owner == 0 && Value == 0 && Text.Length <= 96 && TeamTransition.ValidLocation(Text),
        ActivityAction.BuyUpgrade => Owner == 0 && Value is >= 0 and <= int.MaxValue &&
            Text.Length <= 96 && TeamTransition.ValidLocation(Text),
        ActivityAction.ToggleService => Owner == 0 && Value is 0 or 1 &&
            Text.Length <= 96 && TeamTransition.ValidLocation(Text),
        ActivityAction.HangUp or ActivityAction.PhoneAdvance or ActivityAction.BrowseUpgrades or
        ActivityAction.CloseUpgrades or ActivityAction.RedeemStamps or ActivityAction.RedeemLottery or ActivityAction.ReadNewspaper =>
            Owner == 0 && Text.Length == 0 && Value == 0,
        ActivityAction.HopperRun => Owner > 0 && Text.Length == 0 && Value == 0,
        ActivityAction.LogisticsSetting => Owner > 0 && Value == 0 && Text.Length is > 0 and <= MaxTextBytes,
        ActivityAction.ProductionTarget => Owner > 0 && Value is > 0 and <= int.MaxValue && Owner != Value && Text.Length == 0,
        _ => false,
    });

    internal void Write(SyncWriter writer)
    {
        writer.Byte((byte)Action);
        writer.ULong(Proof);
        writer.UInt((uint)Owner);
        writer.Long(Value);
        writer.Text(Text);
    }

    internal static ActivityIntent Read(SyncReader reader)
    {
        var action = (ActivityAction)reader.Byte();
        ulong proof = reader.ULong();
        uint owner = reader.UInt();
        long value = reader.Long();
        string content = reader.Text(action == ActivityAction.LogisticsSetting ? MaxTextBytes : 96);
        return owner <= int.MaxValue ? new ActivityIntent(action, proof, (int)owner, value, content) : default;
    }
}
