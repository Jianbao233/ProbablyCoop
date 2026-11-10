using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// Transfer line identity and explicit skips, never host animation frames.
// Each frontend uses its existing native typewriter and local visual settings.
internal sealed partial class NativeWorld
{
    private sealed class SourceBubble
    { internal ulong Line; internal string Text; internal bool Skipped; }
    private sealed class LocalBubble
    {
        internal ulong Line,Started;
        internal string Text;
        internal bool Visible,Rich,Skipped,LocallySkipped,WasActive;
    }
    private readonly Dictionary<TextBubble,SourceBubble> sourceBubbles=new Dictionary<TextBubble,SourceBubble>();
    private readonly Dictionary<TextBubble,LocalBubble> localBubbles=new Dictionary<TextBubble,LocalBubble>();
    private ulong nextBubbleLine;
    private static TextBubble[] NativeBubbles(bool phone)=>phone
        ? new[]{DialoguePhoneUIManager.Instance?.clientBubble,DialoguePhoneUIManager.Instance?.playerBubble}
        : new[]{DialogUIManager.Instance?.clientBubble,DialogUIManager.Instance?.playerBubble,DialogUIManager.Instance?.secBubble};
    private SourceBubble SourceLine(TextBubble bubble)
    {
        if(!sourceBubbles.TryGetValue(bubble,out var row))
        {
            // A room may be created during an existing line. Bootstrap its
            // current text once; ordinary native ShowText events use raw text.
            row=new SourceBubble { Line=checked(++nextBubbleLine),Text=bubble.textfield.text ?? "" };
            sourceBubbles.Add(bubble,row);
        }
        return row;
    }
    private void RecordBubbleLine(TextBubble bubble,string text)
    {
        if(bubble==null) return;
        sourceBubbles[bubble]=new SourceBubble { Line=checked(++nextBubbleLine),Text=text ?? "" };
        nextCapture=0;
    }
    internal void RecordAdvanceSkip(bool phone)
    {
        foreach(var bubble in NativeBubbles(phone))
            if(bubble!=null && bubble.gameObject.activeInHierarchy && bubble.typewriter.isShowingText)
            { SourceLine(bubble).Skipped=true; nextCapture=0; }
    }
    private JObject CaptureNativeBubble(TextBubble bubble)
    {
        var row=SourceLine(bubble); var color=bubble.backgroundImage.color;
        return new JObject { ["line"]=row.Line,["visible"]=bubble.gameObject.activeSelf,["text"]=row.Text,
            ["rich"]=bubble.textfield.richText,["skip"]=row.Skipped,["color"]=new JArray(color.r,color.g,color.b,color.a) };
    }
    private void ApplyNativeBubble(TextBubble bubble,JObject data)
    {
        if(data==null || data["line"]?.Type!=JTokenType.Integer || !ulong.TryParse(data["line"].ToString(),out ulong line) || line==0 ||
            data["text"]?.Type!=JTokenType.String || ((string)data["text"]).Length>32768 ||
            data["visible"]?.Type!=JTokenType.Boolean || data["rich"]?.Type!=JTokenType.Boolean || data["skip"]?.Type!=JTokenType.Boolean ||
            data["color"] is not JArray color || color.Count!=4) throw new InvalidDataException("Invalid native bubble line");
        if(!localBubbles.TryGetValue(bubble,out var row)) localBubbles[bubble]=row=new LocalBubble();
        row.Line=line; row.Text=(string)data["text"]; row.Visible=(bool)data["visible"]; row.Rich=(bool)data["rich"]; row.Skipped=(bool)data["skip"];
        bubble.SetColor(new Color((float)color[0],(float)color[1],(float)color[2],(float)color[3]));
        if(!row.Visible)
        {
            if(bubble.typewriter.isShowingText) bubble.typewriter.StopShowingText();
            bubble.gameObject.SetActive(false); row.WasActive=false;
            return;
        }
        bubble.gameObject.SetActive(true); PumpBubble(bubble,row);
    }
    private static void PumpBubble(TextBubble bubble,LocalBubble row)
    {
        var writer=bubble.typewriter;
        if(!row.Visible || !bubble.gameObject.activeInHierarchy)
        {
            if(writer.isShowingText) writer.StopShowingText();
            row.WasActive=false; return;
        }
        if(row.Started!=row.Line)
        {
            writer.StopShowingText();
            row.Started=row.Line; row.LocallySkipped=false;
            bubble.textfield.richText=row.Rich;
            writer.hideAppearancesOnSkip=false;
            writer.ShowText(row.Text);
            if(!EmporiumOption.Instance.enableTypewriterEffect)
            { writer.hideAppearancesOnSkip=true; writer.SkipTypewriter(); row.LocallySkipped=true; }
        }
        else if(!row.WasActive)
        {
            // Reopening a local phone view does not replay an old line or
            // invoke completion callbacks for a conversation already in flight.
            writer.StopShowingText(); writer.TextAnimator.SetVisibilityEntireText(true,false);
        }
        if(row.Skipped && !row.LocallySkipped)
        { writer.SkipTypewriter(); row.LocallySkipped=true; }
        row.WasActive=true;
    }
    private void PumpBubblePresentation()
    {
        foreach(var pair in localBubbles) if(pair.Key!=null) PumpBubble(pair.Key,pair.Value);
    }
    private bool SkipLocalBubbles(bool phone,out JArray skipped)
    {
        skipped=null; var bubbles=NativeBubbles(phone);
        for(int i=0;i<bubbles.Length;i++)
        {
            var bubble=bubbles[i];
            if(bubble==null || !bubble.gameObject.activeInHierarchy || !bubble.typewriter.isShowingText ||
                !localBubbles.TryGetValue(bubble,out var row) || !row.Visible || row.Started!=row.Line) continue;
            skipped??=new JArray(); skipped.Add(new JObject { ["slot"]=i,["line"]=row.Line });
            bubble.typewriter.SkipTypewriter(); row.LocallySkipped=true;
        }
        return skipped!=null;
    }
    private ushort ExecuteBubbleSkip(bool phone,JArray data)
    {
        var bubbles=NativeBubbles(phone); var selected=new List<TextBubble>(); var seen=new HashSet<int>();
        if(data==null || data.Count<1 || data.Count>bubbles.Length) return (ushort)ShopResult.Stale;
        foreach(var value in data)
        {
            if(value is not JObject row || row["slot"]?.Type!=JTokenType.Integer || row["line"]?.Type!=JTokenType.Integer ||
                !int.TryParse(row["slot"].ToString(),out int slot) || slot<0 || slot>=bubbles.Length || !seen.Add(slot) ||
                !ulong.TryParse(row["line"].ToString(),out ulong line) || bubbles[slot]==null || !bubbles[slot].gameObject.activeSelf ||
                SourceLine(bubbles[slot]).Line!=line) return (ushort)ShopResult.Stale;
            selected.Add(bubbles[slot]);
        }
        // Already-complete source text is an accepted no-op, never Advance.
        if(!phone) remoteDialogueClient=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
        if(selected.Any(bubble=>bubble.typewriter.isShowingText))
        {
            if(phone)
            {
                var ui=DialoguePhoneUIManager.Instance;
                if(ui.currentDialog!=null && !ui.IsChoice()) AccessTools.Field(typeof(DialoguePhoneUIManager),"isSkipCurrentDialog").SetValue(ui,true);
            }
            else
            {
                var ui=DialogUIManager.Instance;
                if(ui.currentDialog!=null && !ui.IsChoice()) AccessTools.Field(typeof(DialogUIManager),"isSkipCurrentDialog").SetValue(ui,true);
            }
        }
        foreach(var bubble in selected)
        { SourceLine(bubble).Skipped=true; if(bubble.typewriter.isShowingText) bubble.typewriter.SkipTypewriter(); }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private void ClearBubblePresentation()
    {
        if(!host) foreach(var bubble in localBubbles.Keys) if(bubble!=null) bubble.typewriter.StopShowingText();
        sourceBubbles.Clear(); localBubbles.Clear();
    }
    internal ulong BubbleLineIdentity(TextBubble bubble)=>host ? SourceLine(bubble).Line :
        localBubbles.TryGetValue(bubble,out var row) ? row.Line : 0;
    internal bool BubbleSkipObserved(TextBubble bubble)=>sourceBubbles.TryGetValue(bubble,out var row) && row.Skipped;
    [HarmonyPatch]
    private static class SourceNativeBubbleLine
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DialogUIManager),nameof(DialogUIManager.DisplayClientText));
            yield return AccessTools.Method(typeof(DialogUIManager),nameof(DialogUIManager.DisplayerPlayerText));
            yield return AccessTools.Method(typeof(DialogUIManager),nameof(DialogUIManager.DisplaySecText));
            yield return AccessTools.Method(typeof(DialogUIManager),nameof(DialogUIManager.PlayClientSingleText));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.DisplayClientText));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.DisplayerPlayerText));
            yield return AccessTools.Method(typeof(DialoguePhoneUIManager),nameof(DialoguePhoneUIManager.PlayClientSingleText));
        }
        private static void Postfix(object __instance,MethodBase __originalMethod,object __0)
        {
            if(NativeGuest.Active || Host==null) return;
            bool phone=__instance is DialoguePhoneUIManager;
            bool player=__originalMethod.Name==nameof(DialogUIManager.DisplayerPlayerText);
            TextBubble bubble=phone ? player ? ((DialoguePhoneUIManager)__instance).playerBubble : ((DialoguePhoneUIManager)__instance).clientBubble :
                player ? ((DialogUIManager)__instance).playerBubble : __originalMethod.Name==nameof(DialogUIManager.DisplaySecText)
                    ? ((DialogUIManager)__instance).secBubble : ((DialogUIManager)__instance).clientBubble;
            string text=__0 is Dialogue dialogue ? dialogue.plainText : phone ? (string)__0 : LocGenderHelper.ResolveForCurrentClient((string)__0);
            Host.RecordBubbleLine(bubble,text);
        }
    }
    [HarmonyPatch(typeof(DialogUIManager),nameof(DialogUIManager.OnCurrentTextDisplayed))]
    private static class ReplicaBubbleCompletion
    { private static bool Prefix()=>!NativeGuest.Active; }
}
