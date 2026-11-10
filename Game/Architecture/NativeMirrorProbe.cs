using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Declarative native item and UI graph projection, shared by production
/// replication and the independent native parity probes. Guest constructors create
/// renderers only; native item factories and business delegates remain on the host.</summary>
internal static partial class NativeMirrorProbe
{
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly FieldInfo[] ItemFields = typeof(GameItem).GetFields(Fields).Where(f => !f.IsStatic && Scalar(f.FieldType)).ToArray();
    private static readonly FieldInfo[] TagFields = typeof(TagState).GetFields(Fields).Where(f => !f.IsStatic && Scalar(f.FieldType)).ToArray();
    private static readonly NativeTagRow.Layout TagLayout=NativeTagRow.GetLayout(TagFields);
    private static readonly Func<TagState,NativeTagRow> FreezeTag=CaptureTag;
    private static readonly bool VerifyFrozenTags=NativeBackgroundQa.IsHostProduct &&
        Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa")>=0;
    private static int verifiedTagRows;
    private static int verifiedInheritedTags;
    private static int verifiedInheritedShapes;
    private static NativeTagRow CaptureTag(TagState tag)
    {
        var row=new NativeTagRow(tag.valueEnabled,tag.valueString,tag.valueInt,tag.valueFloat,
            tag.valueLong,tag.valueDouble,tag.valueBool,tag.identifier,tag.identifierName);
        if(VerifyFrozenTags && verifiedTagRows<64)
        {
            for(int i=0;i<TagFields.Length;i++)
                if(!JToken.DeepEquals(NativeSnapshotCodec.Scalar(TagFields[i].GetValue(tag)),NativeSnapshotCodec.Scalar(row.Value(TagLayout.Columns[i]))))
                    throw new InvalidOperationException("Frozen native tag contract differs from reflection: "+TagFields[i].Name);
            if(++verifiedTagRows==64) Debug.Log("[probably_coop QA] PASS typed-tag-capture 64 native rows match every original reflected scalar in order");
        }
        return row;
    }
    private static readonly FieldInfo[] FeatureFields = typeof(ItemFeature).GetFields(Fields).Where(f => !f.IsStatic && Scalar(f.FieldType)).ToArray();
    private static readonly FieldInfo[] ConditionFields = typeof(ItemCondition).GetFields(Fields).Where(f => !f.IsStatic && Scalar(f.FieldType)).ToArray();
    private static readonly FieldInfo[] InventoryFields = typeof(GameInventory).GetFields(Fields).Where(f => !f.IsStatic && Scalar(f.FieldType)).ToArray();
    private static readonly FieldInfo[] TagElementFields = typeof(TagElement).GetFields(Fields).Where(f => !f.IsStatic && !f.IsInitOnly && Scalar(f.FieldType)).ToArray();
    private static readonly FieldInfo[] IndicatorFields = typeof(IndicatorElement).GetFields(Fields).Where(f => !f.IsStatic && !f.IsInitOnly && Scalar(f.FieldType)).ToArray();
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings {
        TypeNameHandling = TypeNameHandling.None, MaxDepth = 64
    });
    private static bool Scalar(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);
    private static JObject FieldData(object obj, FieldInfo[] contract)
    {
        if(Context?.Deferred!=null) return Context.Deferred.Capture(obj,contract);
        var data = new JObject();
        foreach (var field in contract)
        {
            var value = field.GetValue(obj);
            data[field.Name] = NativeSnapshotCodec.Scalar(value);
        }
        return data;
    }
    private static void ApplyFields(object obj, FieldInfo[] contract, JObject data)
    {
        if (contract.Length != data.Count) throw new InvalidOperationException("field contract mismatch");
        foreach (var field in contract)
        {
            if (!data.TryGetValue(field.Name, out var value)) throw new InvalidOperationException("missing data field");
            field.SetValue(obj, value.Type == JTokenType.Null ? null : value.ToObject(field.FieldType, Serializer));
        }
    }
    private static JObject Tags(TagSystem tags,JObject basis=null)
    {
        if(Context?.Deferred!=null)
        {
            using var timing=NativePerformance.Measure(TagLayout!=null ? "host.tags_typed" : "host.tags_reflection");
            return TagLayout!=null ? Context.Deferred.CaptureTags(tags.dict,FreezeTag,TagLayout,basis) : Context.Deferred.CaptureMap(tags.dict,TagFields);
        }
        var data = new JObject();
        foreach (var pair in tags.dict) data[pair.Key] = FieldData(pair.Value, TagFields);
        return data;
    }
    private static TagSystem ReadTags(JObject data,JObject basis=null)
    {
        var tags = new TagSystem();
        foreach (var p in data.Properties())
        {
            var tag = new TagState(p.Name);
            ApplyFields(tag, TagFields,NativeTagRow.Resolve(p,basis));
            tags.dict.Add(p.Name, tag);
        }
        return tags;
    }
    internal static JObject ChoiceShape(GridShape shape)=>Shape(shape);
    internal static GridShapeBuilder ReadChoiceShape(JObject data)=>ReadShape(data);
    private static JObject Shape(GridShape shape,JObject basis=null)
    {
        var fields=Context?.Deferred;
        if(fields?.PresentationRows==true)
            return fields.CaptureShape(shape,shape.minX,shape.minY,shape.width,shape.height,shape.flipped,shape.orientation,
                shape.GetOutsideBounds(),WriteCells,basis);
        if(shape.width<0 || shape.height<0 || shape.width>128 || shape.height>128)
            throw new InvalidOperationException("Invalid bounded native shape capture");
        JToken bitmap=fields==null ? ShapeCells(shape) :
            fields.CaptureCells(shape,checked(shape.width*shape.height),WriteCells,basis?["cells"] as JArray);
        return new JObject { ["x"] = shape.minX, ["y"] = shape.minY, ["w"] = shape.width,
            ["h"] = shape.height, ["flip"] = shape.flipped, ["rotation"] = shape.orientation,
            ["outside"] = shape.GetOutsideBounds(), ["cells"] = bitmap };
    }
    internal static JArray ShapeCells(GridShape shape,NativeDeferredFields deferred=null)
    {
        if(shape.width<0 || shape.height<0 || shape.width>128 || shape.height>128)
            throw new InvalidOperationException("Invalid bounded native shape capture");
        if(deferred!=null) return (JArray)deferred.CaptureCells(shape,shape.width*shape.height,WriteCells);
        var cells=new JArray();
        for(int y=0;y<shape.height;y++) for(int x=0;x<shape.width;x++) cells.Add(shape.GetLocal(x,y));
        return cells;
    }
    private static readonly NativeDeferredFields.FillCells<GridShape> WriteCells=FillShapeCells;
    private static void FillShapeCells(GridShape shape,Span<byte> bytes)
    {
        int index=0;
        for(int y=0;y<shape.height;y++) for(int x=0;x<shape.width;x++) bytes[index++]=shape.GetLocal(x,y);
    }
    private static GridShapeBuilder ReadShape(JObject data,JObject basis=null)
    {
        int width=(int)data["w"],height=(int)data["h"],rotation=(int)data["rotation"];
        var source=data["cells"];
        if(source?.Type==JTokenType.Boolean && !(bool)source) source=basis?["cells"];
        if(width<0 || height<0 || width>128 || height>128 || rotation<0 || rotation>3 ||
            source is not JArray cells || cells.Count!=width*height)
            throw new InvalidOperationException("Invalid bounded native shape");
        var shape = new GridShapeBuilder((int)data["x"], (int)data["y"], (int)data["w"], (int)data["h"],
            (bool)data["flip"], (int)data["rotation"]);
        // Pure shape buffer: never invoke inventory insertion or game item callbacks.
        AccessTools.Field(typeof(GridShapeBuilder), "data").SetValue(shape,cells.ToObject<byte[]>(Serializer));
        shape.outside = (byte)data["outside"];
        return shape;
    }
    private static JObject Feature(ItemFeature feature,NativeModelContext identities,bool capture)
    {
        var data=new JObject {
            ["fields"]=FieldData(feature,FeatureFields),
            ["real"]=feature.realCondition==null ? null : FieldData(feature.realCondition,ConditionFields),
            ["fake"]=feature.fakeCondition==null ? null : FieldData(feature.fakeCondition,ConditionFields)
        };
        if(identities!=null)
        {
            uint id=identities.Id(feature);
            data["entity"]=id;
            // Editor proof requests only read the live feature values and identity.
            if(capture) { identities.Objects[id]=feature; identities.Seen.Add(id); }
        }
        return data;
    }
    private static ItemFeature ReadFeature(JObject data)
    {
        if(FindUnchanged(data) is ItemFeature same) return same;
        var feature=FindExisting(data) as ItemFeature ?? new ItemFeature();
        ApplyFields(feature, FeatureFields, (JObject)data["fields"]);
        feature.realCondition=null; feature.fakeCondition=null;
        if (data["real"] is JObject real) { feature.realCondition = new ItemCondition(); ApplyFields(feature.realCondition, ConditionFields, real); }
        if (data["fake"] is JObject fake) { feature.fakeCondition = new ItemCondition(); ApplyFields(feature.fakeCondition, ConditionFields, fake); }
        Remember(feature,data);
        return feature;
    }
    internal static JObject FeatureData(ItemFeature feature,NativeModelContext identities) => Feature(feature,identities,false);
    internal static JObject Capture(GameItem item)
    {
        // Controls belong to the nearest item in the data graph, including
        // equipment carried inside another container. Renderer visibility does
        // not determine business ownership. Restore the enclosing item after
        // recursively capturing a child (also when native queries throw).
        var context=Context;
        var previous=context?.CaptureOwner;
        try
        {
            if(context!=null) context.CaptureOwner=item;
            return CaptureCurrentItem(item);
        }
        finally { if(context!=null) context.CaptureOwner=previous; }
    }
    private static JObject CaptureCurrentItem(GameItem item)
    {
        // Native query callbacks may lazily initialise tags/names. Run the
        // capture-scoped preparation before projecting those fields, including
        // items nested in machine/container windows.
        using(NativePerformance.Detailed ? NativePerformance.Measure("host.item_prepare") : default)
            Context?.CapturingItem?.Invoke(item);
        var fields=Context?.Deferred;
        JObject state,modified,shape,placed,values,content,examine;
        JArray types,features;
        using(NativePerformance.Detailed ? NativePerformance.Measure("host.item_scalars") : default)
        {
            state=Tags(item.state); modified=Tags(item.modifiedState,state);
            shape=Shape(item.shape); placed=Shape(item.modifiedShape,shape);
            values=CaptureItemFields(item);
            types=fields?.CaptureStrings(item.itemTypes) ?? JArray.FromObject(item.itemTypes,Serializer);
        }
        using(NativePerformance.Detailed ? NativePerformance.Measure("host.item_features") : default)
            features=new JArray(item.itemFeatures.Select(feature=>Feature(feature,Context,true)));
        // Inclusive: nested inventories recursively capture their own items.
        using(NativePerformance.Detailed ? NativePerformance.Measure("host.item_windows") : default)
        { content=CaptureWindow(item.contentWindow); examine=CaptureWindow(item.examineWindow); }
        var spriteModifiers=CaptureSpriteModifiers(item);
        if(fields?.WorkerItems==true)
            return Annotate(item,fields.StageItem(new NativeDeferredFields.ItemRow {
                Fields=values,State=state,Modified=modified,Shape=shape,ModifiedShape=placed,
                Types=types,Features=features,Content=content,Examine=examine,SpriteModifiers=spriteModifiers }));
        var data = new JObject {
            ["fields"]=values, ["state"]=state, ["modified"]=modified,
            ["shape"]=shape, ["modifiedShape"]=placed, ["types"]=types,
            ["features"]=features, ["content"]=content, ["examine"]=examine
        };
        if(spriteModifiers!=null) data["spriteModifiers"]=spriteModifiers;
        return Annotate(item,data);
    }
    internal static JObject CaptureValues(GameItem item) => new JObject {
        ["fields"] = FieldData(item, ItemFields), ["state"] = Tags(item.state), ["modified"] = Tags(item.modifiedState)
    };
    private static JObject CaptureWindow(PixelWindow window)
    {
        if (window == null) return null;
        return Annotate(window, new JObject { ["w"] = window.widthPixels, ["h"] = window.heightPixels,
            ["move"] = window.canMove, ["style"] = (int)window.style, ["title"] = window.titleString,
            ["xp"] = window.xPadding, ["yp"] = window.yPadding, ["element"] = CaptureElement(window.childElement) });
    }
    private static PixelWindow ReadWindow(JObject data)
    {
        if (data == null) return null;
        if (FindUnchanged(data) is PixelWindow same) return same;
        var existing=FindExisting(data) as PixelWindow;
        var window = existing ?? new PixelWindow((int)data["w"], (int)data["h"], (bool)data["move"], (int)data["xp"], (int)data["yp"],
            (string)data["title"], (WindowStyle)(int)data["style"]);
        var child=ReadElement((JObject)data["element"]);
        // Attach detaches/reparents even the same child and relinks its storage
        // graph. Live machine readouts must not rebuild the actor's open window.
        if(!ReferenceEquals(window.childElement,child)) window.Attach(child);
        bool resize=existing==null || PropertyChanged(data,"w") || PropertyChanged(data,"h"),
            padding=existing==null || PropertyChanged(data,"xp") || PropertyChanged(data,"yp"),
            title=existing==null || PropertyChanged(data,"title");
        if(resize || padding || title) NativeReplicaGraph.WindowLayout(()=> {
            if(padding) { window.xPadding=(int)data["xp"]; window.yPadding=(int)data["yp"]; }
            if(resize) window.ResizePixels((int)data["w"],(int)data["h"]);
            // Classic SetTitle dereferences its argument; normalize a missing
            // title and let the native implementation measure on this client.
            if(title) window.SetTitle((string)data["title"] ?? "");
            else if(padding) window.Validate();
            // Embedded content may currently render under a stolen-UI slot.
            // Reflow that actual parent after changed window geometry only.
            NativeReplicaGraph.NotifySizeChanged(window.childElement);
        });
        Remember(window, data);
        return window;
    }
    private static JObject CaptureElement(PixelElement element)
    {
        if (element == null) return null;
        JObject data;
        switch (element)
        {
            case GameGridScrollableInventory scroll:
                data=new JObject { ["kind"]="scrollGrid",["fields"]=FieldData(scroll,InventoryFields),
                    ["w"]=scroll.width,["h"]=scroll.height,["displayH"]=scroll.heightPixels,
                    ["stack"]=scroll.alwaysStack,["overflow"]=(float)AccessTools.Field(typeof(GameGridScrollableInventory),"scrollOverflow").GetValue(scroll),
                    ["items"]=new JArray(scroll.childItems.Select(Capture)) }; break;
            case GameGridInventory grid:
                data = new JObject { ["kind"] = "grid", ["fields"] = FieldData(grid, InventoryFields),
                    ["shape"] = Shape(grid.inventoryShape), ["items"] = new JArray(grid.childItems.Select(Capture)),
                    ["machinery"] = grid.isMachineryStyle }; break;
            case GameSlotInventory slot:
                var slotImage = (TreeNodeRender)AccessTools.Field(typeof(GameSlotInventory), "backgroundImage").GetValue(slot);
                data = new JObject { ["kind"] = "slot", ["fields"] = FieldData(slot, InventoryFields),
                    ["w"] = (int)AccessTools.Field(typeof(GameSlotInventory), "defaultGridWidth").GetValue(slot),
                    ["h"] = (int)AccessTools.Field(typeof(GameSlotInventory), "defaultGridHeight").GetValue(slot),
                    ["steal"] = slot.stealUI, ["down"] = slot.isUIDown, ["machinery"] = slot.isMachineryStyle,
                    ["image"] = SpriteKey(slotImage?.image.sprite),
                    ["color"] = slotImage == null ? null : new JArray(slotImage.image.color.r, slotImage.image.color.g, slotImage.image.color.b, slotImage.image.color.a),
                    ["transparency"] = slot.transparency, ["hidden"] = slot.isHidden,
                    ["items"] = new JArray(slot.childItems.Select(Capture)) }; break;
            case MachineryPanelRow row:
                data = new JObject { ["kind"] = "row", ["columns"] = row.columns,
                    ["order"] = JToken.FromObject((int[])AccessTools.Field(typeof(MachineryPanelRow), "displayOrder").GetValue(row) ?? Array.Empty<int>()),
                    ["priority"] = row.searchUsingPriority, ["panels"] = new JArray(row.Panels.Select(CaptureElement)) }; break;
            case MachineryPanel panel:
                data = new JObject { ["kind"] = "panel", ["role"] = panel.role, ["label"] = CaptureElement(panel.label),
                    ["requestedW"] = (int)AccessTools.Field(typeof(MachineryPanel), "requestedWidth").GetValue(panel),
                    ["requestedH"] = (int)AccessTools.Field(typeof(MachineryPanel), "requestedHeight").GetValue(panel),
                    ["minW"] = (int)AccessTools.Field(typeof(MachineryPanel), "minWidth").GetValue(panel),
                    ["hidden"] = panel.isHidden, ["content"] = CaptureElement(panel.content) }; break;
            case MachineryText text:
                var textNode = (TreeNodeRender)AccessTools.Field(typeof(MachineryText), "textNode").GetValue(text);
                data = new JObject { ["kind"] = "text", ["text"] = text.GetText(), ["fill"] = text.fillWidth,
                    ["size"] = textNode.text.fontSize, ["style"] = (int)textNode.text.fontStyle,
                    ["align"] = (int)textNode.text.horizontalAlignment,
                    ["minHeight"] = (int)AccessTools.Field(typeof(MachineryText), "minHeight").GetValue(text) }; break;
            case MachineryIntake intake:
                data = new JObject { ["kind"] = "intake", ["vents"] = intake.vents,
                    ["style"] = (bool)AccessTools.Field(typeof(MachineryIntake), "styleInventories").GetValue(intake),
                    ["content"] = CaptureElement(intake.content) }; break;
            case MachineryRadioList radios:
                data = new JObject { ["kind"] = "radio", ["selected"] = radios.selected,
                    ["options"] = new JArray(radios.group.buttons.Select(CaptureRadioOption)) }; break;
            case MachineryStack stack:
                data = new JObject { ["kind"] = "stack", ["vertical"] = stack.vertical, ["gap"] = stack.gap,
                    ["align"] = (int)stack.align, ["mainAlign"] = (int)stack.mainAlign,
                    ["children"] = new JArray(stack.elements.Select(CaptureElement)) }; break;
            case MachineryButton button:
                data = new JObject { ["kind"] = "button", ["text"] = button.text, ["description"] = button.description,
                    ["audio"] = button.onClickAudio,
                    ["icon"] = SpriteKey(button.icon),
                    ["faces"] = new JArray(new[] { "faceNormal", "faceHover", "facePressed", "faceDisabled" }
                        .Select(n => SpriteKey((Sprite)AccessTools.Field(typeof(MachineryButton), n).GetValue(button)))),
                    ["fill"] = button.fillWidth, ["w"] = (int)AccessTools.Field(typeof(MachineryButton), "forceWidth").GetValue(button),
                    ["h"] = (int)AccessTools.Field(typeof(MachineryButton), "forceHeight").GetValue(button),
                    ["disabled"] = button.isDisabled, ["hidden"] = button.isHidden }; break;
            default: return Annotate(element, CaptureExtraLayout(element));
        }
        if(element is GameInventory inventory) data["inventoryState"]=CaptureInventoryState(inventory);
        return Annotate(element, data);
    }
    private static readonly FieldInfo RadioTextColor=AccessTools.Field(typeof(MachineryRadioButton),"textColor");
    private static JObject CaptureRadioOption(MachineryRadioButton button)
    {
        var tint=(Color?)RadioTextColor.GetValue(button);
        return new JObject { ["text"]=button.text, ["description"]=button.description, ["audio"]=button.onClickAudio,
            ["disabled"]=button.isDisabled, ["hidden"]=button.isHidden,
            ["color"]=ColorData(tint) };
    }

    private static MachineryRadioList ReadRadioList(JObject data)
    {
        var options=(JArray)data["options"];
        int selected=(int)data["selected"];
        if(selected < -1 || selected>=options.Count) throw new InvalidOperationException("Invalid radio selection");
        var radio=FindExisting(data) as MachineryRadioList;
        if(radio==null || radio.group.buttons.Count!=options.Count)
            radio=new MachineryRadioList(options.Select(o=>(string)o["text"]).ToArray(),selected);
        for(int i=0;i<options.Count;i++)
        {
            var option=options[i];
            ApplyRadioOption(radio.Get(i),option,i==selected);
        }
        return radio;
    }
    private static void ApplyRadioOption(MachineryRadioButton button,JToken option,bool on)
    {
        if(button.text!=(string)option["text"]) button.SetText((string)option["text"]);
        button.SetDescription((string)option["description"]);
        button.onClickAudio=(string)option["audio"];
        if(button.isDisabled!=(bool)option["disabled"]) button.SetDisabled((bool)option["disabled"]);
        if(button.isHidden!=(bool)option["hidden"]) button.SetHidden((bool)option["hidden"]);
        var color=ReadColor(option["color"] as JArray);
        if(!Nullable.Equals((Color?)RadioTextColor.GetValue(button),color)) button.SetTextColor(color);
        button.SetOn(on,false);
    }

    private static PixelElement ReadElement(JObject data)
    {
        if (data == null) return null;
        if (FindUnchanged(data) is PixelElement same) return same;
        var previous=FindExisting(data) as PixelElement;
        int oldWidth=previous?.widthPixels ?? 0,oldHeight=previous?.heightPixels ?? 0;
        bool oldFill=previous is MachineryButton oldButton && oldButton.fillWidth;
        var result = ReadElementCore(data);
        // Reusing a parent must still propagate genuine layout size changes.
        // Value-only updates of equal dimensions avoid a whole-tree reattach.
        if(ReferenceEquals(previous,result) && (result.widthPixels!=oldWidth || result.heightPixels!=oldHeight ||
            result is MachineryButton button && button.fillWidth!=oldFill))
            NativeReplicaGraph.NotifySizeChanged(result);
        Remember(result, data);
        return result;
    }
    private static PixelElement ReadElementCore(JObject data)
    {
        if (data == null) return null;
        switch ((string)data["kind"])
        {
            case "scrollGrid":
                int scrollWidth=(int)data["w"],scrollHeight=(int)data["h"],displayHeight=(int)data["displayH"];
                if(scrollWidth<1 || scrollWidth>128 || scrollHeight<1 || scrollHeight>4096 || displayHeight<1 || displayHeight>4096)
                    throw new InvalidOperationException("Scrollable inventory size invalid");
                var scrolling=FindExisting(data) as GameGridScrollableInventory ?? new GameGridScrollableInventory(scrollWidth,1,scrollHeight,(float)data["overflow"]);
                scrolling.SetShape(scrollWidth,1,scrollHeight); scrolling.ResizePixels(scrolling.widthPixels,displayHeight);
                scrolling.alwaysStack=(bool)data["stack"]; ApplyFields(scrolling,InventoryFields,(JObject)data["fields"]);
                ApplyInventoryState(scrolling,(JObject)data["inventoryState"]);
                var scrollChildren=new List<GameItem>();
                foreach(JObject childData in (JArray)data["items"]) { var child=Hydrate(childData); AttachReplica(child,scrolling); scrollChildren.Add(child); }
                foreach(var child in scrolling.childItems.ToArray()) if(!scrollChildren.Contains(child)) DetachReplica(child);
                NativeWorld.OrderReplicaInventory(scrolling,scrollChildren);
                return scrolling;
            case "grid":
                var shape = ReadShape((JObject)data["shape"]);
                var grid = FindExisting(data) as GameGridInventory ?? new GameGridInventory(shape.width, shape.height);
                ApplyFields(grid, InventoryFields, (JObject)data["fields"]);
                ApplyInventoryState(grid,(JObject)data["inventoryState"]);
                AccessTools.Property(typeof(GameGridInventory), nameof(GameGridInventory.inventoryShape)).SetValue(grid, shape);
                if ((bool)data["machinery"]) grid.SetMachineryStyle();
                var gridChildren=new List<GameItem>();
                foreach (JObject itemData in (JArray)data["items"])
                {
                    var child = Hydrate(itemData); AttachReplica(child,grid); gridChildren.Add(child);
                }
                foreach(var child in grid.childItems.ToArray()) if(!gridChildren.Contains(child)) DetachReplica(child);
                NativeWorld.OrderReplicaInventory(grid,gridChildren);
                if(PropertyChanged(data,"shape") || PropertyChanged(data,"machinery"))
                {
                    NativeReplicaGraph.Validate(grid);
                    NativeReplicaGraph.NotifySizeChanged(grid);
                }
                return grid;
            case "slot":
                var slot = FindExisting(data) as GameSlotInventory ?? new GameSlotInventory((int)data["w"], (int)data["h"], (bool)data["steal"], (bool)data["down"]);
                ApplyFields(slot, InventoryFields, (JObject)data["fields"]);
                ApplyInventoryState(slot,(JObject)data["inventoryState"]);
                slot.stealUI=(bool)data["steal"]; slot.isUIDown=(bool)data["down"];
                if (data["image"] is JObject slotImage)
                {
                    var color = (JArray)data["color"];
                    slot.SetBackgroundSprite((string)slotImage["atlas"], (string)slotImage["sprite"],
                        new Color((float)color[0], (float)color[1], (float)color[2], (float)color[3]));
                }
                slot.SetTransparency((float)data["transparency"]);
                if (slot.isHidden!=(bool)data["hidden"]) { if((bool)data["hidden"]) slot.Hide(); else slot.Show(); }
                if ((bool)data["machinery"]) slot.SetMachineryStyle();
                if (((JArray)data["items"]).Count > 0)
                {
                    var child = Hydrate((JObject)data["items"][0]);
                    if(slot.childItem!=null && !ReferenceEquals(slot.childItem,child)) DetachReplica(slot.childItem);
                    AttachReplica(child,slot);
                }
                else if(slot.childItem!=null) DetachReplica(slot.childItem);
                if(new[]{"w","h","steal","down","image","color","transparency","hidden","machinery"}.Any(p=>PropertyChanged(data,p)))
                {
                    NativeReplicaSlotPresentation.Refresh(slot);
                    NativeReplicaGraph.Validate(slot);
                    NativeReplicaGraph.NotifySizeChanged(slot);
                }
                return slot;
            case "row":
                var row = FindExisting(data) as MachineryPanelRow ?? new MachineryPanelRow();
                row.searchUsingPriority=(bool)data["priority"];
                var panels=((JArray)data["panels"]).Cast<JObject>().Select(p=>(MachineryPanel)ReadElement(p)).ToArray();
                if(!row.Panels.SequenceEqual(panels))
                {
                    foreach(var previous in row.Panels.ToArray()) row.Detach(previous);
                    foreach(var panel in panels) row.Add(panel);
                }
                if(PropertyChanged(data,"order")) row.SetDisplayOrder(data["order"].ToObject<int[]>());
                if(PropertyChanged(data,"columns")) row.SetColumns((int)data["columns"]);
                return row;
            case "panel":
                var nativePanel=FindExisting(data) as MachineryPanel ?? new MachineryPanel();
                nativePanel.SetRole((string)data["role"]);
                var panelContent=ReadElement(data["content"] as JObject);
                var panelLabel=(MachineryLabel)ReadElement(data["label"] as JObject);
                if(!ReferenceEquals(nativePanel.content,panelContent)) nativePanel.SetContent(panelContent);
                if(!ReferenceEquals(nativePanel.label,panelLabel)) nativePanel.SetLabel(panelLabel);
                if(PropertyChanged(data,"minW")) nativePanel.SetMinWidth((int)data["minW"]);
                if(PropertyChanged(data,"hidden")) nativePanel.SetHidden((bool)data["hidden"]);
                if(PropertyChanged(data,"requestedW") || PropertyChanged(data,"requestedH"))
                    nativePanel.ResizePixels((int)data["requestedW"],(int)data["requestedH"]);
                return nativePanel;
            case "text":
                var text = FindExisting(data) as MachineryText ?? new MachineryText();
                var node = (TreeNodeRender)AccessTools.Field(typeof(MachineryText), "textNode").GetValue(text);
                if(PropertyChanged(data,"style")) node.text.fontStyle=(FontStyles)(int)data["style"];
                if(PropertyChanged(data,"size")) node.text.fontSize=(float)data["size"];
                if(PropertyChanged(data,"align")) text.SetAlignment((HorizontalAlignmentOptions)(int)data["align"]);
                if(PropertyChanged(data,"fill")) text.SetFillWidth((bool)data["fill"]);
                if(PropertyChanged(data,"minHeight")) text.SetMinHeight((int)data["minHeight"]);
                // The native setter also notifies the owning layout when its
                // measured size changes. Keep the text/parent render identities.
                if(PropertyChanged(data,"text") || PropertyChanged(data,"style") || PropertyChanged(data,"size"))
                    text.SetText((string)data["text"]);
                return text;
            case "intake":
                var intake=FindExisting(data) as MachineryIntake;
                if(intake==null || PropertyChanged(data,"style"))
                    return new MachineryIntake(ReadElement(data["content"] as JObject),(bool)data["vents"],(bool)data["style"]);
                var intakeContent=ReadElement(data["content"] as JObject);
                if(!ReferenceEquals(intake.content,intakeContent)) intake.SetContent(intakeContent);
                if(PropertyChanged(data,"vents")) { intake.vents=(bool)data["vents"]; intake.Validate(); }
                return intake;
            case "radio": return ReadRadioList(data);
            case "stack":
                var stack=FindExisting(data) as MachineryStack;
                bool newStack=stack==null || stack.vertical!=(bool)data["vertical"];
                if(newStack) stack=new MachineryStack((bool)data["vertical"],(int)data["gap"]);
                var stackChildren=((JArray)data["children"]).Cast<JObject>().Select(ReadElement).ToArray();
                if(!stack.elements.SequenceEqual(stackChildren))
                {
                    foreach(var previous in stack.elements.ToArray()) stack.Detach(previous);
                    foreach(var childElement in stackChildren) stack.Add(childElement);
                }
                if(newStack || PropertyChanged(data,"gap")) stack.gap=(int)data["gap"];
                if(newStack || PropertyChanged(data,"align")) stack.SetAlign((MachineryStack.Align)(int)data["align"]);
                if(newStack || PropertyChanged(data,"mainAlign")) stack.SetMainAlign((MachineryStack.Align)(int)data["mainAlign"]);
                if(newStack || PropertyChanged(data,"gap")) stack.Validate();
                return stack;
            case "button":
                var oldButton=FindExisting(data) as MachineryButton;
                var button=oldButton ?? new MachineryButton((string)data["text"],null,(int)data["w"],(int)data["h"]);
                if(button.text!=(string)data["text"]) button.SetText((string)data["text"]);
                if(oldButton!=null && (PropertyChanged(data,"w") || PropertyChanged(data,"h"))) button.SetSize((int)data["w"],(int)data["h"]);
                button.SetDescription((string)data["description"]).SetFillWidth((bool)data["fill"]);
                button.onClickAudio=(string)data["audio"];
                if(PropertyChanged(data,"icon") && (oldButton!=null || data["icon"] is JObject)) button.SetIcon(ReadSprite(data["icon"] as JObject));
                var faces=(JArray)data["faces"];
                if(PropertyChanged(data,"faces") && (oldButton!=null || faces[0] is JObject))
                    button.SetFaceSprites(ReadSprite(faces[0] as JObject),ReadSprite(faces[1] as JObject),
                        ReadSprite(faces[2] as JObject),ReadSprite(faces[3] as JObject));
                button.SetDisabled((bool)data["disabled"]); button.SetHidden((bool)data["hidden"]);
                return button;
            default: return ReadExtraLayout(data);
        }
    }
    internal static GameItemElement Hydrate(JObject data)
    {
        if (FindUnchanged(data) is GameItemElement same) return same;
        var values = (JObject)data["fields"];
        var item = FindExisting(data) as GameItemElement ?? new GameItemElement((string)values["<identifier>k__BackingField"],
            (string)values["<spriteAtlasPath>k__BackingField"], (string)values["<spritePath>k__BackingField"]);
        bool fieldsChanged=PropertyChanged(data,"fields"),stateChanged=PropertyChanged(data,"state"),
            modifiedChanged=PropertyChanged(data,"modified"),shapeChanged=PropertyChanged(data,"shape"),
            positionChanged=PropertyChanged(data,"modifiedShape"),typesChanged=PropertyChanged(data,"types"),
            featuresChanged=PropertyChanged(data,"features"), modifiersChanged=PropertyChanged(data,"spriteModifiers");
        if(fieldsChanged)
        {
            ApplyFields(item, ItemFields, values);
            AccessTools.Property(typeof(GameItem), "spriteChanged").SetValue(item, true);
        }
        if(stateChanged) item.state=ReadTags((JObject)data["state"]);
        if(modifiedChanged || stateChanged)
        {
            item.modifiedState=ReadTags((JObject)data["modified"],(JObject)data["state"]);
            if(NativeBackgroundQa.Enabled && verifiedInheritedTags<64)
                foreach(var tag in ((JObject)data["modified"]).Properties())
                {
                    if(tag.Value.Type!=JTokenType.Boolean) continue;
                    var primary=item.state.dict[tag.Name]; var secondary=item.modifiedState.dict[tag.Name];
                    if(ReferenceEquals(primary,secondary) || !CaptureTag(primary).SameValues(CaptureTag(secondary)))
                        throw new InvalidOperationException("Guest inherited tag is not an independent exact native copy");
                    if(++verifiedInheritedTags==64)
                    { Debug.Log("[probably_coop QA] PASS independent-tag-state 64 shared wire rows reconstructed as separate native tags"); break; }
                }
        }
        if(shapeChanged) item.shape=ReadShape((JObject)data["shape"]);
        var placed=(JObject)data["modifiedShape"];
        if(positionChanged || shapeChanged && placed["cells"]?.Type==JTokenType.Boolean)
        {
            item.modifiedShape=ReadShape(placed,(JObject)data["shape"]);
            if(NativeBackgroundQa.Enabled && verifiedInheritedShapes<64 && placed["cells"]?.Type==JTokenType.Boolean)
            {
                var basis=(JArray)data["shape"]["cells"];
                if(ReferenceEquals(item.shape,item.modifiedShape)) throw new InvalidOperationException("Shared mutable native item shape");
                int i=0;
                for(int y=0;y<item.modifiedShape.height;y++) for(int x=0;x<item.modifiedShape.width;x++)
                    if(item.modifiedShape.GetLocal(x,y)!=(byte)basis[i++]) throw new InvalidOperationException("Lost native placement cell");
                if(++verifiedInheritedShapes==64) Debug.Log("[probably_coop QA] PASS independent-shape-cells 64 matching bitmaps restored to independent original placements");
            }
        }
        if(typesChanged) item.itemTypes=data["types"].ToObject<List<string>>(Serializer);
        if(featuresChanged)
        {
            item.itemFeatures=((JArray)data["features"]).Cast<JObject>().Select(ReadFeature).ToList();
        }
        if(modifiersChanged)
        {
            var modifiers=ReadSpriteModifiers(data["spriteModifiers"] as JArray);
            item.getItemSpriteModifierForItemFunc=modifiers==null ? null : _=>modifiers;
        }
        // Assign declarative features; AddItemFeature may execute an on-activate business callback.
        if(PropertyChanged(data,"content"))
        {
            var content=ReadWindow(data["content"] as JObject);
            if(!ReferenceEquals(item.contentWindow,content)) item.SetContentWindow(content);
        }
        if(PropertyChanged(data,"examine"))
        {
            var examine=ReadWindow(data["examine"] as JObject);
            if(!ReferenceEquals(item.examineWindow,examine)) item.SetExamineWindow(examine);
        }
        // ValidateSelect resets drag preview/background transforms. A remote
        // machine label or tooltip update alone must not reset the local hand.
        if(fieldsChanged || stateChanged || modifiedChanged || shapeChanged || positionChanged || typesChanged || featuresChanged || modifiersChanged)
            NativeReplicaGraph.Validate(item);
        Remember(item, data);
        return item;
    }
    internal static object Tooltip(RichText text) => new { text.nakedText, text.richTextForeground,
        text.richTextBackground, text.richTextUnderline, text.richTextStrikethrough,
        text.recommendedWidth, text.recommendedHeight, text.isTruncated };
    internal static bool SameTooltip(RichText a,RichText b) => ReferenceEquals(a,b) ||
        a!=null && b!=null && a.recommendedWidth==b.recommendedWidth && a.recommendedHeight==b.recommendedHeight &&
        a.isTruncated==b.isTruncated && a.nakedText==b.nakedText && a.richTextForeground==b.richTextForeground &&
        a.richTextBackground==b.richTextBackground && a.richTextUnderline==b.richTextUnderline &&
        a.richTextStrikethrough==b.richTextStrikethrough;
    private static int verifiedTooltipFields;
    internal static JObject TooltipData(RichText text,NativeDeferredFields fields=null)
    {
        if(fields?.PresentationRows==true) return fields.CaptureTooltip(text.nakedText,text.richTextForeground,text.richTextBackground,
            text.richTextUnderline,text.richTextStrikethrough,text.recommendedWidth,text.recommendedHeight,text.isTruncated);
        // Preserve the full original rich text and dimensions. Avoid creating
        // a serializer/anonymous object for eight already-known scalar fields.
        var data=new JObject {
            ["nakedText"]=NativeSnapshotCodec.Scalar(text.nakedText),
            ["richTextForeground"]=NativeSnapshotCodec.Scalar(text.richTextForeground),
            ["richTextBackground"]=NativeSnapshotCodec.Scalar(text.richTextBackground),
            ["richTextUnderline"]=NativeSnapshotCodec.Scalar(text.richTextUnderline),
            ["richTextStrikethrough"]=NativeSnapshotCodec.Scalar(text.richTextStrikethrough),
            ["recommendedWidth"]=text.recommendedWidth,["recommendedHeight"]=text.recommendedHeight,["isTruncated"]=text.isTruncated };
        if(NativeBackgroundQa.Enabled && verifiedTooltipFields<64)
        {
            if(!JToken.DeepEquals(data,JObject.FromObject(Tooltip(text)))) throw new InvalidOperationException("Native tooltip fields differ from serializer");
            if(++verifiedTooltipFields==64) Debug.Log("[probably_coop QA] PASS direct-tooltip-fields 64 native texts match full original serializer projection");
        }
        return data;
    }

    internal static object Run()
    {
        if (!NativeArchitectureQa.Active) throw new InvalidOperationException("QA-only probe");
        var cases = new List<object>();
        foreach (var id in new[] { "processed_meat", "processed_juice", "processed_meat_featured", "backpack_small", "heating_plate", "packet_red_cigarette" })
        {
            GameItem source = null; GameItemElement replica = null;
            try
            {
                source = DirectoryMaster.Item(id == "processed_meat_featured" ? "processed_meat" : id);
                GeneralHelper.SetItemOwned(source, true);
                if (id == "processed_meat_featured") source.AddItemFeature(ItemFeatureList.Discount(-15));
                if (id == "backpack_small") source.TryAcceptAll(DirectoryMaster.Item("processed_juice"));
                var sourceText = Tooltip(((GameItemElement)source).GetTooltip());
                var sourceAdvanced = Tooltip(((GameItemElement)source).GetTooltipAdvanced());
                var baselineBounds = RenderTooltip((GameItemElement)source, "tooltip-" + id + "-source");
                var data = Capture(source);
                // Round trip through data, with no shared object/delegate references.
                var bytes = data.ToString(Formatting.None);
                replica = Hydrate(JObject.Parse(bytes));
                var replicaText = Tooltip(replica.GetTooltip());
                var replicaAdvanced = Tooltip(replica.GetTooltipAdvanced());
                var replicaBounds = RenderTooltip(replica, "tooltip-" + id + "-replica");
                bool equal = JToken.DeepEquals(JToken.FromObject(sourceText), JToken.FromObject(replicaText)) &&
                    JToken.DeepEquals(JToken.FromObject(sourceAdvanced), JToken.FromObject(replicaAdvanced));
                var stable = Capture(replica);
                bool dataEqual = JToken.DeepEquals(data, stable);
                var differences = new List<string>(); Diff(data, stable, "item", differences);
                cases.Add(new { id, @case = "tooltip-" + id, status = equal && dataEqual ? "PASS" : "FAIL", evidence = "native full normal/advanced tooltip and declarative graph roundtrip; single process; GPU pixels verified separately; no network/UI-input evidence",
                    dataBytes = System.Text.Encoding.UTF8.GetByteCount(bytes), source = sourceText, replica = replicaText,
                    sourceAdvanced, replicaAdvanced, baselineBounds, replicaBounds,
                    dataEqualAfterNativeRender = dataEqual, dataDifferences = differences,
                    nativeSourceType = source.GetType().FullName,
                    gameplayCallbacks = "not copied or executed on the replica; command adapters still required" });
            }
            catch (Exception e) { cases.Add(new { id, status = "FAIL", error = e.ToString() }); }
            finally { replica?.Destroy(); source?.Destroy(); }
        }
        return new { cases, productionReady = false,
            scope = "Native data-mirror architecture probe. Unknown layouts fail explicitly. No guest replacement UI is created." };
    }
    private static object RenderTooltip(GameItemElement item, string name)
    {
        if (!NativeArchitectureQa.GraphicsRequested) return null;
        var overlay = OverlayHandler.current;
        try
        {
            // Same native renderer used by ItemTooltipHandler; the test supplies a
            // fixed logical position, without synthesizing mouse or keyboard input.
            overlay.ShowTooltip2(new Vector2(Screen.width * .35f, Screen.height * .5f), Vector2.zero,
                item.GetTooltip(), item.IsGameItemType("MACHINE") ? 72 : 60);
            Canvas.ForceUpdateCanvases();
            // PixelWindow's transform is an origin only (zero-sized rect). The
            // original outside border owns the actual complete visible rectangle.
            var border = (TreeNodeRender)AccessTools.Field(typeof(PixelWindow), "outsideBorder")
                .GetValue(overlay.GetTooltipWindow());
            var bounds = NativeOffscreenCapture.Bounds(border.rectTransform);
            NativeArchitectureQa.CaptureFrame(name);
            return bounds;
        }
        finally { overlay.HideTooltip(); }
    }
    internal static void Diff(JToken a, JToken b, string path, List<string> output)
    {
        if (JToken.DeepEquals(a, b)) return;
        if (a is JObject ao && b is JObject bo)
        {
            foreach (var key in ao.Properties().Select(p => p.Name).Union(bo.Properties().Select(p => p.Name)))
                Diff(ao[key], bo[key], path + "." + key, output);
        }
        else if (a is JArray aa && b is JArray ba && aa.Count == ba.Count)
            for (int i = 0; i < aa.Count; i++) Diff(aa[i], ba[i], path + "[" + i + "]", output);
        else output.Add(path + ": expected=" + a?.ToString(Formatting.None) + "; actual=" + b?.ToString(Formatting.None));
    }
}
