using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class FastTooltip
    {
        internal GameItemElement Item;
        internal RichText Normal,Advanced;
        internal ulong Sequence;
        internal double Expires;
        internal int Day;
        internal string Scene;
    }
    private FastTooltip fastRead;
    internal ulong CurrentFastObservation => fastRead?.Sequence ?? 0;
    private ulong lastFastObservation;
    private readonly TooltipSnapshotWatermark<GameItemElement,SentTooltip> outstandingReads=new TooltipSnapshotWatermark<GameItemElement,SentTooltip>();
    private sealed class SentTooltip : IEquatable<SentTooltip>
    {
        internal readonly int Day;
        internal readonly string Scene;
        private readonly RichText normal,advanced;
        internal SentTooltip(int day,string scene,RichText normal,RichText advanced)
        {
            Day=day; Scene=scene;
            this.normal=Copy(normal); this.advanced=NativeMirrorProbe.SameTooltip(normal,advanced) ? this.normal : Copy(advanced);
        }
        private static RichText Copy(RichText t)=>new RichText(t.nakedText,t.richTextStrikethrough,t.richTextUnderline,
            t.richTextForeground,t.richTextBackground,t.recommendedWidth,t.recommendedHeight,t.isTruncated);
        internal bool Matches(RichText a,RichText b)=>NativeMirrorProbe.SameTooltip(normal,a) && NativeMirrorProbe.SameTooltip(advanced,b);
        public bool Equals(SentTooltip other)=>other!=null && Day==other.Day && Scene==other.Scene && Matches(other.normal,other.advanced);
    }
    private bool SnapshotMatchesRead(GameItemElement item,SentTooltip read) =>
        StoreStation.Instance.dayCounter==read.Day && (MapUIManager.Instance.Current?.Id ?? "")==read.Scene &&
        captureTooltips.TryGetValue(item,out var pair) && read.Matches(pair.Normal,pair.Advanced);
    private double nextFastRead;
    private static readonly UTF8Encoding ReadEncoding=new UTF8Encoding(false,true);

    private void PublishFastTooltip()
    {
        double now=Time.realtimeSinceStartupAsDouble;
        if(remoteHover==0 || remoteInterest==0 || remoteInterest==servedInterest || now<nextFastRead ||
            now>=viewsExpiry) return;
        nextFastRead=now+0.06;
        servedInterest=remoteInterest;
        if(!model.Objects.TryGetValue(remoteHover,out var value) || value is not GameItemElement item) return;
        foreach(var ticket in tooltipTickets)
        {
            if(!ReferenceEquals(ticket.Item,item)) continue;
            if(!Attached(ticket)) return;
            using var timing=NativePerformance.Measure("host.focused_read",NativePerformance.FrameWork.Tooltip);
            var pair=EvaluateTooltip(item);
            // The complete world snapshot always carries both variants. Very
            // large descriptions take that bounded chunked path exclusively.
            if(TextLength(pair.Normal)>ReplicaLink.MaxTooltip || TextLength(pair.Advanced)>ReplicaLink.MaxTooltip) return;
            var reply=new JObject { ["id"]=remoteHover,["day"]=StoreStation.Instance.dayCounter,
                ["scene"]=MapUIManager.Instance.Current?.Id ?? "",["normal"]=NativeMirrorProbe.TooltipData(pair.Normal),
                ["advanced"]=PackAdvanced(pair.Normal,pair.Advanced) };
            var json=reply.ToString(Formatting.None);
            if(ReadEncoding.GetByteCount(json)<=ReplicaLink.MaxTooltip)
            {
                ulong seq=++lastFastObservation;
                outstandingReads.Sent(seq,item,new SentTooltip((int)reply["day"],(string)reply["scene"],pair.Normal,pair.Advanced));
                link.PublishTooltip(remoteInterest,seq,ReadEncoding.GetBytes(json));
            }
            return;
        }
    }
    private static long TextLength(RichText text) =>
        (long)(text.nakedText?.Length ?? 0)+(text.richTextForeground?.Length ?? 0)+(text.richTextBackground?.Length ?? 0)+
        (text.richTextUnderline?.Length ?? 0)+(text.richTextStrikethrough?.Length ?? 0);

    private void ReceiveFastTooltip(uint interest,ulong observation,byte[] payload)
    {
        if(closed || !Ready || lastHover==0 || !readOrder.Accepts(interest,observation) || Time.realtimeSinceStartupAsDouble>=readUntil ||
            !model.Objects.TryGetValue(lastHover,out var value) || !ReferenceEquals(value,readItem)) return;
        try
        {
            var data=JObject.Parse(ReadEncoding.GetString(payload));
            int day=StoreStation.Instance.dayCounter;
            string scene=MapUIManager.Instance.Current?.Id ?? "";
            if((uint)data["id"]!=lastHover || (int)data["day"]!=day || (string)data["scene"]!=scene ||
                data["normal"] is not JObject text || !ValidReadText(text) ||
                data["advanced"] is JObject details && !ValidReadText(details)) return;
            var normal=ReadTooltip(text);
            fastRead=new FastTooltip { Item=readItem,Normal=normal,Advanced=ReadAdvanced(normal,data["advanced"]),Sequence=observation,
                Expires=Time.realtimeSinceStartupAsDouble+0.5,Day=day,Scene=scene };
            readOrder.Received(observation);
            if(NativeBackgroundQa.Enabled)
                UnityEngine.Debug.Log($"[probably_coop QA] applied_tooltip entity={lastHover} interest={interest} observation={observation}");
            NativePerformance.Sample("guest.fresh_tooltip",(Time.realtimeSinceStartupAsDouble-readStarted)*1000);
        }
        catch(Exception error) when(error is JsonException or DecoderFallbackException or ArgumentException or
            InvalidCastException or OverflowException or FormatException or System.IO.InvalidDataException)
        { /* Invalid auxiliary reads leave the original snapshot in place. */ }
    }
    private static bool ValidReadText(JObject text) => text.Count==8 &&
        ReadString(text["nakedText"]) && ReadString(text["richTextForeground"]) && ReadString(text["richTextBackground"]) &&
        ReadString(text["richTextUnderline"]) && ReadString(text["richTextStrikethrough"]) &&
        text["recommendedWidth"]?.Type==JTokenType.Integer && text["recommendedHeight"]?.Type==JTokenType.Integer &&
        text["isTruncated"]?.Type==JTokenType.Boolean;
    private static bool ReadString(JToken value) => value?.Type is JTokenType.String or JTokenType.Null;
    private void ApplyTooltipWatermark(ulong through)
    {
        readOrder.Applied(through);
        if(fastRead!=null && (fastRead.Sequence<=through || !ReferenceEquals(fastRead.Item,readItem))) fastRead=null;
    }
    internal RichText CurrentFastTooltip(GameItemElement item,bool advanced)
    {
        if(fastRead==null || Time.realtimeSinceStartupAsDouble>=fastRead.Expires || !ReferenceEquals(fastRead.Item,item) ||
            model.ReplicaId(item)!=lastHover || fastRead.Day!=StoreStation.Instance.dayCounter ||
            fastRead.Scene!=(MapUIManager.Instance.Current?.Id ?? "")) return null;
        return advanced ? fastRead.Advanced : fastRead.Normal;
    }
}
