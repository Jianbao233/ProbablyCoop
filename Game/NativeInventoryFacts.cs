using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

// Portable item facts from the public native item and tag model. Factory
// behavior, delegates, localized strings, windows and UI references stay local.
internal sealed class NativeInventoryFacts
{
    private const byte Version = 5;
    private const int MaxTags = 256, MaxTypes = 64, MaxNameBytes = 160, MaxValueBytes = 2048;
    private const int MaxShapeSide = 48;
    private const string OwnedTag = "IS_OWNED_TAG";
    private readonly long baseValue, value, lateValue, backupValue;
    private readonly Dictionary<string, TagState> tags;
    private readonly string[] types;
    private readonly string atlas, sprite;
    private readonly GridShape shape;
    private readonly ItemFeature[] features;
    private readonly (MachineControl kind, float first, float second)[] controls;

    private NativeInventoryFacts(long baseValue, long value, long lateValue, long backupValue,
        Dictionary<string, TagState> tags, string[] types, string atlas, string sprite, GridShape shape,
        (MachineControl kind, float first, float second)[] controls, ItemFeature[] features)
    {
        this.baseValue = baseValue;
        this.value = value;
        this.lateValue = lateValue;
        this.backupValue = backupValue;
        this.tags = tags;
        this.types = types;
        this.atlas = atlas;
        this.sprite = sprite;
        this.shape = shape;
        this.controls = controls;
        this.features = features;
    }

    internal bool Owned => tags.TryGetValue(OwnedTag, out var tag) && tag.valueEnabled;

    internal void WriteControlProof(SyncWriter writer)
    {
        writer.UInt((uint)controls.Length);
        foreach (var control in controls)
        {
            writer.Byte((byte)control.kind);
            writer.UInt(unchecked((uint)BitConverter.SingleToInt32Bits(control.first)));
            writer.UInt(unchecked((uint)BitConverter.SingleToInt32Bits(control.second)));
        }
        writer.Text(tags.TryGetValue(MachineChemFinisher.SELECTED_CHEM_ID_TAG, out var recipe) && recipe.valueEnabled
            ? recipe.valueString : "");
        foreach (string key in NativeWorld.HopperConfigKeys.Append(MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING))
            if (tags.TryGetValue(key, out var setting))
            {
                writer.Text(key);
                writer.Bool(setting.valueEnabled);
                writer.Bool(setting.valueBool);
                writer.Long(setting.valueInt);
                writer.Text(setting.valueString);
            }
        writer.UInt((uint)features.Length);
        foreach (var feature in features)
        {
            writer.Text(feature.identifier);
            writer.Text(feature.realCondition?.identifier);
            writer.Text(feature.fakeCondition?.identifier);
            writer.Bool(feature.IsExposed());
        }
    }

    internal bool MatchesNative(GameItem item)
    {
        if (baseValue != item.unitBaseValue || value != item.unitValue || lateValue != item.lateUnitValue ||
            backupValue != item.backupUnitValue || atlas != (item.spriteAtlasPath ?? "") || sprite != (item.spritePath ?? "") ||
            Footprint(item) == shape || !types.SequenceEqual(item.itemTypes) || !FeaturesMatch(item)) return false;
        int liveTags = 0;
        foreach (var pair in item.state.dict)
        {
            var live = pair.Value;
            if (!HasValue(live)) continue;
            liveTags++;
            if (!tags.TryGetValue(pair.Key, out var known) || known.valueEnabled != live.valueEnabled ||
                known.valueBool != live.valueBool || known.valueInt != live.valueInt || known.valueLong != live.valueLong ||
                BitConverter.SingleToInt32Bits(known.valueFloat) != BitConverter.SingleToInt32Bits(live.valueFloat) ||
                BitConverter.DoubleToInt64Bits(known.valueDouble) != BitConverter.DoubleToInt64Bits(live.valueDouble) ||
                known.valueString != (live.valueString ?? "")) return false;
        }
        if (liveTags != tags.Count) return false;
        if (controls.Length == 0 && item.contentWindow == null) return true;
        var widgets = NativeMachineControls.Read(item);
        if (controls.Length != widgets.Count) return false;
        for (int i = 0; i < widgets.Count; i++)
        {
            var value = NativeMachineControls.Values(widgets[i]);
            if (controls[i] != (NativeMachineControls.Kind(widgets[i]), value.first, value.second)) return false;
        }
        return true;
    }

    internal static byte[] Capture(GameItem item)
    {
        var writer = new SyncWriter();
        writer.Byte(Version);
        writer.Long(item.unitBaseValue);
        writer.Long(item.unitValue);
        writer.Long(item.lateUnitValue);
        writer.Long(item.backupUnitValue);
        writer.Text(item.spriteAtlasPath);
        writer.Text(item.spritePath);
        writer.UInt((uint)item.shape.width);
        writer.UInt((uint)item.shape.height);
        var mask = new byte[checked(item.shape.width * item.shape.height)];
        for (int y = 0; y < item.shape.height; y++)
            for (int x = 0; x < item.shape.width; x++)
                mask[y * item.shape.width + x] = item.shape.GetLocal(x, y);
        writer.Bytes(mask);
        writer.UInt((uint)item.itemTypes.Count);
        foreach (string type in item.itemTypes) writer.Text(type);
        var tags = item.state.dict.Where(pair => HasValue(pair.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        writer.UInt((uint)tags.Length);
        foreach (var pair in tags)
        {
            var tag = pair.Value;
            writer.Text(pair.Key);
            writer.Bool(tag.valueEnabled);
            writer.Bool(tag.valueBool);
            writer.Long(tag.valueInt);
            writer.Long(tag.valueLong);
            writer.Long(BitConverter.SingleToInt32Bits(tag.valueFloat));
            writer.Long(BitConverter.DoubleToInt64Bits(tag.valueDouble));
            writer.Text(tag.valueString);
        }
        var widgets = NativeMachineControls.Read(item);
        writer.UInt((uint)widgets.Count);
        foreach (var widget in widgets)
        {
            var value = NativeMachineControls.Values(widget);
            writer.Byte((byte)NativeMachineControls.Kind(widget));
            writer.UInt(unchecked((uint)BitConverter.SingleToInt32Bits(value.first)));
            writer.UInt(unchecked((uint)BitConverter.SingleToInt32Bits(value.second)));
        }
        writer.Text(item.itemFeatures.Count == 0 ? "" :
            new JArray(item.itemFeatures.Select(feature => NativeMirrorProbe.FeatureData(feature, null))).ToString(Formatting.None));
        return writer.ToArray();
    }

    internal static bool TryDecode(byte[] data, out NativeInventoryFacts facts)
    {
        facts = null;
        if (data == null || data.Length > InventoryEntry.MaxFactsBytes) return false;
        var reader = new SyncReader(data);
        if (reader.Byte() != Version) return false;
        long baseValue = reader.Long(), value = reader.Long(), lateValue = reader.Long(), backupValue = reader.Long();
        string atlas = reader.Text(MaxNameBytes), sprite = reader.Text(MaxNameBytes);
        uint width = reader.UInt(), height = reader.UInt();
        var mask = reader.Bytes(MaxShapeSide * MaxShapeSide);
        if (!reader.Ok || atlas.Any(char.IsControl) || sprite.Any(char.IsControl) ||
            width == 0 || height == 0 || width > MaxShapeSide || height > MaxShapeSide ||
            mask.Length != width * height || !mask.Contains((byte)1) || mask.Any(cell => cell > 1)) return false;
        uint typeCount = reader.UInt();
        if (!reader.Ok || typeCount > MaxTypes) return false;
        var types = new string[typeCount];
        for (int i = 0; i < types.Length; i++)
        {
            string type = reader.Text(MaxNameBytes);
            if (!reader.Ok || type.Length == 0 || type.Any(char.IsControl)) return false;
            types[i] = type;
        }
        uint count = reader.UInt();
        if (!reader.Ok || count > MaxTags) return false;
        var tags = new Dictionary<string, TagState>((int)count, StringComparer.Ordinal);
        string prior = null;
        for (int i = 0; i < count; i++)
        {
            string key = reader.Text(MaxNameBytes);
            bool enabled = reader.Bool(), flag = reader.Bool();
            long integer = reader.Long(), longValue = reader.Long(), single = reader.Long(), dbl = reader.Long();
            string text = reader.Text(key == MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING ||
                key == HopperHelper.FILTER_STRING ? ActivityIntent.MaxTextBytes : MaxValueBytes);
            if (!reader.Ok || key.Length == 0 || key.Any(char.IsControl) || string.CompareOrdinal(prior, key) >= 0 ||
                integer < int.MinValue || integer > int.MaxValue || single < int.MinValue || single > int.MaxValue)
                return false;
            var tag = new TagState(key)
            {
                valueEnabled = enabled,
                valueBool = flag,
                valueInt = (int)integer,
                valueLong = longValue,
                valueFloat = BitConverter.Int32BitsToSingle((int)single),
                valueDouble = BitConverter.Int64BitsToDouble(dbl),
                valueString = text,
            };
            if (float.IsNaN(tag.valueFloat) || float.IsInfinity(tag.valueFloat) ||
                double.IsNaN(tag.valueDouble) || double.IsInfinity(tag.valueDouble) || !HasValue(tag)) return false;
            tags.Add(key, tag);
            prior = key;
        }
        uint widgetCount = reader.UInt();
        if (!reader.Ok || widgetCount > NativeMachineControls.Limit) return false;
        var controls = new (MachineControl kind, float first, float second)[widgetCount];
        for (int i = 0; i < controls.Length; i++)
        {
            var kind = (MachineControl)reader.Byte();
            float first = BitConverter.Int32BitsToSingle(unchecked((int)reader.UInt()));
            float second = BitConverter.Int32BitsToSingle(unchecked((int)reader.UInt()));
            if (!new MachineIntent(1, i, kind, 1, first, second).Valid) return false;
            controls[i] = (kind, first, second);
        }
        string featureText = reader.Text(InventoryEntry.MaxFactsBytes);
        if (!reader.Ok || !reader.AtEnd || !TryReadFeatures(featureText, out var features)) return false;
        var shape = new GridShapeBuilder().SetData(mask, (int)width).Build();
        facts = new NativeInventoryFacts(baseValue, value, lateValue, backupValue, tags, types, atlas, sprite, shape, controls, features);
        return true;
    }

    private static bool TryReadFeatures(string text, out ItemFeature[] result)
    {
        result = Array.Empty<ItemFeature>();
        if (text.Length == 0) return true;
        try
        {
            var rows = JArray.Parse(text, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (rows.Count > 48 || rows.Any(row => row is not JObject ||
                row["fields"] is not JObject || row["real"]?.Type is not (JTokenType.Object or JTokenType.Null) ||
                row["fake"]?.Type is not (JTokenType.Object or JTokenType.Null))) return false;
            result = rows.Cast<JObject>().Select(NativeMirrorProbe.ReadFeature).ToArray();
            return result.All(feature => Enum.IsDefined(typeof(ItemFeature.FeatureType), feature.featureType) &&
                Enum.IsDefined(typeof(ItemFeature.ValueStage), feature.valueStage) &&
                (!feature.useCondition || feature.realCondition != null && feature.fakeCondition != null));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or
            InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private bool FeaturesMatch(GameItem item)
    {
        var live = item.itemFeatures;
        if (live.Count != features.Length) return false;
        for (int i = 0; i < features.Length; i++)
            if (!NativeMirrorProbe.FeatureMatches(live[i], features[i])) return false;
        return true;
    }

    internal void Apply(GameItem item)
    {
        item.unitBaseValue = baseValue;
        item.unitValue = value;
        item.lateUnitValue = lateValue;
        item.backupUnitValue = backupValue;
        if (item.spriteAtlasPath != atlas || item.spritePath != sprite) item.SetSprite(atlas, sprite);
        if (Footprint(item) == shape) item.shape = shape.Clone();
        // Keep any native references to tags alive when rolling an effect back.
        foreach (var current in item.state.dict)
            if (!tags.ContainsKey(current.Key) && HasValue(current.Value)) current.Value.Destroy();
        foreach (var pair in tags)
            item.state.GetTag(pair.Key).SetEnabled(pair.Value.valueEnabled).Set(pair.Value);
        if (!types.SequenceEqual(item.itemTypes))
        {
            item.itemTypes.Clear();
            item.itemTypes.AddRange(types);
        }
        if (!FeaturesMatch(item))
        {
            var live = item.itemFeatures;
            for (int i = 0; i < features.Length; i++)
            {
                var expected = features[i];
                var existing = i < live.Count ? live[i] : null;
                if (NativeMirrorProbe.FeatureMatches(existing, expected)) continue;
                var updated = NativeMirrorProbe.CopyFeature(expected,
                    existing?.identifier == expected.identifier && existing?.category == expected.category ? existing : null);
                if (i < live.Count) live[i] = updated;
                else live.Add(updated);
            }
            if (live.Count > features.Length) live.RemoveRange(features.Length, live.Count - features.Length);
        }
        ApplyControls(item);
    }

    internal void ApplyControls(GameItem item)
    {
        if (controls.Length == 0) return;
        var widgets = NativeMachineControls.Read(item);
        if (widgets.Count != controls.Length) return;
        for (int i = 0; i < widgets.Count; i++)
        {
            var control = controls[i];
            if (NativeMachineControls.Kind(widgets[i]) == control.kind)
                NativeMachineControls.Display(widgets[i], control.first, control.second);
        }
    }

    internal GridShape Footprint(GameItem item)
    {
        if (item.shape.width != shape.width || item.shape.height != shape.height) return shape;
        for (int y = 0; y < shape.height; y++)
            for (int x = 0; x < shape.width; x++)
                if (item.shape.GetLocal(x, y) != shape.GetLocal(x, y)) return shape;
        return item.modifiedShape;
    }

    private static bool HasValue(TagState tag) => tag != null &&
        (tag.valueEnabled || tag.valueBool || tag.valueInt != 0 || tag.valueLong != 0 ||
         tag.valueFloat != 0 || tag.valueDouble != 0 || !string.IsNullOrEmpty(tag.valueString));
}
