using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProbablyCoop.Core;

internal enum ShopAction : ushort { Move = 1, Buy, Sell, Ready, FinishDay, PickUp, Place, Release, GoScavenge, Scavenge, TakeLoot, DropLoot, ReturnFromScavenge, SelectOffer, ToggleShutter, RingBell, DismissCustomer, AdvanceDialogue, ChooseDialogue }
internal enum ShopResult : ushort { None, Applied, Stale, Unavailable, NoSpace, Busy, Failed }
internal enum ShopDay : byte { Playing, Ready, Settling, Saved, SaveUnverified, Ended }

internal readonly struct ShopPlacement
{
    internal readonly byte Container, X, Y, Transform;

    internal ShopPlacement(byte container, byte x, byte y, byte transform)
    {
        Container = container;
        X = x;
        Y = y;
        Transform = transform;
    }

    internal bool Valid => Container is 1 or 2 or 6 or 7 && X < 24 && Y < 10 && Transform < 8;
}

internal sealed class ShopItem
{
    internal uint Id;
    internal string Atlas = "";
    internal string Sprite = "";
    internal byte X, Y, Width, Height, Transform;
    internal ulong Cells;
    internal string Label = "";
    internal int Quantity;
    internal byte Place;
    internal bool Owned;
    internal bool Movable;
}

internal sealed class ShopView
{
    internal bool Active;
    internal int Day;
    internal int Cash;
    internal ShopDay Phase;
    internal byte ReadyMask;
    internal ulong NightServices;
    internal int NightCost;
    internal uint HeldItem;
    internal byte Shutter;
    internal byte ShowcaseTier;
    internal string Customer = "";
    internal string CustomerSprite = "";
    internal uint DialogueId;
    internal byte DialogueSpeaker;
    internal byte DialogueChoicesEnabled;
    internal string DialogueLine = "";
    internal string[] DialogueChoices = Array.Empty<string>();
    internal bool CanGoOut;
    internal bool GuestOutside;
    internal byte SearchesLeft;
    internal ushort MinorWoundChance, MajorWoundChance;
    internal int[] ServiceCosts = Array.Empty<int>();
    internal string[] MorningReport = Array.Empty<string>();
    internal uint OfferId;
    internal ShopAction OfferAction;
    internal int Price;
    internal string OfferLabel = "";
    internal bool MoreItems;
    internal List<ShopItem> Items = new List<ShopItem>();
}

internal interface IShopAuthority
{
    ShopView Capture();
    ShopResult Apply(ShopAction action, uint entity, ShopPlacement placement);
}

internal static class ShopProtocol
{
    internal const int MaxItems = 80;
    internal const int MaxText = 64;
    internal const int MaxSpriteText = 48;
    internal const int MaxReportLines = 20;
    internal const int MaxReportText = 192;
    internal const int MaxDialogueChoices = 8;
    internal const int MaxDialogueLine = 224;
    internal const int MaxDialogueChoice = 96;
    internal const int MinSize = 60;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] Encode(ShopView view)
    {
        if (view.Items.Count > MaxItems) throw new ArgumentOutOfRangeException(nameof(view));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(view.Active);
        writer.Write(view.Day);
        writer.Write(view.Cash);
        writer.Write((byte)view.Phase);
        writer.Write(view.ReadyMask);
        writer.Write(view.NightServices);
        writer.Write(view.NightCost);
        writer.Write(view.HeldItem);
        writer.Write(view.Shutter);
        writer.Write(view.ShowcaseTier);
        WriteText(writer, view.Customer);
        WriteText(writer, view.CustomerSprite);
        writer.Write(view.DialogueId);
        writer.Write(view.DialogueSpeaker);
        writer.Write(view.DialogueChoicesEnabled);
        WriteText(writer, view.DialogueLine, MaxDialogueLine);
        writer.Write((byte)view.DialogueChoices.Length);
        foreach (string choice in view.DialogueChoices) WriteText(writer, choice, MaxDialogueChoice);
        writer.Write((byte)((view.CanGoOut ? 1 : 0) | (view.GuestOutside ? 2 : 0)));
        writer.Write(view.SearchesLeft);
        writer.Write(view.MinorWoundChance);
        writer.Write(view.MajorWoundChance);
        writer.Write((byte)view.ServiceCosts.Length);
        foreach (int cost in view.ServiceCosts) writer.Write(cost);
        writer.Write((byte)view.MorningReport.Length);
        foreach (string line in view.MorningReport) WriteText(writer, line, MaxReportText);
        writer.Write(view.OfferId);
        writer.Write((ushort)view.OfferAction);
        writer.Write(view.Price);
        WriteText(writer, view.OfferLabel);
        writer.Write(view.MoreItems);
        writer.Write((byte)view.Items.Count);
        foreach (var item in view.Items)
        {
            writer.Write(item.Id);
            writer.Write(item.Quantity);
            writer.Write(item.Place);
            writer.Write((byte)((item.Owned ? 1 : 0) | (item.Movable ? 2 : 0)));
            WriteText(writer, item.Label);
            WriteText(writer, item.Atlas, MaxSpriteText);
            WriteText(writer, item.Sprite, MaxSpriteText);
            writer.Write(item.X);
            writer.Write(item.Y);
            writer.Write(item.Width);
            writer.Write(item.Height);
            writer.Write(item.Transform);
            writer.Write(item.Cells);
        }
        return stream.ToArray();
    }

    internal static bool TryDecode(byte[] payload, out ShopView view)
    {
        view = null;
        if (payload == null || payload.Length < MinSize || payload.Length > Wire.MaxSize - Wire.HeaderSize) return false;
        try
        {
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var result = new ShopView
            {
                Active = reader.ReadBoolean(), Day = reader.ReadInt32(), Cash = reader.ReadInt32(),
                Phase = (ShopDay)reader.ReadByte(), ReadyMask = reader.ReadByte(),
                NightServices = reader.ReadUInt64(), NightCost = reader.ReadInt32(), HeldItem = reader.ReadUInt32(),
                Shutter = reader.ReadByte(), ShowcaseTier = reader.ReadByte(), Customer = ReadText(reader), CustomerSprite = ReadText(reader)
            };
            if (result.Shutter > 2 || result.ShowcaseTier > 2 || (result.Customer.Length == 0) != (result.CustomerSprite.Length == 0))
                return false;
            result.DialogueId = reader.ReadUInt32();
            result.DialogueSpeaker = reader.ReadByte();
            result.DialogueChoicesEnabled = reader.ReadByte();
            result.DialogueLine = ReadText(reader, MaxDialogueLine);
            int choices = reader.ReadByte();
            if (result.DialogueId > 0xffffff || result.DialogueSpeaker > 3 || choices > MaxDialogueChoices ||
                (result.DialogueChoicesEnabled >> choices) != 0 ||
                (result.DialogueId == 0 && (result.DialogueSpeaker != 0 || result.DialogueLine.Length != 0 || choices != 0)))
                return false;
            result.DialogueChoices = new string[choices];
            for (int i = 0; i < choices; i++) result.DialogueChoices[i] = ReadText(reader, MaxDialogueChoice);
            int travel = reader.ReadByte();
            result.CanGoOut = (travel & 1) != 0;
            result.GuestOutside = (travel & 2) != 0;
            result.SearchesLeft = reader.ReadByte();
            result.MinorWoundChance = reader.ReadUInt16();
            result.MajorWoundChance = reader.ReadUInt16();
            if (travel > 2 || (!result.GuestOutside && (result.SearchesLeft != 0 ||
                    result.MinorWoundChance != 0 || result.MajorWoundChance != 0)) ||
                result.MinorWoundChance > 10000 || result.MajorWoundChance > 10000) return false;
            int serviceCount = reader.ReadByte();
            if (serviceCount > 64 || (result.Phase != ShopDay.Ready && serviceCount != 0)) return false;
            result.ServiceCosts = new int[serviceCount];
            long total = 0;
            for (int i = 0; i < serviceCount; i++) total += result.ServiceCosts[i] = reader.ReadInt32();
            if (total != result.NightCost || serviceCount != CountBits(result.NightServices)) return false;
            int reportCount = reader.ReadByte();
            if (reportCount > MaxReportLines || (result.Phase != ShopDay.Saved && reportCount != 0)) return false;
            result.MorningReport = new string[reportCount];
            for (int i = 0; i < reportCount; i++) result.MorningReport[i] = ReadText(reader, MaxReportText);
            result.OfferId = reader.ReadUInt32();
            result.OfferAction = (ShopAction)reader.ReadUInt16();
            result.Price = reader.ReadInt32();
            result.OfferLabel = ReadText(reader);
            result.MoreItems = reader.ReadBoolean();
            int count = reader.ReadByte();
            if (count > MaxItems || result.Day < 0 || result.Phase > ShopDay.Ended || result.ReadyMask > 3 ||
                (result.Phase != ShopDay.Ready && (result.ReadyMask != 0 ||
                    result.NightServices != 0 || result.NightCost != 0)) ||
                result.Price < 0 || (result.OfferId == 0 ? result.OfferAction != 0 :
                    result.OfferAction is not (ShopAction.Buy or ShopAction.Sell))) return false;
            var ids = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint id = reader.ReadUInt32();
                int amount = reader.ReadInt32();
                byte place = reader.ReadByte();
                byte flags = reader.ReadByte();
                if (id == 0 || !ids.Add(id) || amount < 1 || place is < 1 or > 7 || (place is >= 3 and <= 5 && !result.GuestOutside) || flags > 3) return false;
                var item = new ShopItem
                {
                    Id = id, Quantity = amount, Place = place, Owned = (flags & 1) != 0,
                    Movable = (flags & 2) != 0, Label = ReadText(reader),
                    Atlas = ReadText(reader, MaxSpriteText), Sprite = ReadText(reader, MaxSpriteText),
                    X = reader.ReadByte(), Y = reader.ReadByte(), Width = reader.ReadByte(),
                    Height = reader.ReadByte(), Transform = reader.ReadByte(), Cells = reader.ReadUInt64()
                };
                if (!ValidPlacement(item)) return false;
                result.Items.Add(item);
            }
            if (stream.Position != stream.Length || (!result.Active && (count != 0 || result.OfferId != 0)) ||
                (result.HeldItem != 0 && !ids.Contains(result.HeldItem))) return false;
            view = result;
            return true;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException) { return false; }
    }

    private static int CountBits(ulong value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }

    private static bool ValidPlacement(ShopItem item)
    {
        if (item.Cells == 0)
            return item.Width == 0 && item.Height == 0 && item.Transform == 0 && item.X == 0 && item.Y == 0;
        int area = item.Width * item.Height;
        return item.Width > 0 && item.Height > 0 && area <= 64 && item.Transform < 8 &&
            item.X < 24 && item.Y < 10 && (area == 64 || item.Cells >> area == 0);
    }

    private static void WriteText(BinaryWriter writer, string value, int limit = MaxText)
    {
        Span<byte> buffer = stackalloc byte[limit];
        // Encoder.Convert never leaves a partial UTF-8 sequence at the packet boundary.
        Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), buffer, true, out _, out int used, out _);
        writer.Write((byte)used);
        writer.Write(buffer.Slice(0, used));
    }

    private static string ReadText(BinaryReader reader, int limit = MaxText)
    {
        int count = reader.ReadByte();
        if (count > limit) throw new InvalidDataException();
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return Utf8.GetString(bytes);
    }
}
