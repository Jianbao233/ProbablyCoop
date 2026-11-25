using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Api;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal static partial class NativeMirrorProbe
{
    // Cache only control metadata. GetValue/SetValue stay at each original call site.
    // Keep fixed wire names separate: a missing native field still fails when used.
    private static class ControlFields
    {
        internal static readonly System.Reflection.FieldInfo ScrollOverflow=AccessTools.Field(typeof(GameGridScrollableInventory),"scrollOverflow");
        internal static readonly System.Reflection.FieldInfo SlotBackgroundImage=AccessTools.Field(typeof(GameSlotInventory),"backgroundImage");
        internal static readonly System.Reflection.FieldInfo SlotHighlight=AccessTools.Field(typeof(GameSlotInventory),"highlight");
        internal static readonly System.Reflection.FieldInfo SlotWidth=AccessTools.Field(typeof(GameSlotInventory),"defaultGridWidth");
        internal static readonly System.Reflection.FieldInfo SlotHeight=AccessTools.Field(typeof(GameSlotInventory),"defaultGridHeight");
        internal static readonly System.Reflection.FieldInfo RowDisplayOrder=AccessTools.Field(typeof(MachineryPanelRow),"displayOrder");
        internal static readonly System.Reflection.FieldInfo PanelRequestedWidth=AccessTools.Field(typeof(MachineryPanel),"requestedWidth");
        internal static readonly System.Reflection.FieldInfo PanelRequestedHeight=AccessTools.Field(typeof(MachineryPanel),"requestedHeight");
        internal static readonly System.Reflection.FieldInfo PanelMinWidth=AccessTools.Field(typeof(MachineryPanel),"minWidth");
        internal static readonly System.Reflection.FieldInfo TextNode=AccessTools.Field(typeof(MachineryText),"textNode");
        internal static readonly System.Reflection.FieldInfo TextMinHeight=AccessTools.Field(typeof(MachineryText),"minHeight");
        internal static readonly System.Reflection.FieldInfo IntakeStyleInventories=AccessTools.Field(typeof(MachineryIntake),"styleInventories");
        internal static readonly System.Reflection.FieldInfo ButtonWidth=AccessTools.Field(typeof(MachineryButton),"forceWidth");
        internal static readonly System.Reflection.FieldInfo ButtonHeight=AccessTools.Field(typeof(MachineryButton),"forceHeight");
        internal static readonly System.Reflection.FieldInfo RangeWidth=AccessTools.Field(typeof(MachineryRangeSlider),"forceWidth");
        internal static readonly System.Reflection.FieldInfo RangeRequestedWidth=AccessTools.Field(typeof(MachineryRangeSlider),"requestedWidth");
        internal static readonly System.Reflection.FieldInfo AdvButtonWidth=AccessTools.Field(typeof(AdvButtonElement),"forceWidth");
        internal static readonly System.Reflection.FieldInfo AdvButtonHeight=AccessTools.Field(typeof(AdvButtonElement),"forceHeight");
        internal static readonly System.Reflection.FieldInfo PushIsToggle=AccessTools.Field(typeof(PushButtonElement),"isToggle");
        internal static readonly System.Reflection.FieldInfo PushIsLocked=AccessTools.Field(typeof(PushButtonElement),"isLocked");
        internal static readonly System.Reflection.FieldInfo PushWidth=AccessTools.Field(typeof(PushButtonElement),"forceWidth");
        internal static readonly System.Reflection.FieldInfo PushHeight=AccessTools.Field(typeof(PushButtonElement),"forceHeight");
        internal static readonly System.Reflection.FieldInfo PushIsDown=AccessTools.Field(typeof(PushButtonElement),"isDown");
        internal static readonly System.Reflection.FieldInfo LabelIcon=AccessTools.Field(typeof(MachineryLabel),"icon");
        internal static readonly System.Reflection.FieldInfo LabelWidth=AccessTools.Field(typeof(MachineryLabel),"fixedWidth");
        internal static readonly System.Reflection.FieldInfo TabsRequestedWidth=AccessTools.Field(typeof(MachineryTabs),"requestedWidth");
        internal static readonly System.Reflection.FieldInfo TabsRequestedHeight=AccessTools.Field(typeof(MachineryTabs),"requestedHeight");
        internal static readonly System.Reflection.FieldInfo SliderWidth=AccessTools.Field(typeof(MachinerySlider),"forceWidth");
        internal static readonly System.Reflection.FieldInfo SliderRequestedWidth=AccessTools.Field(typeof(MachinerySlider),"requestedWidth");
        internal static readonly System.Reflection.FieldInfo GridXSpacing=AccessTools.Field(typeof(GridPixelElement),"_xSpacing");
        internal static readonly System.Reflection.FieldInfo GridYSpacing=AccessTools.Field(typeof(GridPixelElement),"_ySpacing");
        internal static readonly System.Reflection.FieldInfo GridChildMinimumSize=AccessTools.Field(typeof(GridPixelElement),"childMinimumSize");
        internal static readonly string[] TagLayerNames={ "textNode", "textBackgroundNode", "textUnderlineNode", "textStrikethroughNode" };
        internal static readonly System.Reflection.FieldInfo[] TagLayers=TagLayerNames.Select(name=>AccessTools.Field(typeof(TagElement),name)).ToArray();
        internal static readonly System.Reflection.FieldInfo[] ButtonFaces={
            AccessTools.Field(typeof(MachineryButton),"faceNormal"), AccessTools.Field(typeof(MachineryButton),"faceHover"),
            AccessTools.Field(typeof(MachineryButton),"facePressed"), AccessTools.Field(typeof(MachineryButton),"faceDisabled") };
        internal static readonly System.Reflection.FieldInfo[] PushNodes={
            AccessTools.Field(typeof(PushButtonElement),"buttonNode"), AccessTools.Field(typeof(PushButtonElement),"buttonNode2") };
    }

    private sealed class SpriteIdentity
    {
        internal string Atlas, Key;
        internal SpriteIdentity(string atlas,string key) { Atlas=atlas; Key=key; }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Sprite,SpriteIdentity> SpriteIdentities = new System.Runtime.CompilerServices.ConditionalWeakTable<Sprite,SpriteIdentity>();
    private static readonly System.Reflection.FieldInfo AtlasCacheField=AccessTools.Field(typeof(RenderHandler),"atlasCache");
    // Prefer native atlas identities. Runtime sprites use a bounded data-only
    // image/geometry record; Unity references and callbacks never cross the wire.
    private static JObject SpriteKey(Sprite sprite,bool required=true)
    {
        using var timing=NativePerformance.Measure("host.sprite_identity");
        if (sprite == null) return null;
        var cache = (Dictionary<string, Dictionary<string, Sprite>>)AtlasCacheField.GetValue(null);
        // Atlas identities are stable for a loaded asset. Revalidate the exact
        // entry, avoiding sorting/scanning every atlas for every unchanged icon.
        if(SpriteIdentities.TryGetValue(sprite,out var saved))
        {
            if(cache.TryGetValue(saved.Atlas,out var atlas) && atlas.TryGetValue(saved.Key,out var current) && current==sprite)
                return new JObject { ["atlas"]=saved.Atlas,["sprite"]=saved.Key };
            SpriteIdentities.Remove(sprite);
        }
        // A validated fallback keeps observing texture and mesh changes, but
        // does not rescan every atlas on every subsequent capture.
        if(required && NativeSpriteTransfer.HasCapture(sprite)) return CaptureRuntimeSprite(sprite);
        foreach (var atlas in cache.OrderBy(p => p.Key, StringComparer.Ordinal))
            foreach (var entry in atlas.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (entry.Value == sprite)
                {
                    SpriteIdentities.Add(sprite,new SpriteIdentity(atlas.Key,entry.Key));
                    return new JObject { ["atlas"] = atlas.Key, ["sprite"] = entry.Key };
                }
        if(!required) return null;
        return CaptureRuntimeSprite(sprite);
    }
    private static JObject CaptureRuntimeSprite(Sprite sprite)
    {
        try { return NativeSpriteTransfer.Capture(sprite); }
        catch(Exception ex) { throw new NotSupportedException("Cannot synchronize sprite for item ["+Context?.CaptureOwner?.identifier+"]: "+ex.Message,ex); }
    }
    private static Sprite ReadSprite(JObject data) => data == null ? null : data["inline"]!=null
        ? NativeSpriteTransfer.Read(data) : RenderHandler.LoadFromAtlas((string)data["atlas"], (string)data["sprite"]);

    // A liquid surface, label, or machine overlay is a native render result.
    // Send the resolved layers so the guest can also render them on its held item.
    private const int MaximumItemSpriteModifiers=128;
    private static JArray CaptureSpriteModifiers(GameItem item)
    {
        var frozen=FreezeSpriteModifiers(item);
        return frozen==null ? null : new JArray(frozen.Select(layer=>layer.Materialize()));
    }

    private static NativeDeferredFields.SpriteLayer[] FreezeSpriteModifiers(GameItem item)
    {
        List<ItemSpriteModifier> layers;
        using(NativePerformance.Detailed ? NativePerformance.Measure("host.sprite_callback") : default)
            layers=item.getItemSpriteModifierForItemFunc?.Invoke(item);
        if(layers==null || layers.Count==0) return null;
        if(layers.Count>MaximumItemSpriteModifiers)
            throw new InvalidOperationException("Too many native item sprite layers");
        using var timing=NativePerformance.Detailed ? NativePerformance.Measure("host.sprite_freeze") : default;
        var frozen=new List<NativeDeferredFields.SpriteLayer>(layers.Count);
        foreach(var layer in layers)
            if(layer.sprite!=null) frozen.Add(new NativeDeferredFields.SpriteLayer(
                SpriteKey(layer.sprite),layer.position.x,layer.position.y,layer.orientation,layer.isFlipped,
                layer.priority,layer.haveMask,layer.color.r,layer.color.g,layer.color.b,layer.color.a));
        return frozen.ToArray();
    }

    private static List<ItemSpriteModifier> ReadSpriteModifiers(JArray data)
    {
        if(data==null || data.Count==0) return null;
        if(data.Count>MaximumItemSpriteModifiers)
            throw new InvalidOperationException("Too many replicated item sprite layers");
        var layers=new List<ItemSpriteModifier>(data.Count);
        foreach(var row in data)
        {
            var color=(JArray)row["color"];
            layers.Add(new ItemSpriteModifier(ReadSprite((JObject)row["image"]),
                new Vector2((float)row["x"],(float)row["y"]),(int)row["rotation"],(bool)row["flip"],
                (int)row["priority"],(bool)row["mask"],new Color((float)color[0],(float)color[1],(float)color[2],(float)color[3])));
        }
        return layers;
    }
    private static JObject CaptureExtraLayout(PixelElement element)
    {
        switch (element)
        {
            case TagElement tag:
                return new JObject { ["kind"] = "tag", ["fields"] = FieldData(tag,TagElementFields),
                    ["layers"] = new JArray(ControlFields.TagLayerNames.Select((name, index) => {
                        var node = (TreeNodeRender)ControlFields.TagLayers[index].GetValue(tag);
                        return new JObject { ["name"] = name, ["text"] = node.text.text, ["enabled"] = node.text.enabled,
                            ["size"] = node.text.fontSize, ["style"] = (int)node.text.fontStyle,
                            ["color"] = ColorData(node.text.color), ["wrap"] = node.text.enableWordWrapping,
                            ["overflow"] = (int)node.text.overflowMode };
                    })) };
            case MachineryRadioButton radio:
                var option=CaptureRadioOption(radio);
                option["kind"]="radioButton"; option["on"]=radio.isOn;
                return option;
            case IndicatorElement indicator:
                return new JObject { ["kind"] = "indicator", ["fields"] = FieldData(indicator,IndicatorFields) };
            case MachineryRangeSlider range:
                return new JObject { ["kind"] = "rangeSlider", ["min"] = range.min, ["max"] = range.max,
                    ["step"] = range.step, ["low"] = range.low, ["high"] = range.high, ["disabled"] = range.isDisabled,
                    ["description"] = range.description, ["fill"] = range.fillWidth,
                    ["w"] = (int)ControlFields.RangeWidth.GetValue(range),
                    ["requestedW"] = (int)ControlFields.RangeRequestedWidth.GetValue(range) };
            case AdvButtonElement button:
                return new JObject { ["kind"] = "advButton", ["text"] = button.text,
                    ["description"] = button.description, ["hidden"] = button.isHidden,
                    ["w"] = (int)ControlFields.AdvButtonWidth.GetValue(button),
                    ["h"] = (int)ControlFields.AdvButtonHeight.GetValue(button),
                    ["normal"] = SpriteKey(button.spriteNormal,required:false),
                    ["highlight"] = SpriteKey(button.spriteHighlight,required:false),
                    ["clicked"] = SpriteKey(button.spriteClicked,required:false),
                    ["disabled"] = SpriteKey(button.spriteDisabled,required:false) };
            case PushButtonElement push:
                return new JObject { ["kind"] = "push", ["text"] = push.text, ["color"] = (int)push.GetColor(),
                    ["textColor"] = (int)push.GetTextColor(), ["light"] = push.IsLight(), ["down"] = push.IsDown(),
                    ["toggle"] = (bool)ControlFields.PushIsToggle.GetValue(push),
                    ["locked"] = (bool)ControlFields.PushIsLocked.GetValue(push),
                    ["w"] = (int)ControlFields.PushWidth.GetValue(push),
                    ["h"] = (int)ControlFields.PushHeight.GetValue(push) };
            case MachineryLabel label:
                return new JObject { ["kind"] = "label", ["text"] = label.GetText(),
                    ["icon"] = SpriteKey((Sprite)ControlFields.LabelIcon.GetValue(label)),
                    ["width"] = (int)ControlFields.LabelWidth.GetValue(label) };
            case MachineryTabs tabs:
                return new JObject { ["kind"] = "tabs", ["selected"] = tabs.selected,
                    ["renameBay"] = NativeStorageTabs.TryNativeOwner(tabs,out var renameBay) ? Context?.Id(renameBay) ?? 0 : 0,
                    ["w"] = (int)ControlFields.TabsRequestedWidth.GetValue(tabs),
                    ["h"] = (int)ControlFields.TabsRequestedHeight.GetValue(tabs),
                    ["pages"] = new JArray(Enumerable.Range(0, tabs.count).Select(n => new JObject {
                        ["caption"] = tabs.GetCaption(n), ["page"] = CaptureElement(tabs.GetPage(n)) })) };
            case MachinerySlider slider:
                return new JObject { ["kind"] = "slider", ["min"] = slider.min, ["max"] = slider.max,
                    ["step"] = slider.step, ["value"] = slider.value, ["disabled"] = slider.isDisabled,
                    ["description"] = slider.description, ["fill"] = slider.fillWidth,
                    ["w"] = (int)ControlFields.SliderWidth.GetValue(slider),
                    ["requestedW"] = (int)ControlFields.SliderRequestedWidth.GetValue(slider) };
            case GridPixelElement grid:
                var cells = new JArray();
                for (int y = 0; y < grid.gridHeight; y++)
                    for (int x = 0; x < grid.gridWidth; x++) cells.Add(CaptureElement(grid.GetElement(x, y)));
                return new JObject { ["kind"] = "layoutGrid", ["w"] = grid.gridWidth, ["h"] = grid.gridHeight,
                    ["center"] = grid.isCentered, ["justify"] = grid.justifyWidth, ["priority"] = grid.searchUsingPriority,
                    ["spaceX"] = (int)ControlFields.GridXSpacing.GetValue(grid),
                    ["spaceY"] = (int)ControlFields.GridYSpacing.GetValue(grid),
                    ["minimums"] = JArray.FromObject((ValueTuple<int,int>[])ControlFields.GridChildMinimumSize.GetValue(grid)),
                    ["cells"] = cells };
            default: return CaptureUnsupportedWidget(element);
        }
    }

    private static JObject CaptureDeclaredStorage(PixelElement wrapper, ICoopNativeStorageLayout layout)
    {
        if (wrapper is not GraphNodeStorage owner || wrapper is GameInventory ||
            wrapper.GetType().Assembly == typeof(SelectableElement).Assembly ||
            !ModLoader.Mods.Any(mod => mod?.IsEnabled == true && mod.Assembly == wrapper.GetType().Assembly))
            throw new NotSupportedException("Cooperative storage must be declared by its active custom graph owner.");

        var slots = layout.CoopStorage;
        var children = owner.children;
        if (slots == null || slots.Count == 0 || slots.Count > 24 || children == null || children.Count != slots.Count)
            throw new NotSupportedException("Cooperative storage must declare every direct native inventory exactly once.");

        var visited = new HashSet<GameInventory>();
        var rows = new JArray();
        foreach (var slot in slots)
        {
            if (slot == null || !visited.Add(slot) ||
                slot.GetType() != typeof(GameSlotInventory) && slot.GetType() != typeof(GameGridInventory) &&
                slot.GetType() != typeof(GameGridScrollableInventory) ||
                !children.Any(child => ReferenceEquals(child, slot)) ||
                slot.parents?.Any(parent => ReferenceEquals(parent, wrapper)) != true)
                throw new NotSupportedException("A declared cooperative slot is not native storage owned by the layout.");
            rows.Add(CaptureElement((PixelElement)slot));
        }

        // Match the standard native stack record. This carries real storage
        // IDs, not the wrapper's callbacks or custom transfer behaviour.
        return new JObject { ["kind"] = "stack", ["vertical"] = true, ["gap"] = 4,
            ["align"] = (int)MachineryStack.Align.Start, ["mainAlign"] = (int)MachineryStack.Align.Start,
            ["children"] = rows };
    }

    private static JObject CaptureUnsupportedWidget(PixelElement element)
    {
        // Irreplaceable storage and child layouts must still fail validation.
        // A third-party selectable leaf can fall back to an inert native button.
        if (element is not SelectableElement selectable || element is not MachineryWidget ||
            element is MachineryContainer || element is GraphNodeStorage ||
            element.GetType().Assembly == typeof(SelectableElement).Assembly ||
            selectable.handler == null ||
            selectable.handler.GetComponentsInChildren<TreeNodeRender>(true).Any(node =>
                node.window != null || node.element != null && !ReferenceEquals(node.element, element)))
            throw new NotSupportedException("Native layout adapter missing: " + element.GetType().FullName);

        return new JObject
        {
            ["kind"] = "button",
            ["text"] = "Co-op",
            ["description"] = "此控件需要联机适配 / This control needs a co-op adapter.",
            ["audio"] = "",
            ["icon"] = null,
            ["faces"] = new JArray(JValue.CreateNull(), JValue.CreateNull(), JValue.CreateNull(), JValue.CreateNull()),
            ["fill"] = false,
            ["w"] = Math.Clamp(selectable.widthPixels, 1, 4096),
            ["h"] = Math.Clamp(selectable.heightPixels, 1, 4096),
            ["disabled"] = true,
            ["hidden"] = selectable.isHidden
        };
    }

    private static PixelElement ReadExtraLayout(JObject data)
    {
        switch ((string)data["kind"])
        {
            case "tag":
                var tag = FindExisting(data) as TagElement ?? new TagElement();
                ApplyFields(tag, TagElementFields, (JObject)data["fields"]);
                // Fixed local field set, not fields named by a remote sender.
                var names = ControlFields.TagLayerNames;
                for (int i = 0; i < names.Length; i++)
                {
                    var layer = (JObject)((JArray)data["layers"])[i];
                    if ((string)layer["name"] != names[i]) throw new InvalidOperationException("Tag text layer contract mismatch");
                    var node = (TreeNodeRender)ControlFields.TagLayers[i].GetValue(tag);
                    node.text.text = (string)layer["text"]; node.text.enabled = (bool)layer["enabled"];
                    node.text.fontSize = (float)layer["size"]; node.text.fontStyle = (TMPro.FontStyles)(int)layer["style"];
                    node.text.color = ReadColor((JArray)layer["color"]).Value;
                    node.text.enableWordWrapping = (bool)layer["wrap"];
                    node.text.overflowMode = (TMPro.TextOverflowModes)(int)layer["overflow"];
                }
                tag.Validate(); return tag;
            case "radioButton":
                var radio=FindExisting(data) as MachineryRadioButton ?? new MachineryRadioButton((string)data["text"],(bool)data["on"]);
                ApplyRadioOption(radio,data,(bool)data["on"]);
                return radio;
            case "indicator":
                var indicator = FindExisting(data) as IndicatorElement ?? new IndicatorElement();
                ApplyFields(indicator, IndicatorFields, (JObject)data["fields"]);
                indicator.Validate(); return indicator;
            case "rangeSlider":
                var range = FindExisting(data) as MachineryRangeSlider ?? new MachineryRangeSlider(0, 1, (int)data["w"]);
                bool keepRange=NativeWorld.Guest?.EditingContinuous(range)==true || NativeWorld.DraggingContinuous(range);
                range.SetRange((float)data["min"], (float)data["max"], (float)data["step"]).SetFillWidth((bool)data["fill"]);
                if(!keepRange) range.SetValues((float)data["low"], (float)data["high"], false);
                if(range.isDisabled!=(bool)data["disabled"]) range.SetDisabled((bool)data["disabled"]);
                range.description = (string)data["description"];
                range.ResizePixels((int)data["requestedW"], range.heightPixels); return range;
            case "advButton":
                var advanced = FindExisting(data) as AdvButtonElement;
                var normal = ReadSprite(data["normal"] as JObject) ??
                    RenderHandler.LoadFromAtlas("Window/elements_inner", "default");
                int width = (int)data["w"], height = (int)data["h"];
                if (advanced == null)
                    advanced = new AdvButtonElement(normal, (string)data["text"], width, height);
                else
                    advanced.Set((string)data["text"]).ResizePixels(width, height);
                advanced.SetSpriteHighlight(ReadSprite(data["highlight"] as JObject))
                    .SetSpriteClicked(ReadSprite(data["clicked"] as JObject))
                    .SetSpriteDisabled(ReadSprite(data["disabled"] as JObject))
                    .SetSpriteNormal(normal).SetDescription((string)data["description"]);
                // Content callbacks have no validated shared action contract.
                advanced.Disable();
                if ((bool)data["hidden"]) advanced.Hide(); else advanced.Show();
                return advanced;
            case "push":
                var push = (FindExisting(data) as PushButtonElement ?? new PushButtonElement()).SetText((string)data["text"], (int)data["w"], (int)data["h"])
                    .SetColor((IndicatorElement.IndicatorColor)(int)data["color"]).SetTextColor((IndicatorElement.IndicatorColor)(int)data["textColor"]);
                if ((bool)data["light"]) push.Light(); else push.Dark();
                ControlFields.PushIsToggle.SetValue(push, (bool)data["toggle"]);
                // No onDown/onUp callback exists on this replica; do not invoke activation
                // even for layout reconstruction. Preserve the visual latch directly.
                ControlFields.PushIsDown.SetValue(push, (bool)data["down"]);
                foreach (var field in ControlFields.PushNodes)
                {
                    var node = (TreeNodeRender)field.GetValue(push);
                    node.rectTransform.pivot = (bool)data["down"] ? new Vector2(1,0) : new Vector2(0,1);
                    node.rectTransform.localScale = (bool)data["down"] ? new Vector3(-1,-1,1) : Vector3.one;
                }
                if ((bool)data["locked"]) push.Lock(); else push.Unlock();
                return push;
            case "label":
                var label = FindExisting(data) as MachineryLabel ?? new MachineryLabel();
                var labelIcon=(Sprite)ControlFields.LabelIcon.GetValue(label);
                if (data["icon"] is JObject icon)
                {
                    var sprite=ReadSprite(icon);
                    if(labelIcon!=sprite || label.GetText()!=null) label.SetIcon(sprite);
                }
                else if(label.GetText()!=(string)data["text"] || labelIcon!=null) label.SetText((string)data["text"]);
                return label.SetWidth((int)data["width"]);
            case "tabs":
                var oldTabs=FindExisting(data) as MachineryTabs;
                int localTab=oldTabs?.selected ?? (int)data["selected"];
                var pages=((JArray)data["pages"]).Cast<JObject>().ToArray();
                var contents=pages.Select(page=>ReadElement(page["page"] as JObject)).ToArray();
                bool samePages=oldTabs!=null && oldTabs.count==pages.Length &&
                    Enumerable.Range(0,pages.Length).All(index=>ReferenceEquals(oldTabs.GetPage(index),contents[index]));
                // Data inside a tab changes while the player is working. Keep
                // the strip, focus, hover and local selected page in that case.
                var tabs = samePages ? oldTabs : new MachineryTabs();
                for(int tabIndex=0;tabIndex<pages.Length;tabIndex++)
                {
                    string caption=(string)pages[tabIndex]["caption"];
                    if(!samePages) tabs.AddTab(caption,contents[tabIndex]);
                    else if(tabs.GetCaption(tabIndex)!=caption) tabs.SetCaption(tabIndex,caption);
                }
                NativeStorageTabs.BindReplica(tabs,(uint?)data["renameBay"] ?? 0);
                tabs.Select(Math.Max(0,Math.Min(tabs.count-1,localTab)), false); tabs.ResizePixels((int)data["w"], (int)data["h"]); return tabs;
            case "slider":
                var slider = FindExisting(data) as MachinerySlider ?? new MachinerySlider(0, (int)data["w"]);
                bool keepSlider=NativeWorld.Guest?.EditingContinuous(slider)==true || NativeWorld.DraggingContinuous(slider);
                slider.SetRange((float)data["min"], (float)data["max"], (float)data["step"]).SetFillWidth((bool)data["fill"]);
                if(!keepSlider) slider.SetValue((float)data["value"], false);
                if(slider.isDisabled!=(bool)data["disabled"]) slider.SetDisabled((bool)data["disabled"]);
                slider.description = (string)data["description"];
                slider.ResizePixels((int)data["requestedW"], slider.heightPixels); return slider;
            case "layoutGrid":
                var grid=FindExisting(data) as GridPixelElement;
                if(grid!=null && (grid.gridWidth!=(int)data["w"] || grid.gridHeight!=(int)data["h"]))
                { grid.DetachAll(); grid=null; }
                grid ??= new GridPixelElement((int)data["w"], (int)data["h"]);
                grid.isCentered=(bool)data["center"];
                grid.justifyWidth=(bool)data["justify"]; grid.searchUsingPriority=(bool)data["priority"];
                grid.SetSpacing((int)data["spaceX"], (int)data["spaceY"]);
                var children=((JArray)data["cells"]).Select(cell=>ReadElement(cell as JObject)).ToArray();
                // Detach changed cells before attaching any replacement: swaps
                // otherwise fail the original storage single-parent check.
                int index = 0;
                for (int y = 0; y < grid.gridHeight; y++)
                    for (int x = 0; x < grid.gridWidth; x++)
                    {
                        var old=grid.GetElement(x,y);
                        if(old!=null && !ReferenceEquals(old,children[index])) grid.Detach(old);
                        index++;
                    }
                index=0;
                for(int y=0;y<grid.gridHeight;y++)
                    for(int x=0;x<grid.gridWidth;x++)
                    {
                        var child=children[index++];
                        if(child!=null && !ReferenceEquals(grid.GetElement(x,y),child) && !grid.AttachPos(child,x,y))
                            throw new InvalidOperationException("Replica layout grid could not attach its child");
                    }
                var minimums = ((JArray)data["minimums"]).ToObject<ValueTuple<int,int>[]>();
                Array.Copy(minimums, (ValueTuple<int,int>[])ControlFields.GridChildMinimumSize.GetValue(grid), minimums.Length);
                grid.Validate(); return grid;
            default: throw new InvalidOperationException("Unknown declarative layout kind");
        }
    }
    private static JArray ColorData(Color? color) => color.HasValue
        ? new JArray(color.Value.r, color.Value.g, color.Value.b, color.Value.a) : null;
    private static Color? ReadColor(JArray color) => color == null ? (Color?)null
        : new Color((float)color[0], (float)color[1], (float)color[2], (float)color[3]);
}
