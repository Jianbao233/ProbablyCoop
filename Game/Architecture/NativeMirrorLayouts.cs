using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal static partial class NativeMirrorProbe
{
    private sealed class SpriteIdentity
    {
        internal string Atlas, Key;
        internal SpriteIdentity(string atlas,string key) { Atlas=atlas; Key=key; }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Sprite,SpriteIdentity> SpriteIdentities = new System.Runtime.CompilerServices.ConditionalWeakTable<Sprite,SpriteIdentity>();
    private static readonly System.Reflection.FieldInfo AtlasCacheField=AccessTools.Field(typeof(RenderHandler),"atlasCache");
    // Asset references remain declarative atlas/key pairs. No texture bytes, Unity
    // instance ids, native pointers or callback delegates are transferred.
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
        foreach (var atlas in cache.OrderBy(p => p.Key, StringComparer.Ordinal))
            foreach (var entry in atlas.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (entry.Value == sprite)
                {
                    SpriteIdentities.Add(sprite,new SpriteIdentity(atlas.Key,entry.Key));
                    return new JObject { ["atlas"] = atlas.Key, ["sprite"] = entry.Key };
                }
        if(!required) return null;
        throw new NotSupportedException("Sprite lacks a native atlas identity: " + sprite.name);
    }
    private static Sprite ReadSprite(JObject data) => data == null ? null : RenderHandler.LoadFromAtlas((string)data["atlas"], (string)data["sprite"]);

    // A liquid surface, label, or machine overlay is a native render result.
    // Send the resolved layers so the guest can also render them on its held item.
    private const int MaximumItemSpriteModifiers=128;
    private static JArray CaptureSpriteModifiers(GameItem item)
    {
        var layers=item.getItemSpriteModifierForItemFunc?.Invoke(item);
        if(layers==null || layers.Count==0) return null;
        if(layers.Count>MaximumItemSpriteModifiers)
            throw new InvalidOperationException("Too many native item sprite layers");
        return new JArray(layers.Where(layer=>layer.sprite!=null).Select(layer=>new JObject {
            ["image"]=SpriteKey(layer.sprite),["x"]=layer.position.x,["y"]=layer.position.y,
            ["rotation"]=layer.orientation,["flip"]=layer.isFlipped,["priority"]=layer.priority,["mask"]=layer.haveMask,
            ["color"]=new JArray(layer.color.r,layer.color.g,layer.color.b,layer.color.a)
        }));
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
                    ["layers"] = new JArray(new[] { "textNode", "textBackgroundNode", "textUnderlineNode", "textStrikethroughNode" }.Select(name => {
                        var node = (TreeNodeRender)AccessTools.Field(typeof(TagElement), name).GetValue(tag);
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
                    ["w"] = (int)AccessTools.Field(typeof(MachineryRangeSlider), "forceWidth").GetValue(range),
                    ["requestedW"] = (int)AccessTools.Field(typeof(MachineryRangeSlider), "requestedWidth").GetValue(range) };
            case AdvButtonElement button:
                return new JObject { ["kind"] = "advButton", ["text"] = button.text,
                    ["description"] = button.description, ["hidden"] = button.isHidden,
                    ["w"] = (int)AccessTools.Field(typeof(AdvButtonElement), "forceWidth").GetValue(button),
                    ["h"] = (int)AccessTools.Field(typeof(AdvButtonElement), "forceHeight").GetValue(button),
                    ["normal"] = SpriteKey(button.spriteNormal,required:false),
                    ["highlight"] = SpriteKey(button.spriteHighlight,required:false),
                    ["clicked"] = SpriteKey(button.spriteClicked,required:false),
                    ["disabled"] = SpriteKey(button.spriteDisabled,required:false) };
            case PushButtonElement push:
                return new JObject { ["kind"] = "push", ["text"] = push.text, ["color"] = (int)push.GetColor(),
                    ["textColor"] = (int)push.GetTextColor(), ["light"] = push.IsLight(), ["down"] = push.IsDown(),
                    ["toggle"] = (bool)AccessTools.Field(typeof(PushButtonElement), "isToggle").GetValue(push),
                    ["locked"] = (bool)AccessTools.Field(typeof(PushButtonElement), "isLocked").GetValue(push),
                    ["w"] = (int)AccessTools.Field(typeof(PushButtonElement), "forceWidth").GetValue(push),
                    ["h"] = (int)AccessTools.Field(typeof(PushButtonElement), "forceHeight").GetValue(push) };
            case MachineryLabel label:
                return new JObject { ["kind"] = "label", ["text"] = label.GetText(),
                    ["icon"] = SpriteKey((Sprite)AccessTools.Field(typeof(MachineryLabel), "icon").GetValue(label)),
                    ["width"] = (int)AccessTools.Field(typeof(MachineryLabel), "fixedWidth").GetValue(label) };
            case MachineryTabs tabs:
                return new JObject { ["kind"] = "tabs", ["selected"] = tabs.selected,
                    ["renameBay"] = NativeStorageTabs.TryNativeOwner(tabs,out var renameBay) ? Context?.Id(renameBay) ?? 0 : 0,
                    ["w"] = (int)AccessTools.Field(typeof(MachineryTabs), "requestedWidth").GetValue(tabs),
                    ["h"] = (int)AccessTools.Field(typeof(MachineryTabs), "requestedHeight").GetValue(tabs),
                    ["pages"] = new JArray(Enumerable.Range(0, tabs.count).Select(n => new JObject {
                        ["caption"] = tabs.GetCaption(n), ["page"] = CaptureElement(tabs.GetPage(n)) })) };
            case MachinerySlider slider:
                return new JObject { ["kind"] = "slider", ["min"] = slider.min, ["max"] = slider.max,
                    ["step"] = slider.step, ["value"] = slider.value, ["disabled"] = slider.isDisabled,
                    ["description"] = slider.description, ["fill"] = slider.fillWidth,
                    ["w"] = (int)AccessTools.Field(typeof(MachinerySlider), "forceWidth").GetValue(slider),
                    ["requestedW"] = (int)AccessTools.Field(typeof(MachinerySlider), "requestedWidth").GetValue(slider) };
            case GridPixelElement grid:
                var cells = new JArray();
                for (int y = 0; y < grid.gridHeight; y++)
                    for (int x = 0; x < grid.gridWidth; x++) cells.Add(CaptureElement(grid.GetElement(x, y)));
                return new JObject { ["kind"] = "layoutGrid", ["w"] = grid.gridWidth, ["h"] = grid.gridHeight,
                    ["center"] = grid.isCentered, ["justify"] = grid.justifyWidth, ["priority"] = grid.searchUsingPriority,
                    ["spaceX"] = (int)AccessTools.Field(typeof(GridPixelElement), "_xSpacing").GetValue(grid),
                    ["spaceY"] = (int)AccessTools.Field(typeof(GridPixelElement), "_ySpacing").GetValue(grid),
                    ["minimums"] = JArray.FromObject((ValueTuple<int,int>[])AccessTools.Field(typeof(GridPixelElement), "childMinimumSize").GetValue(grid)),
                    ["cells"] = cells };
            default: throw new NotSupportedException("Native layout adapter missing: " + element.GetType().FullName);
        }
    }
    private static PixelElement ReadExtraLayout(JObject data)
    {
        switch ((string)data["kind"])
        {
            case "tag":
                var tag = FindExisting(data) as TagElement ?? new TagElement();
                ApplyFields(tag, TagElementFields, (JObject)data["fields"]);
                // Fixed local field set, not fields named by a remote sender.
                string[] names = { "textNode", "textBackgroundNode", "textUnderlineNode", "textStrikethroughNode" };
                for (int i = 0; i < names.Length; i++)
                {
                    var layer = (JObject)((JArray)data["layers"])[i];
                    if ((string)layer["name"] != names[i]) throw new InvalidOperationException("Tag text layer contract mismatch");
                    var node = (TreeNodeRender)AccessTools.Field(typeof(TagElement), names[i]).GetValue(tag);
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
                AccessTools.Field(typeof(PushButtonElement), "isToggle").SetValue(push, (bool)data["toggle"]);
                // No onDown/onUp callback exists on this replica; do not invoke activation
                // even for layout reconstruction. Preserve the visual latch directly.
                AccessTools.Field(typeof(PushButtonElement), "isDown").SetValue(push, (bool)data["down"]);
                foreach (var field in new[] { "buttonNode", "buttonNode2" })
                {
                    var node = (TreeNodeRender)AccessTools.Field(typeof(PushButtonElement), field).GetValue(push);
                    node.rectTransform.pivot = (bool)data["down"] ? new Vector2(1,0) : new Vector2(0,1);
                    node.rectTransform.localScale = (bool)data["down"] ? new Vector3(-1,-1,1) : Vector3.one;
                }
                if ((bool)data["locked"]) push.Lock(); else push.Unlock();
                return push;
            case "label":
                var label = FindExisting(data) as MachineryLabel ?? new MachineryLabel();
                var labelIcon=(Sprite)AccessTools.Field(typeof(MachineryLabel),"icon").GetValue(label);
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
                Array.Copy(minimums, (ValueTuple<int,int>[])AccessTools.Field(typeof(GridPixelElement), "childMinimumSize").GetValue(grid), minimums.Length);
                grid.Validate(); return grid;
            default: throw new InvalidOperationException("Unknown declarative layout kind");
        }
    }
    private static JArray ColorData(Color? color) => color.HasValue
        ? new JArray(color.Value.r, color.Value.g, color.Value.b, color.Value.a) : null;
    private static Color? ReadColor(JArray color) => color == null ? (Color?)null
        : new Color((float)color[0], (float)color[1], (float)color[2], (float)color[3]);
}
