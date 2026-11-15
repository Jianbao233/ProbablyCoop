namespace ProbablyCoop.Sync;

internal enum AuthorityAction : byte { Scavenge = 1, SetAppraisalCategory = 2, Business = 3, Machine = 4, Item = 5, Activity = 6 }

internal readonly struct AuthorityRequest
{
    internal readonly AuthorityAction Action;
    internal readonly int Day;
    internal readonly uint Epoch;
    internal readonly ulong Serial;
    internal readonly int Item;
    internal readonly string Category;
    internal readonly bool Allowed;
    internal readonly BusinessIntent Business;
    internal readonly MachineIntent Machine;
    internal readonly ItemIntent ItemUse;
    internal readonly ActivityIntent Activity;

    internal AuthorityRequest(AuthorityAction action, int day, uint epoch, ulong serial,
        int item = 0, string category = "", bool allowed = false, BusinessIntent business = default,
        MachineIntent machine = default, ItemIntent itemUse = default, ActivityIntent activity = default)
    {
        Action = action;
        Day = day;
        Epoch = epoch;
        Serial = serial;
        Item = item;
        Category = category;
        Allowed = allowed;
        Business = business;
        Machine = machine;
        ItemUse = itemUse;
        Activity = activity;
    }

    internal byte[] Encode()
    {
        var writer = new SyncWriter();
        writer.Byte(WorldWire.AuthorityRequest);
        writer.Byte((byte)Action);
        writer.UInt((uint)Day);
        writer.UInt(Epoch);
        writer.ULong(Serial);
        if (Action == AuthorityAction.SetAppraisalCategory)
        {
            writer.UInt((uint)Item);
            writer.Text(Category);
            writer.Bool(Allowed);
        }
        else if (Action == AuthorityAction.Business) Business.Write(writer);
        else if (Action == AuthorityAction.Machine) Machine.Write(writer);
        else if (Action == AuthorityAction.Item) ItemUse.Write(writer);
        else if (Action == AuthorityAction.Activity) Activity.Write(writer);
        return writer.ToArray();
    }

    internal static bool TryRead(byte[] bytes, out AuthorityRequest value)
    {
        value = default;
        if (bytes == null || bytes.Length < 2 || bytes.Length >
            (bytes[1] == (byte)AuthorityAction.Activity ? ActivityIntent.MaxTextBytes + 64 : 158)) return false;
        var reader = new SyncReader(bytes);
        if (reader.Byte() != WorldWire.AuthorityRequest) return false;
        var action = (AuthorityAction)reader.Byte();
        uint day = reader.UInt(), epoch = reader.UInt();
        ulong serial = reader.ULong();
        if (!reader.Ok || day == 0 || day > int.MaxValue || serial == 0) return false;
        switch (action)
        {
            case AuthorityAction.Scavenge:
                value = new AuthorityRequest(action, (int)day, epoch, serial);
                break;
            case AuthorityAction.SetAppraisalCategory:
                uint item = reader.UInt();
                string category = reader.Text(128);
                bool allowed = reader.Bool();
                if (item == 0 || item > int.MaxValue || !TeamTransition.ValidLocation(category)) return false;
                value = new AuthorityRequest(action, (int)day, epoch, serial, (int)item, category, allowed);
                break;
            case AuthorityAction.Business:
                var business = BusinessIntent.Read(reader);
                if (!business.Valid) return false;
                value = new AuthorityRequest(action, (int)day, epoch, serial, business: business);
                break;
            case AuthorityAction.Machine:
                var machine = MachineIntent.Read(reader);
                if (!machine.Valid) return false;
                value = new AuthorityRequest(action, (int)day, epoch, serial, machine: machine);
                break;
            case AuthorityAction.Item:
                var itemUse = ItemIntent.Read(reader);
                if (!itemUse.Valid) return false;
                value = new AuthorityRequest(action, (int)day, epoch, serial, itemUse: itemUse);
                break;
            case AuthorityAction.Activity:
                var activity = ActivityIntent.Read(reader);
                if (!activity.Valid) return false;
                value = new AuthorityRequest(action, (int)day, epoch, serial, activity: activity);
                break;
            default:
                return false;
        }
        return reader.Ok && reader.AtEnd;
    }
}
