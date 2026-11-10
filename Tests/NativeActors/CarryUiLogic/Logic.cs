using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProbablyCoop;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Eq<T>(T expected, T actual)
    { checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Check " + checks + ": Expected " + expected + ", got " + actual); }
    private static void Throws<T>(Action action) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static int Main(string[] args)
    {
        if (args.Length != 0) return NativeMetadata.Run(args);
        try { Run(); Console.WriteLine("ActorCarryUi STUB LOGIC PASS checks=" + checks + "; no Unity/native gameplay executed"); return 0; }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
    private static EmporiumEntry Setup(bool guest = false)
    {
        NativeActorCarryUi.Reset(); NativeGuest.Active = guest; NativeWorld.Guest = null; NativeActorSession.Current = null;
        MapUIManager.Instance = new MapUIManager(); ItemMouseDragHandler.current = null; ItemMultiSelectHandler.current = null;
        var entry = EmporiumEntry.Instance = new EmporiumEntry();
        var carry = new NativeActorCarry(entry, guest ? NativeActorRun.Slot.Partner : NativeActorRun.Slot.Host);
        if (guest) NativeWorld.Guest = new NativeWorld { Ready = true, HasActorProjection = true, ActorCarry = carry };
        else NativeActorSession.Current = new NativeActorSession { Carry = carry };
        return entry;
    }
    private static void Run()
    {
        var entry = Setup();
        var window = entry.afterhourWindow;
        var row = (MachineryPanelRow)window.childElement;
        var panels = row.Panels.ToArray();
        var left = entry.afterhourPocketSlotInvLeft;
        var backpack = entry.afterhourPocketSlotInvBackpack;
        var right = entry.afterhourPocketSlotInvRight;
        object graphIdentity = row.GraphIdentity;
        var originalPosition = window.rectTransform.anchoredPosition;
        Eq(true, NativeActorCarryUi.Available); Eq(false, NativeActorCarryUi.IsOpen);
        NativeActorCarryUi.Tick();
        Eq(true, panels[0].isHidden); Eq(false, entry.afterhourInventory.isHidden);
        Eq("随身物品", window.titleString); Eq(false, window.IsVisible());
        Eq(graphIdentity, row.GraphIdentity); Eq(true, row.Panels.SequenceEqual(panels));
        Eq(left, ((MachineryIntake)row.GetPanel("pocket1").content).content);
        Eq(backpack, ((MachineryIntake)row.GetPanel("backpack").content).content);
        Eq(right, ((MachineryIntake)row.GetPanel("pocket2").content).content);
        int updates = panels.Sum(panel => panel.Changes) + window.Layouts;
        for (int index = 0; index < 50; index++) NativeActorCarryUi.Tick();
        Eq(updates, panels.Sum(panel => panel.Changes) + window.Layouts);
        Eq(true, NativeActorCarryUi.Toggle()); Eq(true, NativeActorCarryUi.IsOpen);
        Eq(true, NativeActorCarryUi.Toggle()); Eq(false, NativeActorCarryUi.IsOpen);
        left.childItem = new GameItem { parentInventory = left };
        NativeActorCarryUi.Tick(); Eq(true, NativeActorCarryUi.IsOpen); Eq(left, left.childItem.parentInventory);
        NativeActorCarryUi.Toggle(); NativeActorCarryUi.Tick(); Eq(false, NativeActorCarryUi.IsOpen);
        window.Hide(); NativeActorCarryUi.Tick(); Eq(false, window.IsVisible());
        MapUIManager.Instance.IsOutside = true;
        NativeActorCarryUi.Tick();
        Eq(false, NativeActorCarryUi.Available);
        Eq(false, panels[0].isHidden); Eq("Ground", window.titleString); Eq(originalPosition, window.rectTransform.anchoredPosition);
        Eq(false, NativeActorCarryUi.BlocksGround(entry.afterhourInventory)); Eq(false, NativeActorCarryUi.Toggle());
        window.Show(); NativeActorCarryUi.Tick(); Eq(true, window.IsVisible());
        var secondPosition = window.rectTransform.anchoredPosition;
        MapUIManager.Instance.IsOutside = false; window.Hide(); NativeActorCarryUi.Tick();
        Eq(true, NativeActorCarryUi.IsOpen); Eq(true, NativeActorCarryUi.BlocksGround(entry.afterhourInventory));
        Eq(false, NativeActorCarryUi.BlocksGround(left)); Eq(false, NativeActorCarryUi.BlocksGround(backpack));
        Eq(false, NativeActorCarryUi.BlocksGround(new GameGridInventory())); Eq(false, NativeActorCarryUi.BlocksGround(null));
        Eq(graphIdentity, row.GraphIdentity); Eq(true, row.Panels.SequenceEqual(panels));
        Eq(left, left.childItem.parentInventory);
        ItemMouseDragHandler.current = new ItemMouseDragHandler { IsDraggingItem = true };
        Eq(false, NativeActorCarryUi.Toggle()); Eq(true, NativeActorCarryUi.IsOpen);
        ItemMouseDragHandler.current = null;
        ItemMultiSelectHandler.current = new ItemMultiSelectHandler { IsGroupDragging = true };
        Eq(false, NativeActorCarryUi.Toggle()); ItemMultiSelectHandler.current = null;
        CheckPatches(entry.afterhourInventory, true); CheckPatches(left, false);
        NativeActorCarryUi.Reset(); Eq(false, window.IsVisible()); Eq(false, panels[0].isHidden); Eq("Ground", window.titleString);
        Eq(secondPosition, window.rectTransform.anchoredPosition); Eq(true, row.Panels.SequenceEqual(panels));
        Eq(left, entry.afterhourPocketSlotInvLeft); Eq(backpack, entry.afterhourPocketSlotInvBackpack); Eq(right, entry.afterhourPocketSlotInvRight);

        // Reset is view cleanup, while a live cooperative store still rejects
        // additions to hidden shared ground even before its next UI tick.
        Eq(true, NativeActorCarryUi.BlocksGround(entry.afterhourInventory));
        NativeActorSession.Current = null;
        Eq(false, NativeActorCarryUi.Available); Eq(false, NativeActorCarryUi.BlocksGround(entry.afterhourInventory));
        NativeActorCarryUi.Tick(); Eq(false, panels[0].isHidden); CheckPatches(entry.afterhourInventory, false);

        entry = Setup(); entry.afterhourPocketSlotInvBackpack.childItem = new GameItem();
        Eq(true, NativeActorCarryUi.Toggle()); Eq(true, NativeActorCarryUi.IsOpen); // initial auto-open must not invert the click
        NativeActorCarryUi.Reset();
        entry = Setup(); var ground = ((MachineryPanelRow)entry.afterhourWindow.childElement).GetPanel("ground");
        ground.SetHidden(true); entry.afterhourWindow.SetTitle("Customized original");
        NativeActorCarryUi.Tick(); NativeActorCarryUi.Reset();
        Eq(true, ground.isHidden); Eq("Customized original", entry.afterhourWindow.titleString);

        entry = Setup(true); var oldHost = new EmporiumEntry();
        Eq(true, NativeActorCarryUi.Available); Eq(true, NativeActorCarryUi.BlocksGround(entry.afterhourInventory));
        Eq(false, NativeActorCarryUi.BlocksGround(oldHost.afterhourInventory));
        entry.afterhourPocketSlotInvRight.childItem = new GameItem(); NativeActorCarryUi.Tick(); Eq(true, NativeActorCarryUi.IsOpen);
        NativeWorld.Guest.Ready = false; NativeActorCarryUi.Tick(); Eq(false, NativeActorCarryUi.Available);
        Eq(false, ((MachineryPanelRow)entry.afterhourWindow.childElement).GetPanel("ground").isHidden);
        Eq(false, NativeActorCarryUi.BlocksGround(entry.afterhourInventory));
        NativeWorld.Guest.Ready = true; NativeWorld.Guest.HasActorProjection = false;
        Eq(false, NativeActorCarryUi.Available); NativeWorld.Guest.HasActorProjection = true;
        EmporiumEntry.Instance = new EmporiumEntry(); Eq(false, NativeActorCarryUi.Available);
        EmporiumEntry.Instance = entry; NativeWorld.Guest.ActorCarry.IsDisposed = true; Eq(false, NativeActorCarryUi.Available);

        entry = Setup(); entry.afterhourPocketSlotInvLeft = new GameSlotInventory();
        Eq(false, NativeActorCarryUi.Available); // stale bank/native slot replacement
        entry = Setup(); ((MachineryPanelRow)entry.afterhourWindow.childElement).GetPanel("ground").content = new GameGridInventory();
        Throws<InvalidOperationException>(() => NativeActorCarryUi.Tick()); Eq("Ground", entry.afterhourWindow.titleString);
        NativeActorCarryUi.Reset();
        entry = Setup(); var initializedWindow = entry.afterhourWindow; entry.afterhourWindow = null;
        Eq(false, NativeActorCarryUi.Available); NativeActorCarryUi.Tick(); Eq(false, NativeActorCarryUi.Toggle());
        entry.afterhourWindow = initializedWindow; var initializedRow = initializedWindow.childElement; initializedWindow.childElement = null;
        Eq(false, NativeActorCarryUi.Available); NativeActorCarryUi.Tick(); Eq("Ground", initializedWindow.titleString);
        initializedWindow.childElement = initializedRow; NativeActorCarryUi.Tick(); Eq(true, NativeActorCarryUi.Available);
        NativeActorCarryUi.Reset();
        entry = Setup(); ((MachineryIntake)((MachineryPanelRow)entry.afterhourWindow.childElement).GetPanel("pocket1").content).content = entry.afterhourPocketSlotInvRight;
        Throws<InvalidOperationException>(() => NativeActorCarryUi.Tick()); // wrapper does not excuse a foreign slot
        NativeActorCarryUi.Reset();
        entry = Setup(); ((MachineryIntake)((MachineryPanelRow)entry.afterhourWindow.childElement).GetPanel("pocket1").content).content =
            new MachineryIntake { content = entry.afterhourPocketSlotInvLeft };
        Throws<InvalidOperationException>(() => NativeActorCarryUi.Tick()); // reject arbitrary nested aliases
        NativeActorCarryUi.Reset();
    }
    private static void CheckPatches(GameInventory inventory, bool blocks)
    {
        foreach (var type in new[] { typeof(NativeActorCarryGroundQuery), typeof(NativeActorCarryGroundPlayerInsert),
            typeof(NativeActorCarryGroundSearch), typeof(NativeActorCarryGroundMiddleSearch), typeof(NativeActorCarryGroundDrop), typeof(NativeActorCarryGroundUncheckedDrop) })
        {
            // Grid-specific patches can only receive the grid subclass in
            // production; a different native grid is the negative instance.
            var target = inventory is GameGridInventory ? inventory : new GameGridInventory();
            var method = type.GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            Eq(HarmonyLib.Priority.First, method.GetCustomAttribute<HarmonyLib.HarmonyPriority>().Value);
            object initial = type == typeof(NativeActorCarryGroundQuery) || type == typeof(NativeActorCarryGroundPlayerInsert) ? true :
                type == typeof(NativeActorCarryGroundDrop) || type == typeof(NativeActorCarryGroundUncheckedDrop) ? 7 : new SlotMarker();
            bool drop = type == typeof(NativeActorCarryGroundDrop) || type == typeof(NativeActorCarryGroundUncheckedDrop);
            object[] args = { drop ? new SlotMarker { inventory = target } : target, initial };
            Eq(!blocks, (bool)method.Invoke(null, args));
            if (blocks) Eq(initial is bool ? (object)false : initial is int ? 0 : null, args[1]);
            else Eq(initial, args[1]);
        }
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute
    { public HarmonyPatch(Type type, string name) { } public HarmonyPatch(Type type, string name, Type[] args) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPriority : Attribute
    { public int Value; public HarmonyPriority(int value) { Value = value; } }
    public static class Priority { public const int First = 800; }
}
namespace UnityEngine
{
    public readonly struct Vector2
    {
        public readonly float x,y; public Vector2(float x,float y) { this.x=x;this.y=y; }
        public static Vector2 operator +(Vector2 a,Vector2 b) => new Vector2(a.x+b.x,a.y+b.y);
        public override string ToString() => x + "," + y;
    }
    public sealed class GameObject { public bool activeSelf; }
    public sealed class RectTransform { public Vector2 anchoredPosition; }
}
public sealed class TreeNodeRender { public GameObject gameObject = new GameObject(); }
public class GameInventory { public bool isHidden; }
public class GameItem { public GameInventory parentInventory; }
public sealed class GameSlotInventory : GameInventory { public GameItem childItem; }
public sealed class GameGridInventory : GameInventory
{
    public bool MayHaveValidInventorySlot(GameItem item) => true;
    public SlotMarker TryFindOneValidInventorySlot(GameItem item,bool keepOrientation=false) => null;
    public SlotMarker TryFindOneValidInventorySlot_TopMiddle(GameItem item,bool keepOrientation=false) => null;
}
public sealed class SlotMarker
{ public GameInventory inventory; public int TryAcceptOnce(int amount=-1) => 1; public int AcceptUnchecked() => 1; }
public static class GeneralHelper { public static bool MayPlayerInsertInto(GameInventory targetInventory) => true; }
public sealed class PixelWindow
{
    public object childElement; public TreeNodeRender handler = new TreeNodeRender(); public RectTransform rectTransform = new RectTransform();
    public string titleString = "Ground"; public int widthPixels = 100, Layouts;
    public void SetTitle(string value) { titleString = value; Layouts++; }
    public void Show() { handler.gameObject.activeSelf = true; rectTransform.anchoredPosition = new Vector2(0,0); }
    public void Hide() { handler.gameObject.activeSelf = false; }
    public bool IsVisible() => handler.gameObject.activeSelf;
    public void FixBounds() { }
}
public sealed class MachineryPanel
{
    public object content; public string role; public bool isHidden; public TreeNodeRender handler = new TreeNodeRender(); public int Changes;
    public void SetHidden(bool value) { isHidden = value; Changes++; }
}
public sealed class MachineryIntake { public object content; }
public sealed class MachineryPanelRow
{
    public object GraphIdentity = new object(); public List<MachineryPanel> Panels = new List<MachineryPanel>();
    public MachineryPanel GetPanel(string role) => Panels.SingleOrDefault(panel => panel.role == role);
}
public sealed class EmporiumEntry
{
    public static EmporiumEntry Instance;
    public GameSlotInventory afterhourPocketSlotInvLeft = new GameSlotInventory(), afterhourPocketSlotInvRight = new GameSlotInventory(), afterhourPocketSlotInvBackpack = new GameSlotInventory();
    public GameGridInventory afterhourInventory = new GameGridInventory(); public PixelWindow afterhourWindow = new PixelWindow(), invWindow = new PixelWindow();
    public EmporiumEntry()
    {
        var row = new MachineryPanelRow(); afterhourWindow.childElement = row;
        row.Panels.Add(new MachineryPanel { role="ground",content=new MachineryIntake { content=afterhourInventory } });
        row.Panels.Add(new MachineryPanel { role="pocket1",content=new MachineryIntake { content=afterhourPocketSlotInvLeft } });
        row.Panels.Add(new MachineryPanel { role="backpack",content=new MachineryIntake { content=afterhourPocketSlotInvBackpack } });
        row.Panels.Add(new MachineryPanel { role="pocket2",content=new MachineryIntake { content=afterhourPocketSlotInvRight } });
        afterhourWindow.rectTransform.anchoredPosition = new Vector2(30,-120);
    }
}
public sealed class MapUIManager { public static MapUIManager Instance; public bool IsOutside; }
public sealed class ItemMouseDragHandler { public static ItemMouseDragHandler current; public bool IsDraggingItem; }
public sealed class ItemMultiSelectHandler { public static ItemMultiSelectHandler current; public bool IsGroupDragging; }
namespace ProbablyCoop
{
    internal static class NativeGuest { internal static bool Active; }
    internal sealed class NativeWorld { internal static NativeWorld Guest; internal bool Ready, HasActorProjection; internal NativeActorCarry ActorCarry; }
    internal sealed class NativeActorSession { internal static NativeActorSession Current; internal NativeActorCarry Carry; }
    internal static class NativeActorRun { internal enum Slot { Host, Partner } }
    internal static class NativeActorInventory { internal enum CarrySlot { LeftPocket, RightPocket, Backpack } }
    internal sealed class NativeActorCarry
    {
        internal EmporiumEntry Source; internal bool IsDisposed; private readonly NativeActorRun.Slot local;
        private readonly GameSlotInventory left, right, backpack;
        internal NativeActorCarry(EmporiumEntry source,NativeActorRun.Slot actor)
        { Source=source;local=actor;left=source.afterhourPocketSlotInvLeft;right=source.afterhourPocketSlotInvRight;backpack=source.afterhourPocketSlotInvBackpack; }
        internal GameSlotInventory Get(NativeActorRun.Slot actor,NativeActorInventory.CarrySlot slot)
        {
            if(actor!=local) throw new Exception("Must only expose local actor UI");
            return slot switch { NativeActorInventory.CarrySlot.LeftPocket=>left,NativeActorInventory.CarrySlot.RightPocket=>right,_=>backpack };
        }
    }
}
