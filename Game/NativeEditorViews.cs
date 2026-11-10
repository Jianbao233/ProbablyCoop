using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private enum EditorKind { Rename,Labels,Inspection }
    private sealed class EditorView
    { internal GameItem Tool,Target; internal ulong Context; }
    private readonly Dictionary<EditorKind,EditorView> editorViews=new Dictionary<EditorKind,EditorView>();
    private static readonly System.Reflection.FieldInfo LabelTarget=AccessTools.Field(typeof(LabelerUIManager),"curItem");
    private static EditorKind EditorFor(string action)=>action=="rename" ? EditorKind.Rename : action=="labels" ? EditorKind.Labels : EditorKind.Inspection;
    private void OpenEditor(EditorKind kind,GameItem tool,GameItem target)
    {
        editorViews[kind]=new EditorView { Tool=tool,Target=target,Context=observedOperationContext };
        switch(kind)
        {
            case EditorKind.Rename: LabelerBlueUIManager.Instance.OpenUI(target); break;
            case EditorKind.Labels: LabelerUIManager.Instance.OpenUI(target); break;
            case EditorKind.Inspection: InspectionUIManager.Instance.OnInspectItem(target); break;
        }
    }
    private static GameItem EditorTarget(EditorKind kind)=>kind switch {
        EditorKind.Rename=>ReplicaRenameItem.Target,
        EditorKind.Labels=>(GameItem)LabelTarget.GetValue(LabelerUIManager.Instance),
        _=>InspectionUIManager.Instance.currentInspectingItem };
    private bool EditorTool(string action,GameItem target,out GameItem tool)
    {
        var kind=EditorFor(action); tool=null;
        if(!editorViews.TryGetValue(kind,out var view) || view.Context!=observedOperationContext ||
            !ReferenceEquals(view.Target,target) || !ReferenceEquals(EditorTarget(kind),target) || !TryData(view.Tool,out _) ||
            view.Tool.parentInventory==null || !Accessible(view.Tool.parentInventory)) return false;
        tool=view.Tool; return true;
    }
    private static void CloseEditor(EditorKind kind,EditorView view)
    {
        if(!ReferenceEquals(EditorTarget(kind),view.Target)) return;
        switch(kind)
        {
            case EditorKind.Rename: LabelerBlueUIManager.Instance.CloseUI(); StoreUIManager.Instance.StopTyping(); break;
            case EditorKind.Labels: LabelerUIManager.Instance.CloseUI(); break;
            case EditorKind.Inspection: InspectionUIManager.Instance.CloseUI(); break;
        }
    }
    private void ReconcileEditors()
    {
        ReconcileStorageTabEditor();
        foreach(var pair in editorViews.ToArray())
        {
            var view=pair.Value;
            if(view.Context==observedOperationContext && TryData(view.Target,out _) && TryData(view.Tool,out _) &&
                view.Target.parentInventory!=null && Accessible(view.Target.parentInventory) &&
                view.Tool.parentInventory!=null && Accessible(view.Tool.parentInventory)) continue;
            editorViews.Remove(pair.Key); CloseEditor(pair.Key,view);
        }
    }
    private void ClearEditors()
    {
        ClearStorageTabEditor();
        var closing=editorViews.ToArray(); editorViews.Clear();
        // CloseUI may synchronously emit Unity onEndEdit. Retire bindings first
        // so scene teardown cannot submit an edit from the discarded draft.
        foreach(var pair in closing) CloseEditor(pair.Key,pair.Value);
    }
}
