namespace ProbablyCoop.Sync;

internal enum BusinessAction : byte
{
    Bell = 1, Dismiss, Advance, Choice,
    Buy, Sell, Accept, ConfirmBudget, Wholesale,
    Discount, Markup, Blackmail, BuyingMarkup, BuyingDiscount, Threaten,
    Aim, Fire, Holster, Estimate, OpenShop, CloseShop, BarterCycle, Report,
}

internal readonly struct BusinessIntent
{
    internal readonly BusinessAction Action;
    internal readonly ulong Customer, Revision;
    internal readonly int Item, Value;

    internal BusinessIntent(BusinessAction action, ulong customer, ulong revision = 0, int item = 0, int value = 0)
    {
        Action = action;
        Customer = customer;
        Revision = revision;
        Item = item;
        Value = value;
    }

    internal bool Valid => Item >= 0 && (Action switch
    {
        BusinessAction.Bell or BusinessAction.Dismiss => Revision == 0 && Item == 0 && Value == 0,
        BusinessAction.OpenShop => Customer == 0 && Revision == 0 && Item == 0 && Value == 0,
        BusinessAction.CloseShop => Revision == 0 && Item == 0 && Value >= 0,
        BusinessAction.BarterCycle => Customer != 0 && Revision != 0 && Item == 0 && Value is -1 or 1,
        BusinessAction.Advance => Revision != 0 && Item == 0 && Value == 0,
        BusinessAction.Choice => Revision != 0 && Item == 0 && Value is >= 0 and < 16,
        BusinessAction.Estimate => Customer != 0 && Revision != 0 && Item > 0 && Value is >= 0 and <= 100_000_000,
        BusinessAction.Buy or BusinessAction.Accept =>
            Customer != 0 && Revision != 0 && Value == 0,
        BusinessAction.Sell or BusinessAction.ConfirmBudget or BusinessAction.Wholesale or BusinessAction.Blackmail or
            BusinessAction.Threaten or BusinessAction.Aim or BusinessAction.Fire or BusinessAction.Holster or BusinessAction.Report =>
            Customer != 0 && Revision != 0 && Value == 0 && Item == 0,
        BusinessAction.Discount or BusinessAction.Markup or BusinessAction.BuyingMarkup or BusinessAction.BuyingDiscount =>
            Customer != 0 && Revision != 0 && Item == 0 && Value is 10 or 25 or 50 or 75,
        _ => false,
    });

    internal void Write(SyncWriter writer)
    {
        writer.Byte((byte)Action);
        writer.ULong(Customer);
        writer.ULong(Revision);
        writer.UInt((uint)Item);
        writer.Long(Value);
    }

    internal static BusinessIntent Read(SyncReader reader)
    {
        var action = (BusinessAction)reader.Byte();
        ulong customer = reader.ULong(), revision = reader.ULong();
        uint item = reader.UInt();
        long value = reader.Long();
        return item <= int.MaxValue && value is >= int.MinValue and <= int.MaxValue
            ? new BusinessIntent(action, customer, revision, (int)item, (int)value) : default;
    }
}
