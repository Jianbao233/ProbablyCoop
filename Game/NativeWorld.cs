using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Host-owned native data, client-owned native windows. Only typed requests
/// can cross back into native business methods; factories and saves never run on a guest.</summary>
internal sealed partial class NativeWorld
{
    private const int WorldSchema=21;
    private int captureCompositionSamples;
    private readonly NativeModelContext model = new NativeModelContext();
    private readonly NativeDeferredFields.StockCache frozenStock=new NativeDeferredFields.StockCache();
    private NativeDeferredFields.MarkerPool availableMarkers;
    private readonly bool host;
    private ReplicaLink link;
    private NativeShop shop;
    private ulong applied;
    private NativeReplicaGraph projectingGraph;
    private bool projectionFaulted;
    private double nextCapture;
    private JObject currentClient;
    private string tradeToken = "";
    private string budgetConfirmationToken;
    private Task<DecodedSnapshot> decoding;
    private Task<EncodedSnapshot> encoding;
    private NativeRecordBasis authorityBasis;
    private ulong authorityBaseRevision;
    private EncodedSnapshot awaitingFull;
    private NativeSnapshotIndex guestBasis;
    private byte[] guestBaseBytes;
    private ulong guestBaseRevision;
    private NativeSnapshotIndex appliedIndex;
    private readonly CancellationTokenSource decodeLifetime=new CancellationTokenSource();
    private ulong decodingRevision;
    private ReplicaLink.Stamp decodingStamp, encodingStamp;
    private bool closed;
    private bool peerConnected;
    private ulong peerGeneration,encodingGeneration;
    private NativeCaptureComparison.Bucket encodingComparison;
    private sealed class EncodedSnapshot
    {
        internal byte[] Bytes;
        internal NativeRecordBasis Checkpoint;
        internal int Referenced;
        internal int Elided;
        internal NativeDeferredFields.MarkerPool Markers;
        internal int ReusedMarkers;
        internal ulong MinimumRevision;
        internal Exception Error;
        internal double Milliseconds;
    }
    private sealed class DecodedSnapshot
    {
        internal JObject Data;
        internal NativeSnapshotIndex Index;
        internal NativeSnapshotIndex Basis;
        internal bool Delta;
        internal byte[] BasisBytes;
        internal ulong BasisRevision;
        internal Exception Error;
        internal double Milliseconds;
        internal double DecodeMilliseconds,IndexMilliseconds;
    }
    private static readonly FieldInfo[] ClientFields = typeof(StoreClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => !f.IsStatic && (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(List<string>) || f.FieldType==typeof(double?))).ToArray();
    private static readonly FieldInfo[] WorldFields = typeof(PlayerStore).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => !f.IsStatic && !f.IsInitOnly && (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType==typeof(string)) &&
            !f.Name.EndsWith("InvJSON",StringComparison.Ordinal) &&
            !new[]{"saveSlotId","runID","saveVersion","tutorialEnable","devMode","isProduction","sessionStartTime"}.Contains(f.Name)).ToArray();
    internal static NativeWorld Guest { get; private set; }
    internal static NativeWorld Host { get; private set; }
    // Native UI may read the first complete graph before its public revision
    // is committed. Neither a half-hydrated graph nor a failed publication is
    // input-ready, even when an older revision was successfully applied.
    internal bool Ready => !projectionFaulted && (projectingGraph!=null ? projectingGraph.Readable : applied!=0);
    internal bool HasPendingWork => !Ready || gesture!=null || handActions.Count>0 || releasedHands.Count>0 ||
        cancelledHands.Count>0 || orphanHandRequests.Count>0 || documentReads.Count>0 || documentReadAttempt!=null ||
        placementPreviews.Count>0 || controlPreviews.Count>0 || controlDrafts.Count>0 || continuousPreviews.Count>0 || pendingChoice!=null || HasUiRequests ||
        link?.HasUnacknowledgedRequests==true;
    internal ShopView View { get; private set; }
    internal string[] MorningReport { get; private set; }=Array.Empty<string>();
    internal int MorningReportDay { get; private set; }
    private JArray appliedRoots;
    private static readonly PropertyInfo InventoryShape=AccessTools.Property(typeof(GameGridInventory),nameof(GameGridInventory.inventoryShape));
    private int verifiedRootGrids;
    internal IReadOnlyDictionary<uint, object> Objects => model.Objects;
    internal uint PointerIdentity(object value)=>model.ReplicaId(value);
    internal PixelElement PointerElement(uint id)=>model.Objects.TryGetValue(id,out var value) ? value as PixelElement : null;
    private GameItem captureRoot;
    private readonly Dictionary<GameItemElement,(RichText Normal,RichText Advanced)> captureTooltips=new Dictionary<GameItemElement,(RichText,RichText)>();
    private int verifiedTooltipPairs;
    internal NativeWorld(bool host)
    {
        this.host=host;
        if (host) { Host=this; model.Captured=CaptureExtra; model.CapturingItem=PrepareCaptureTooltips; }
        else { Guest=this; model.Applied=ApplyExtra; }
    }
    internal void Close()
    {
        bool closeSession=!closed;
        closed=true;
        Exception actorError=null;
        try { CloseActorProjection(); }
        catch(Exception error) { actorError=error; }
        // Actor unpatch failures must not retain session delegates/windows. A
        // later Close still retries the retained actor cleanup ownership.
        try { if(closeSession) CloseSession(); }
        catch(Exception error)
        {
            if(actorError!=null) throw new AggregateException("Session and actor cleanup failed",actorError,error);
            throw;
        }
        if(actorError!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(actorError).Throw();
    }

    private void CloseSession()
    {
        CloseShooting();
        ResetDynamicChoice();
        ClearFrontends();
        ClearBubblePresentation();
        NativeAdvanceInput.Reset();
        NativeLocalItemChooser.Close(this);
        if(!host) Debug.Log($"[probably_coop PERF] projection_tracking completed={commandProjection.CompletedCount} pending={commandProjection.Count} expired={commandProjection.Expired} overflowed={commandProjection.Overflowed}");
        commandProjection.Clear();
        uiRequests.Clear();
        controlPreviews.Clear();
        continuousPreviews.Clear(); continuousThrough=0;
        gesture=releasing=null;
        handActions.Clear();
        releasedHands.Clear();
        orphanHandRequests.Clear();
        cancelledHands.Clear();
        RestorePlacementPreviews();
        placementPreviews.Clear();
        ResetPartnerHand();
        ClearTooltipSchedule();
        moveProofs.Clear();
        sortProofs.Clear(); sortProofRows.Clear();
        contentsProofs.Clear(); contentsProofRows.Clear();
        decodeLifetime.Cancel(); decodeLifetime.Dispose();
        decoding=null; encoding=null;
        awaitingFull=null; authorityBasis=null; guestBasis=null; guestBaseBytes=null;
        if(ReferenceEquals(Guest,this)) { ReleaseReplicaCustomer(); Guest=null; }
        if(ReferenceEquals(Host,this)) { EndSharedPhone(); Host=null; }
        if(link!=null)
        {
            if(link.Execute==Execute) link.Execute=null;
            if(link.AcceptPresence==ReceiveViews) link.AcceptPresence=null;
            if(link.AcceptTooltip==ReceiveFastTooltip) link.AcceptTooltip=null;
        }
        tripUi?.Close(); tripUi=null;
    }

    internal void TickHost(NativeShop authority, ReplicaLink connection,ShopLink shopLink)
    {
        if(closed) return;
        BindHost(authority,connection);
        TickShootingAuthority();
        TickPartnerHand();
        if(peerConnected) NativeCaptureComparison.Current?.Advance(Time.realtimeSinceStartupAsDouble);
        ValidateTripReadiness(); TickTripUi();
        if(peerConnected) PublishFastTooltip();
        if(encoding!=null)
        {
            if(!encoding.IsCompleted)
            {
                if(peerConnected && denseTooltips) RefreshDenseTooltips();
                return;
            }
            var packed=encoding.GetAwaiter().GetResult(); encoding=null;
            if(peerConnected && encodingGeneration==peerGeneration && encodingStamp.IsCurrent(connection))
            {
                if(packed.Error!=null) throw new InvalidDataException("Native snapshot encode failed",packed.Error);
                NativePerformance.Sample("host.pack_worker",packed.Milliseconds);
                encodingComparison?.worker.Add(packed.Milliseconds);
                if(packed.Markers!=null) availableMarkers=packed.Markers;
                if(encodingComparison!=null)
                {
                    encodingComparison.elided+=packed.Elided;
                    encodingComparison.reusedMarkers+=packed.ReusedMarkers;
                }
                if(packed.Checkpoint!=null || !connection.NeedsFull)
                {
                    using var publishTiming=NativePerformance.Measure("host.publish");
                    if(packed.Checkpoint!=null)
                    {
                        packed.MinimumRevision=link.PublishCheckpoint(packed.Bytes);
                        awaitingFull=packed;
                    }
                    else link.Publish(packed.Bytes);
                    if(NativeBackgroundQa.Enabled && verifiedRecordPublish<3 && packed.Referenced>0)
                    {
                        verifiedRecordPublish++;
                        Debug.Log($"[probably_coop QA] PASS native-record-publish base={authorityBaseRevision} references={packed.Referenced} bytes={packed.Bytes.Length}");
                    }
                }
            }
        }
        // Keep authoritative callbacks/readiness alive, but there is no reason
        // to traverse and serialize every inventory without a replica peer.
        if(!peerConnected) { nextCapture=0; return; }
        if(awaitingFull!=null)
        {
            if(connection.FullRevision<awaitingFull.MinimumRevision || connection.FullState==null ||
                !connection.FullState.AsSpan().SequenceEqual(awaitingFull.Bytes)) return;
            authorityBasis=awaitingFull.Checkpoint;
            authorityBaseRevision=connection.FullRevision;
            awaitingFull=null;
        }
        if(Time.realtimeSinceStartupAsDouble<nextCapture) { PrefetchTooltipReads(); return; }
        nextCapture=Time.realtimeSinceStartupAsDouble+0.25;
        JObject snapshot;
        ShopView shopView; byte[] shopBytes;
        var comparison=NativeCaptureComparison.Current;
        var fields=new NativeDeferredFields(comparison?.UseRows ?? true,
            (comparison?.ReuseTags ?? denseTooltips) ? frozenStock : null, comparison?.ReuseShapes==false ? null : frozenStock,
            comparison?.WorkerItems ?? true,(comparison?.ReuseItemFields ?? denseTooltips) ? frozenStock : null);
        encodingComparison=comparison?.Measuring;
        fields.EarlyPlain=comparison?.EarlyPlain ?? true;
        if(comparison?.PoolMarkers!=false)
        {
            fields.Markers=availableMarkers ?? new NativeDeferredFields.MarkerPool();
            availableMarkers=null;
        }
        captureTooltips.Clear(); tooltipTickets.Clear(); nextTicket=0;
        mayUsePrefetch=!HasOpaqueTooltipHooks();
        try
        {
            using(new NativeCaptureComparison.CaptureTimer(encodingComparison))
            using(NativePerformance.Measure("host.capture",NativePerformance.FrameWork.Capture)) snapshot=Capture(fields,out shopView,out shopBytes);
            snapshot["tooltipThrough"]=outstandingReads.Capture(SnapshotMatchesRead);
            if(NativePerformance.Detailed && ++captureCompositionSamples%40==1)
                Debug.Log("[probably_coop PERF] capture_composition stagedItems="+fields.CapturedItemRows+
                    " uniqueItems="+model.Objects.Values.Count(value=>value is GameItem)+" uniqueNodes="+model.Objects.Count+
                    " "+TooltipScheduleSummary());
        }
        finally { captureTooltips.Clear(); prefetched.Clear(); capturedControlOwner=null; capturedControlOwnerProof=null; }
        snapshot["commandThrough"]=connection.CompletedRequest;
        snapshot["commandResult"]=connection.CompletedResult;
        snapshot["commandReceipts"]=new JArray(connection.CaptureCommandReceipts().Select(receipt=>new JObject {
            ["serial"]=receipt.Serial,["result"]=receipt.Result }));
        snapshot["guestHand"]=partnerLease.ActiveSerial;
        snapshot["hostDragging"]=ItemMouseDragHandler.current?.IsDraggingItem==true || ItemMultiSelectHandler.current?.IsGroupDragging==true;
        fields.Seal();
        shopLink.ObserveAuthority(authority,shopView,shopBytes,Time.realtimeSinceStartupAsDouble);
        // Transfer the fresh graph plus captured scalar values exclusively to
        // the worker. No host model node or live native object is shared with it.
        // One task, no backlog: when compression is slow, coalesce future captures.
        encodingGeneration=peerGeneration;
        encodingStamp=connection.CaptureStamp();
        var cancellation=decodeLifetime.Token;
        var basis=connection.NeedsFull || connection.Revision-authorityBaseRevision>=96 ? null : authorityBasis;
        ulong baseRevision=authorityBaseRevision;
        var auditBasis=NativeBackgroundQa.Enabled && verifiedRecordPublish<3 ? connection.FullState : null;
        encoding=Task.Run(()=>EncodeSnapshot(snapshot,fields,cancellation,basis,baseRevision,auditBasis));
    }
    // Install command handlers before transport polling. Full capture runs
    // after polling, so it observes those commands in this same Unity tick.
    internal void BindHost(NativeShop authority,ReplicaLink connection)
    {
        if(closed) return;
        shop=authority; link=connection; link.Execute=Execute; link.AcceptPresence=ReceiveViews;
        BindActorSession();
        ObserveOperationContext();
    }
    internal bool TickGuest(ReplicaLink connection, Dictionary<uint,GameItemElement> legacyReplicas)
    {
        if(closed) return false;
        if(NativeBackgroundQa.Enabled) VerifyRetiredGrid();
        link=connection; link.AcceptTooltip=ReceiveFastTooltip;
        commandProjection.Expire(CommandClock());
        CheckCommandReply(); TickGuestHand(); FlushEndingContinue(); FlushWildClose(); FlushNews(); TickContinuousControls();
        FlushControlPreview(); FlushDynamicChoice();
        PumpBubblePresentation(); TickShootingPresentation();
        PublishViews();
        if(decoding==null)
        {
            if(connection.State==null || applied==connection.Revision) return Ready;
            if(NativeRecordFrame.IsDelta(connection.State) && connection.FullState==null) return Ready;
            // ReplicaLink publishes an immutable, complete byte[] only. Never
            // pass Unity objects or the mutable native model to the worker.
            var bytes=connection.State;
            var cancellation=decodeLifetime.Token;
            var previous=appliedIndex;
            var checkpointBytes=connection.FullState;
            ulong basisRevision=connection.FullRevision;
            var knownBasis=guestBaseRevision==basisRevision && ReferenceEquals(checkpointBytes,guestBaseBytes) ? guestBasis : null;
            decodingRevision=connection.Revision;
            decodingStamp=connection.CaptureStamp();
            decoding=Task.Run(()=>DecodeSnapshot(bytes,previous,cancellation,checkpointBytes,basisRevision,knownBasis));
            return Ready;
        }
        if(!decoding.IsCompleted) return Ready;
        var decoded=decoding.GetAwaiter().GetResult();
        ulong snapshotRevision=decodingRevision;
        decoding=null;
        // CoopRuntime retires the guest on an epoch change. Also reject the
        // worker result here before any native/UI assignment, even on a final tick.
        if(!decodingStamp.IsCurrent(connection)) return false;
        if(decoded.Error!=null)
        {
            if(decoded.Delta && decoded.Error is InvalidDataException)
            {
                connection.RequestFull(decoded.BasisRevision);
                return Ready;
            }
            throw new InvalidDataException("Native snapshot decode failed",decoded.Error);
        }
        guestBasis=decoded.Basis; guestBaseRevision=decoded.BasisRevision; guestBaseBytes=decoded.BasisBytes;
        NativePerformance.Sample("guest.unpack_worker",decoded.Milliseconds);
        NativePerformance.Sample("guest.decode_worker",decoded.DecodeMilliseconds);
        NativePerformance.Sample("guest.index_worker",decoded.IndexMilliseconds);
        using var performance=NativePerformance.Measure("guest.apply",NativePerformance.FrameWork.Apply);
        var data=decoded.Data;
        if((int)data["schema"]!=WorldSchema) throw new InvalidDataException("Unknown native world schema");
        if(data["commandThrough"]?.Type!=JTokenType.Integer || !ulong.TryParse(data["commandThrough"].ToString(),out ulong commandThrough))
            throw new InvalidDataException("Invalid native command watermark");
        if(data["commandResult"]?.Type!=JTokenType.Integer || !ushort.TryParse(data["commandResult"].ToString(),out ushort commandResult) ||
            commandResult>(ushort)ShopResult.Failed || (commandThrough==0)!=(commandResult==(ushort)ShopResult.None))
            throw new InvalidDataException("Invalid native command result");
        var receiptProjection=NativeReceiptProjection.Read(data["commandReceipts"],commandThrough,commandResult);
        if(!ShopProtocol.TryDecode(Convert.FromBase64String((string)data["view"]),out var view)) throw new InvalidDataException("Invalid native display metadata");
        if(data["operationContext"]?.Type!=JTokenType.Integer || !ulong.TryParse(data["operationContext"].ToString(),out ulong contextGeneration) || contextGeneration==0)
            throw new InvalidDataException("Invalid native operation context");
        var roots=(JArray)data["roots"];
        var projectionRoots=NativeRootProjection.Read(roots,decoded.Index).Roots;
        var actorProjection=PrepareActorProjection(data,out var actorRunIdentity);
        var personalNotifications=data["personalNotifications"] is JObject
            ? NativeActorPersonalUi.Notifications.Read(data["personalNotifications"],actorRunIdentity) : null;
        var nativeRoots=PhysicalRoots(decoded.Index.ActorRoots).ToDictionary(root=>root.Key,root=>root.Inventory);
        if(projectionRoots.Length!=nativeRoots.Count || projectionRoots.Any(root=>!nativeRoots.ContainsKey(root.Key)))
            throw new InvalidDataException("Incomplete native fixture root set");
        foreach(var root in projectionRoots)
        {
            var inventory=nativeRoots[root.Key];
            var state=(JObject)root.Data["inventoryState"];
            if(inventory==null || inventory is not (GameGridInventory or GameSlotInventory or GameGridScrollableInventory) ||
                (inventory is GameGridInventory)!=decoded.Index.RootGrids.ContainsKey(root.Key) ||
                (inventory is GameSlotInventory)!=(state["steal"]!=null) ||
                inventory is GameSlotInventory && root.Items.Length>1 ||
                model.Objects.TryGetValue(root.Entity,out var previousRoot) && !ReferenceEquals(previousRoot,inventory))
                throw new InvalidDataException("Native inventory fixture does not match ownership envelope");
        }
        NativeReplicaGraph.ValidateWindowIdentities(decoded.Index,model);
        using var projection=new NativeReplicaGraph();
        projectingGraph=projection;
        bool published=false;
        try
        {
            BeginOperationProjection(contextGeneration);
            actorProjection?.Commit();
            actorProjectionRun=actorRunIdentity;
            ApplyWorldFields((JObject)data["world"]);
            MorningReport=data["morningReport"].ToObject<string[]>(); MorningReportDay=(int)data["morningReportDay"];
            Action refreshBusiness;
            using(NativePerformance.Measure("guest.business")) refreshBusiness=ApplyBusinessFacts((JObject)data["business"],decoded.Index.ChangedBusiness);
            documentReadToken=(string)data["documentRead"];
            ApplySharedSceneFacts((JObject)data["scene"]);
            model.Seen.Clear();
            var retained=new HashSet<GameItem>();
            var bootstrap=projectionRoots.SelectMany(root=>nativeRoots[root.Key].childItems)
                .Where(i=>model.ReplicaId(i)==0).Distinct().ToArray();
            legacyReplicas.Clear();
            // Deferred frontend bindings still need the prepared index when the
            // ownership graph is complete. Keep this scope through publication.
            using var projectionContext=model.Enter(decoded.Index);
            foreach(var root in projectionRoots)
            {
                var inventory=nativeRoots[root.Key];
                model.Objects[root.Entity]=inventory; model.Seen.Add(root.Entity);
                NativeMirrorProbe.ApplyInventoryState(inventory,(JObject)root.Data["inventoryState"]);
                if(inventory is GameGridInventory grid && decoded.Index.RootGrids.TryGetValue(root.Key,out var shape))
                    ApplyRootGrid(grid,shape);
                foreach(var row in root.Items)
                {
                    using var itemTiming=NativePerformance.Measure("guest.hydrate_item");
                    var replica=NativeMirrorProbe.Hydrate(row.Data);
                    NativeMirrorProbe.AttachReplica(replica,inventory); retained.Add(replica);
                    legacyReplicas[row.LegacyId]=replica;
                }
            }
            foreach(var root in projectionRoots)
                foreach(var item in nativeRoots[root.Key].childItems.ToArray())
                    if(!retained.Contains(item))
                    {
                        NativeMirrorProbe.DetachReplica(item);
                    }
            foreach(var root in projectionRoots)
                OrderReplicaInventory(nativeRoots[root.Key],root.Items.Select(row=>(GameItem)model.Objects[row.Entity]));
            model.RetainCurrentProjection(decoded.Index);
            foreach(var item in bootstrap) if(!retained.Contains(item)) { NativeMirrorProbe.DetachReplica(item); NativeModelContext.DestroyUntrackedReplica(item); }
            model.Sweep(true);
            sortState=(JObject)data["sorts"];
            var store=PlayerStore.Instance;
            store.playerCash=(int)data["cash"]; StoreStation.Instance.dayCounter=(int)data["day"];
            store.retailMarkup=(int)data["retailMarkup"];
            store.isClientArrived=(bool)data["clientArrived"];
            currentClient=data["client"] as JObject;
            ApplyReplicaCustomer(store,(uint)data["clientInstance"],decoded.Index);
            projection.Flush();
            // This is read readiness, not the published/acknowledged revision.
            projectionFaulted=false;
            projection.AllowReads();
            PresentSharedScene((JObject)data["scene"]);
            BindFixtureReferences();
            projection.Flush();
            ApplyShutter((JObject)data["shutter"]);
            ApplyCustomerMotion((JObject)data["customerMotion"]);
            tradeToken=(string)data["tradeToken"];
            ApplyBargain((JObject)data["bargain"]);
            bool targetChanged=decoded.Index.ChangedTopLevel.Contains("targets");
            targetQueries=(JObject)data["targets"];
            if(targetChanged && AccessTools.Field(typeof(ItemSelectHandler),"currentItem").GetValue(ItemSelectHandler.current) is GameItem selectedTool)
                AccessTools.Method(typeof(ItemSelectHandler),"RefreshHighlights").Invoke(ItemSelectHandler.current,new object[]{selectedTool});
            var ui=NegociationUIManager.Instance;
            if(ui!=null)
            {
                if(ui.currentNegociatedItem!=null && ui.currentNegociatedItem.parentInventory!=EmporiumEntry.Instance.backInvinvElement)
                { ui.currentNegociatedItem=null; store.currentNegociatedItem=null; }
                ui.RefreshUI();
            }
            refreshBusiness?.Invoke();
            ApplyAuxiliary((JObject)data["auxiliary"]);
            RefreshPc(decoded.Index.ChangedBusiness.Contains("requests"));
            ApplyPhone((JObject)data["phone"]);
            ApplyDialogue((JObject)data["dialogue"]);
            ApplyWild((JObject)data["wild"]);
            ApplyEnding((JObject)data["ending"]);
            ApplySecurityScene((JObject)data["securityScene"]);
            ApplyShooting((JObject)data["shooting"]);
            RefreshLabelEditor(decoded.Index,commandThrough);
            ApplyDynamicChoice((JObject)data["choice"]);
            ApplyFrontends((JArray)data["frontends"]);
            ApplyTooltipWatermark((ulong)data["tooltipThrough"]);
            projection.EndBatch();
            // No observable watermark advances until every native view above has
            // accepted this complete projection. Failure leaves the guest closed
            // to input; it is not claimed to be an in-place rollback.
            applied=snapshotRevision;
            appliedIndex=decoded.Index;
            appliedRoots=roots; View=view;
            // Frozen with this graph on the host. A pre-command encode/decode job
            // may complete after its reply; link.Revision cannot prove causality.
            if(connection.ProjectionApplied(commandThrough,commandResult) && NativeBackgroundQa.Enabled)
                Debug.Log($"[probably_coop QA] snapshot_receipt serial={commandThrough} result={commandResult} revision={applied}");
            ConfirmProjectedReceipts(connection,receiptProjection);
            uiRequests.Project(commandThrough);
            CheckCommandReply(true);
            ObserveHandProjection((ulong)data["guestHand"],commandThrough);
            ReapplyPlacementPreviews(commandThrough);
            ReconcileSelections(commandThrough);
            ReconcileFrontends(commandThrough);
            ReconcileContinuous(commandThrough);
            RemoteHostDragging=(bool)data["hostDragging"];
            commandProjection.Applied(commandThrough,CommandClock(),applied);
            published=true;
            if(personalNotifications!=null)
                actorNotifications.Apply(personalNotifications,(text,color)=>StoreUIManager.Instance.Notify(text,color));
            return true;
        }
        finally
        {
            projectionFaulted=!published;
            projectingGraph=null;
        }
    }

    private JObject Capture(NativeDeferredFields fields,out ShopView shopView,out byte[] shopBytes)
    {
        denseTooltips=(HasActorProjection ? CaptureOwnershipGraph().PlacedItemCount : model.Objects.Count)>=DenseTooltipThreshold;
        useNativeAbilityGuards=denseTooltips && ReplicaItemAbility.NativeReturnContractUnmodified();
        var roots=new JArray(); model.Seen.Clear();
        using(model.Enter(fields))
        {
            foreach(var binding in PhysicalRoots(HasActorProjection))
            {
                var inventory=binding.Inventory;
                captureTooltipKey=binding.Key;
                uint rootId=model.Id(inventory); model.Objects[rootId]=inventory; model.Seen.Add(rootId);
                var items=new JArray();
                foreach(var item in inventory.childItems.ToArray())
                {
                    using var itemTiming=NativePerformance.Measure("host.capture_item");
                    if(item==null || item.unitCount<=0) continue;
                    using var itemClassTiming=NativePerformance.Detailed ? NativePerformance.Measure(
                        item.contentWindow==null && item.examineWindow==null && item.itemFeatures.Count==0
                        ? "host.plain_item" : "host.nested_or_featured_item") : default;
                    captureRoot=item;
                    items.Add(new JObject { ["legacyId"]=shop.ItemId(item), ["data"]=NativeMirrorProbe.Capture(item) });
                }
                roots.Add(new JObject { ["place"]=binding.Key.Place, ["key"]=binding.Key.ToString(), ["entity"]=rootId, ["items"]=items,
                    ["inventoryState"]=NativeMirrorProbe.CaptureInventoryState(inventory),
                    ["shape"]=inventory is GameGridInventory grid ? RootShape(grid.inventoryShape,fields) : null });
            }
        }
        model.Sweep(false);
        moveProofs.Retain(model.Seen);
        sortProofs.Retain(model.Seen);
        contentsProofs.Retain(model.Seen);
        var store=PlayerStore.Instance;
        var blueprint=store.currentClientInstance?.GetClientBlueprint();
        var client=CaptureClient(blueprint);
        var customerMotion=CaptureCustomerMotion();
        using(EnterPartnerOperation())
            if(client!=null) client["itemQueries"]=CaptureClientItemQueries(blueprint);
        tradeToken=TradeToken();
        var snapshot=new JObject { ["schema"]=WorldSchema,["roots"]=roots };
        snapshot["customerMotion"]=customerMotion;
        snapshot["operationContext"]=ObserveOperationContext();
        using(NativePerformance.Measure("host.store_panels"))
        {
            snapshot["wild"]=CaptureWild(); snapshot["phone"]=CapturePhone(fields);
            snapshot["dialogue"]=CaptureDialogue(); snapshot["morningReport"]=JArray.FromObject(shop.FullMorningReport);
            snapshot["morningReportDay"]=shop.FullMorningReportDay;
        }
        using(NativePerformance.Measure("host.shop_view"))
        {
            shopView=shop.Capture(); shopBytes=ShopProtocol.Encode(shopView);
            snapshot["view"]=Convert.ToBase64String(shopBytes);
        }
        snapshot["actors"]=CaptureActors();
        snapshot["partnerSetupRequired"]=PartnerSetupRequired();
        snapshot["personalNotifications"]=NativeActorSession.Current?.PersonalUi.CaptureNotifications();
        snapshot["world"]=fields.Capture(store,HasActorProjection ? SharedWorldFields : WorldFields); snapshot["scene"]=CaptureSharedScene();
        snapshot["cash"]=store.playerCash; snapshot["day"]=StoreStation.Instance.dayCounter;
        snapshot["retailMarkup"]=store.retailMarkup; snapshot["clientArrived"]=store.isClientArrived;
        snapshot["client"]=client; snapshot["clientInstance"]=blueprint==null ? 0u : model.Id(store.currentClientInstance);
        snapshot["tradeToken"]=tradeToken;
        using(EnterPartnerOperation()) snapshot["bargain"]=CaptureBargain();
        using(NativePerformance.Measure("host.business")) snapshot["business"]=CaptureBusiness(fields,!HasActorProjection);
        string oracle=NativeBackgroundQa.Enabled && (++verifiedUpgradeInputs%43==1 || wildAwaitingClose) ? WildProof() : null;
        fields.DeferUpgradeProof((JObject)snapshot["wild"],(JObject)snapshot["business"]["upgrades"],
            store.playerCash,store.wildFavor,HashData,oracle,upgradeProofCache);
        snapshot["shutter"]=CaptureShutter();
        using(EnterPartnerOperation())
        using(NativePerformance.Measure("host.target_queries")) snapshot["targets"]=CaptureTargetQueries();
        snapshot["auxiliary"]=CaptureAuxiliary();
        using(EnterPartnerOperation())
        using(NativePerformance.Measure("host.sorts")) snapshot["sorts"]=CaptureSorts();
        snapshot["ending"]=CaptureEnding(); snapshot["securityScene"]=CaptureSecurityScene(); snapshot["shooting"]=CaptureShooting();
        using(EnterPartnerOperation()) snapshot["choice"]=CaptureDynamicChoice();
        using(EnterPartnerOperation()) snapshot["frontends"]=CaptureFrontends();
        snapshot["documentRead"]=DocumentReadProof();
        return snapshot;
    }
    internal bool AuditProjection(out string detail)
    {
        // A pending local placement is intentionally ahead of the last host
        // projection. Audit its confirmed base; input QA separately checks the
        // immediate preview and its eventual accepted/rejected outcome.
        RestorePlacementPreviews();
        try { return AuditConfirmedProjection(out detail); }
        finally { ReapplyPlacementPreviews(handProjectionThrough); }
    }
    private bool AuditConfirmedProjection(out string detail)
    {
        int roots=0,items=0;
        foreach(JObject root in appliedRoots)
        {
            var inventory=PhysicalRoot(NativeRootProjection.ReadKey(root,appliedIndex.ActorRoots));
            if(!JToken.DeepEquals(NativeMirrorProbe.CaptureInventoryState(inventory),root["inventoryState"]))
            { detail="native root inventory state mismatch: "+root["place"]; return false; }
            if(inventory.childItems.Count!=((JArray)root["items"]).Count)
            { detail="native root count mismatch: "+root["place"]; return false; }
            foreach(JObject row in (JArray)root["items"])
            {
                var data=appliedIndex.Nodes[(uint)row["data"]]; var fields=data["fields"];
                if(!model.Objects.TryGetValue((uint)data["entity"],out var obj) || obj is not GameItem item ||
                    item.parentInventory!=inventory || item.identifier!=(string)fields["<identifier>k__BackingField"] ||
                    item.name!=(string)fields["<name>k__BackingField"] || item.unitCount!=(int)fields["<unitCount>k__BackingField"] ||
                    item.modifiedShape.minX!=(int)data["modifiedShape"]["x"] || item.modifiedShape.minY!=(int)data["modifiedShape"]["y"])
                { detail="native root identity/value mismatch: "+data["entity"]; return false; }
                items++;
            }
            roots++;
        }
        foreach(var slot in model.Objects.Values.OfType<GameSlotInventory>().Distinct())
            if(!NativeReplicaSlotPresentation.Matches(slot))
            { detail="native embedded slot presentation mismatch"; return false; }
        detail=$"atomic native projection roots={roots} items={items} revision={applied}"; return true;
    }
    private void ApplyWorldFields(JObject data)
    {
        var contract=HasActorProjection ? SharedWorldFields : WorldFields;
        if(data.Count!=contract.Length) throw new InvalidDataException("Native world fields mismatch");
        foreach(var field in contract)
        {
            if(!data.TryGetValue(field.Name,out var value)) throw new InvalidDataException("Missing world field");
            field.SetValue(PlayerStore.Instance,value.Type==JTokenType.Null ? null : value.ToObject(field.FieldType));
        }
    }
    internal static GameInventory Root(int place)
    {
        var world=NativeGuest.Active ? Guest : Host;
        if(world?.ActorCarry is NativeActorCarry carry && place is 4 or 5 or 8)
            return carry.Get(world.OperationActor,(NativeActorInventory.CarrySlot)place);
        return NativeFixtureRoot(place);
    }
    private static GameInventory NativeFixtureRoot(int place)
    {
        var e=EmporiumEntry.Instance;
        return place switch { 1=>e.invElement,2=>e.backInvinvElement,3=>e.afterhourInventory,
            4=>e.afterhourPocketSlotInvLeft,5=>e.afterhourPocketSlotInvRight,6=>e.frontInvinvElement,
            7=>e.showcaseElement,8=>e.afterhourPocketSlotInvBackpack,9=>e.backInvinvElementCounter,
            10=>e.docInvElement,11=>e.trashInvElement,12=>e.drainInvElement,13=>e.faucetElement,
            14=>e.cassettePlayerElement,15=>e.vendingMachineElement,16=>e.vendingFountainElement,
            17=>e.hiddenElement,18=>e.responseInventory,19=>e.responseInventoryClosable,20=>e.hirelingInv,
            21=>e.bazarLeftinvElement,22=>e.soldElement,23=>e.swapBufferElement,_=>null };
    }
    internal static void OrderReplicaInventory(GameInventory inventory,IEnumerable<GameItem> ordered)
    {
        // Keep object/window identities while matching the native traversal order.
        // Attach-only reconciliation otherwise retains stale order after group moves.
        var items=inventory is GameGridInventory
            ? (List<GameItem>)AccessTools.Field(typeof(GameGridInventory),"items").GetValue(inventory)
            : inventory is GameGridScrollableInventory
                ? (List<GameItem>)AccessTools.Field(typeof(GameGridScrollableInventory),"items").GetValue(inventory)
                : null;
        if(items==null) return;
        var desired=ordered.ToArray();
        if(items.SequenceEqual(desired)) return;
        items.Clear(); items.AddRange(desired);
    }
    private bool? presentedTap;
    private string presentedFixtureScene;
    private GameItem presentedFaucet;
    private void BindFixtureReferences()
    {
        var e=EmporiumEntry.Instance;
        e.dossier=e.docInvElement.childItem; e.trashcan=e.trashInvElement.childItem;
        e.drain=e.drainInvElement.childItem; e.faucet=e.faucetElement.childItem; e.cassettePlayer=e.cassettePlayerElement.childItem;
        e.trashcanInvElement=e.trashcan?.contentWindow?.childElement as GameGridInventory;
        bool inStore=!MapUIManager.Instance.IsOutside;
        // Presentation derived from authoritative upgrades must be refreshed
        // after business and item hydration, never from guest bootstrap defaults.
        bool tap=EmporiumEntry.IsWaterTapUnlocked();
        string scene=MapUIManager.Instance.Current?.Id ?? "";
        if(e.faucet!=null && (presentedTap!=tap || presentedFixtureScene!=scene || !ReferenceEquals(presentedFaucet,e.faucet)))
        {
            e.LoadFaucetUpdate(PlayerStore.Instance);
            e.faucetWindow.rectTransform.gameObject.SetActive(inStore && tap);
            presentedTap=tap; presentedFixtureScene=scene; presentedFaucet=e.faucet;
        }
        StoreVendingMachine.Instance.gameObject.SetActive(inStore && e.vendingMachineElement.childItem!=null);
        StoreVendingFountain.Instance.gameObject.SetActive(inStore && PlayerStore.Instance.isVendingFountainUnlocked && e.vendingFountainElement.childItem!=null);
    }
    private static JObject RootShape(GridShape shape,NativeDeferredFields deferred=null)
    {
        var cells=NativeMirrorProbe.ShapeCells(shape,deferred);
        return new JObject { ["w"]=shape.width,["h"]=shape.height,["outside"]=shape.GetOutsideBounds(),["cells"]=cells };
    }
    private static bool MatchesRootGrid(GridShape live,NativeSnapshotIndex.RootGrid shape)
    {
        if(live==null || live.width!=shape.Width || live.height!=shape.Height || live.minX!=0 || live.minY!=0 ||
            live.orientation!=0 || live.flipped || live.GetOutsideBounds()!=shape.Outside) return false;
        int index=0;
        for(int y=0;y<shape.Height;y++)
            for(int x=0;x<shape.Width;x++)
                if(live.GetLocal(x,y)!=shape.Cells[index++]) return false;
        return true;
    }
    private void ApplyRootGrid(GameGridInventory grid,NativeSnapshotIndex.RootGrid shape)
    {
        // Native UI may replace or edit a shape in place between snapshots.
        // Compare the live geometry, not only the last pair of wire snapshots.
        bool verifyRepair=NativeBackgroundQa.Enabled && verifiedRootGrids==64;
        if(verifyRepair) NativeBackgroundQa.PerturbRootGrid(grid,InventoryShape,shape);
        if(MatchesRootGrid(grid.inventoryShape,shape))
        {
            if(NativeBackgroundQa.Enabled && ++verifiedRootGrids==64)
                Debug.Log("[probably_coop QA] PASS preserve-root-grid 64 matching native inventory shapes retained");
            return;
        }
        var replacement=new GridShapeBuilder(shape.Width,shape.Height).SetData(shape.Cells,shape.Width).SetDataOutside(shape.Outside);
        InventoryShape.SetValue(grid,replacement);
        NativeReplicaGraph.Validate(grid);
        NativeReplicaGraph.NotifySizeChanged(grid);
        if(verifyRepair)
        {
            if(!MatchesRootGrid(grid.inventoryShape,shape)) throw new InvalidDataException("Guest grid drift was not repaired");
            verifiedRootGrids++;
            Debug.Log("[probably_coop QA] PASS repair-root-grid altered cell restored from decoded host geometry");
        }
    }
    private void PrepareCaptureTooltips(GameItem value)
    {
        if(value is not GameItemElement item || captureTooltips.ContainsKey(item)) return;
        var ticket=new TooltipTicket(item,captureRoot,captureTooltipKey);
        captureTooltips.Add(item,denseTooltips ? ReadDenseTooltip(ticket) : ReadTooltipForCapture(item));
        tooltipTickets.Add(ticket);
    }
    private void CaptureExtra(object value,JObject data)
    {
        int frontend=NativeFrontendCapabilities.Kind(value);
        using var extraTiming=NativePerformance.Measure("host.capture_extra");
        var row=value is GameItem ? model.Deferred?.CurrentItem(data) : null;
        if(row!=null) row.FrontendKind=frontend;
        else if(frontend!=0) data["frontendKind"]=frontend;
        if(value is GameItemElement item)
        {
            using var actorScope=EnterPartnerOperation();
            using var tooltipTiming=NativePerformance.Measure("host.item_queries");
            var tooltip=captureTooltips[item];
            using(NativePerformance.Measure("host.query_tooltip_projection"))
            {
                var normal=NativeMirrorProbe.TooltipData(tooltip.Normal,model.Deferred);
                var advanced=PackAdvanced(tooltip.Normal,tooltip.Advanced,model.Deferred);
                if(row!=null) { row.Tooltip=normal; row.Advanced=advanced; }
                else { data["tooltip"]=normal; data["advanced"]=advanced; }
                if(NativeBackgroundQa.Enabled && verifiedTooltipPairs<64)
                {
                    var decoded=ReadAdvanced(ReadTooltip(NativeMirrorProbe.TooltipData(tooltip.Normal)),PackAdvanced(tooltip.Normal,tooltip.Advanced));
                    if(!NativeMirrorProbe.SameTooltip(decoded,tooltip.Advanced)) throw new InvalidDataException("Advanced tooltip round trip failed");
                    if(++verifiedTooltipPairs==64) VerifyTooltipVariants();
                }
            }
            bool mayRemove; int maxRemove,abilities; string move,contents;
            using(NativePerformance.Measure("host.query_permissions")) { mayRemove=item.MayRemove(); maxRemove=shop.GuestMaxRemove(item); }
            using(NativePerformance.Measure("host.query_move_proof")) move=MoveToken(item);
            using(NativePerformance.Measure("host.query_contents_proof")) contents=ContentsProof(item);
            using(NativePerformance.Measure("host.query_abilities")) abilities=CaptureItemAbilities(item);
            if(row==null)
            {
                data["mayRemove"]=mayRemove; data["maxRemove"]=maxRemove; data["moveToken"]=move;
                data["contentsToken"]=contents; data["abilities"]=abilities;
            }
            else
            {
                row.MayRemove=mayRemove; row.MaxRemove=maxRemove; row.Move=move;
                row.Contents=contents; row.Abilities=abilities;
            }
        }
        bool isControl=value is MachineryButton || value is MachineryRadioList || value is MachineryRadioButton ||
            value is MachinerySlider || value is MachineryRangeSlider;
        var ownerItem=isControl ? model.CaptureOwner : captureRoot;
        uint owner=ownerItem==null ? 0 : model.Id(ownerItem);
        if(row!=null) row.Owner=owner;
        else if(owner!=0) data["owner"]=owner;
        if(isControl && ownerItem!=null)
        {
            using var actorScope=EnterPartnerOperation();
            data["controlToken"]=CaptureControlToken(value,ownerItem);
        }
        // Business requests validate their dedicated move/control/content proofs.
        // The old recursive whole-render-node hash had no reader; unchanged
        // replicas use structural equality, not this redundant hash.
        // The authoritative command adapter needs identities/ownership only;
        // it reads current business values directly from native objects. Keep
        // this metadata separate from the graph owned by the encoding worker.
        uint entity=(uint)data["entity"];
        if(!model.Data.TryGetValue(entity,out var metadata))
            model.Data[entity]=new JObject { ["entity"]=entity,["owner"]=owner };
        else if((uint)metadata["owner"]!=owner) metadata["owner"]=owner;
    }
    private void ApplyExtra(object value,JObject data)
    {
        if(value is GameItemElement item && (NativeMirrorProbe.PropertyChanged(data,"tooltip") ||
            NativeMirrorProbe.PropertyChanged(data,"advanced") || !NativeReplicaTooltip.Has(item)))
        {
            var normal=ReadTooltip((JObject)data["tooltip"]);
            NativeReplicaTooltip.Bind(item,normal,ReadAdvanced(normal,data["advanced"]));
        }
        if(value is MachineryButton button) button.SetCallback(()=>RequestControl(button));
        if(value is MachinerySlider slider) { slider.onChanged=v=>EditContinuous(slider,v,0); slider.onReleased=v=>ReleaseContinuous(slider); }
        if(value is MachineryRangeSlider range) { range.onChanged=(lo,hi)=>EditContinuous(range,lo,hi); range.onReleased=(lo,hi)=>ReleaseContinuous(range); }
        if(value is MachineryRadioList radio)
        {
            radio.group.onChanged=index=>RequestRadio(radio,index);
        }
        if(value is MachineryRadioButton toggle && toggle.group==null)
            toggle.onChanged=desired=>RequestToggle(toggle,desired);
    }
    private static JToken PackAdvanced(RichText normal,RichText advanced,NativeDeferredFields fields=null) =>
        NativeMirrorProbe.SameTooltip(normal,advanced) ? (JToken)false : NativeMirrorProbe.TooltipData(advanced,fields);
    private static RichText ReadAdvanced(RichText normal,JToken advanced) => advanced is JObject fields ? ReadTooltip(fields) :
        advanced?.Type==JTokenType.Boolean && !(bool)advanced ? normal : throw new InvalidDataException("Invalid advanced tooltip");
    private static void VerifyTooltipVariants()
    {
        var normal=new RichText("n","s","u","f","b",5,2,false);
        var original=NativeMirrorProbe.TooltipData(normal);
        foreach(var field in original.Properties())
        {
            var changed=(JObject)original.DeepClone();
            changed[field.Name]=field.Value.Type switch {
                JTokenType.String=>NativeSnapshotCodec.Scalar(null), JTokenType.Boolean=>new JValue(true), _=>new JValue((int)field.Value+1)
            };
            var advanced=ReadTooltip(changed);
            var encoded=PackAdvanced(normal,advanced);
            if(encoded is not JObject || !JToken.DeepEquals(changed,NativeMirrorProbe.TooltipData(ReadAdvanced(normal,encoded))))
                throw new InvalidDataException("Lost advanced tooltip field: "+field.Name);
        }
        Debug.Log("[probably_coop QA] PASS compact-tooltips 64 native pairs plus all eight independent advanced fields");
    }
    private static RichText ReadTooltip(JObject d) => new RichText((string)d["nakedText"],(string)d["richTextStrikethrough"],
        (string)d["richTextUnderline"],(string)d["richTextForeground"],(string)d["richTextBackground"],
        (int)d["recommendedWidth"],(int)d["recommendedHeight"],(bool)d["isTruncated"]);

    private static JObject CaptureClient(StoreClient source)
    {
        if(source==null) return null;
        var data=new JObject();
        foreach(var field in ClientFields) data[field.Name]=NativeSnapshotCodec.FieldValue(field.GetValue(source));
        var barter=source.barterOffer;
        data["barter"]=barter==null ? null : new JObject {
            ["text"]=barter.barterTextBody,["free"]=barter.isFreeReward,["index"]=barter.currentSetIndex,
            ["accepted"]=barter.acceptedResponse,["denied"]=barter.deniedResponse,
            ["query"]=JObject.FromObject(new NativeQueryProbe.BarterView { Acceptable=barter.IsBarterAcceptable(),OfferCount=barter.OfferSetCount(),Response=barter.GetItemPlacedResponse() }),
            ["conditions"]=new JArray(barter.conditions.Select(c=>new JObject { ["item"]=c.requiredItemId,["tag"]=c.requiredItemTag,
                ["required"]=c.requiredDisplay,["offered"]=c.offeredDisplay,["blacklist"]=c.blacklistedConditionId==null ? null : JArray.FromObject(c.blacklistedConditionId) })) };
        var gun=source.gunOrderData;
        data["gun"]=gun==null ? null : new JObject { ["tier"]=gun.tier,["reward"]=gun.baseReward,["text"]=gun.dialogText,
            ["best"]=gun.isBestOrder,["service"]=gun.isServiceOrder,
            ["mandatory"]=new JArray(gun.mandatoryRequirements.Select(CaptureRequirement)),["bonus"]=new JArray(gun.bonusRequirements.Select(CaptureRequirement)) };
        return data;
    }
    private StoreClient ReadClient(JObject data,bool includeItemQueries=false,StoreClient existing=null,HashSet<string> changes=null)
    {
        if(data.Count!=ClientFields.Length+(includeItemQueries ? 3 : 2)) throw new InvalidDataException("Client contract mismatch");
        var client=existing ?? new StoreClient();
        foreach(var field in ClientFields)
        {
            if(changes!=null && !changes.Contains(field.Name)) continue;
            if(!data.TryGetValue(field.Name,out var value)) throw new InvalidDataException("Missing native client field");
            field.SetValue(client,value.Type==JTokenType.Null ? null : value.ToObject(field.FieldType));
        }
        if(data["barter"] is JObject b && (changes==null || changes.Contains("barter")))
        {
            var barter=new BarterOffer { ownerClient=client,barterTextBody=(string)b["text"],isFreeReward=(bool)b["free"],
                currentSetIndex=(int)b["index"],acceptedResponse=(string)b["accepted"],deniedResponse=(string)b["denied"] };
            foreach(JObject c in (JArray)b["conditions"]) barter.conditions.Add(new BarterCondition { requiredItemId=(string)c["item"],
                requiredItemTag=(string)c["tag"],requiredDisplay=(string)c["required"],offeredDisplay=(string)c["offered"],
                blacklistedConditionId=c["blacklist"].Type==JTokenType.Null ? null : c["blacklist"].ToObject<List<string>>() });
            NativeQueryProbe.BindReplica(barter,b["query"].ToObject<NativeQueryProbe.BarterView>()); client.barterOffer=barter;
        }
        else if(data["barter"] is not JObject) client.barterOffer=null;
        if(data["gun"] is JObject g && (changes==null || changes.Contains("gun"))) client.gunOrderData=new GunOrderData { tier=(int)g["tier"],baseReward=(int)g["reward"],
            dialogText=(string)g["text"],isBestOrder=(bool)g["best"],isServiceOrder=(bool)g["service"],
            mandatoryRequirements=((JArray)g["mandatory"]).Cast<JObject>().Select(ReadRequirement).ToList(),
            bonusRequirements=((JArray)g["bonus"]).Cast<JObject>().Select(ReadRequirement).ToList() };
        else if(data["gun"] is not JObject) client.gunOrderData=null;
        if(includeItemQueries && (changes==null || changes.Contains("itemQueries"))) BindClientItemQueries(client,(JObject)data["itemQueries"]);
        return client;
    }
    private static JObject CaptureRequirement(GunOrderRequirement r)=>new JObject { ["id"]=r.id,["value"]=r.value,["reward"]=r.reward,["larger"]=r.isMustBeLargerThan,["caliber"]=r.isCaliber };
    private static GunOrderRequirement ReadRequirement(JObject r)=>new GunOrderRequirement { id=(string)r["id"],value=(int)r["value"],reward=(int)r["reward"],isMustBeLargerThan=(bool)r["larger"],isCaliber=(bool)r["caliber"] };
    private string TradeToken()
    {
        using var actorScope=EnterPartnerOperation();
        var store=PlayerStore.Instance; var blueprint=store.currentClientInstance?.GetClientBlueprint();
        var data=new JObject { ["client"]=blueprint==null ? 0 : model.Id(blueprint), ["blueprint"]=CaptureClient(blueprint),
            ["cash"]=store.playerCash,["bargain"]=CaptureBargain(), ["items"]=new JArray(EmporiumEntry.Instance.backInvinvElement.childItems.Select(i=>new JObject {
                ["id"]=model.Id(i),["owned"]=GeneralHelper.IsItemOwned(i),["count"]=i.unitCount,["price"]=i.GetNegociatedValue(),
                // A callback's decision can change without any serialized client
                // field or price changing. Bind the quote to its eligible set too.
                ["buying"]=blueprint?.IsClientBuyingThisItem(i)==true,["refusing"]=blueprint?.IsClientRefusingItem(i)==true,
                ["canExpose"]=blueprint?.CanClientExposeAnyFeatureDeep(i)==true })) };
        // Native offer cycling replaces the front-counter rewards even when
        // the selected index returns to its old value. An earlier acceptance
        // must not buy a different set of native entities under the same token.
        if(blueprint?.clientIntent==StoreClient.ClientIntent.BARTER) data["barterGoods"]=BarterGoodsProof();
        if(blueprint?.isOfferWholesale==true)
        {
            data["wholesaleGoods"]=BarterGoodsProof();
            data["wholesaleTotal"]=store.GetWholesalePrice();
        }
        using var sha=SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(data.ToString(Formatting.None))));
    }
    internal bool RequestTrade(string action)
    {
        if(host || !Ready) return false;
        var selected=NegociationUIManager.Instance.currentNegociatedItem;
        return Submit(new JObject { ["kind"]="trade",["action"]=action,["token"]=tradeToken,
            ["selected"]=model.ReplicaId(selected) });
    }
    internal void OpenBudgetConfirmation()
    {
        budgetConfirmationToken=tradeToken;
        NegociationUIManager.Instance.OpenConfirmPanel();
    }
    internal void ConfirmBudgetTrade()
    {
        if(string.IsNullOrEmpty(budgetConfirmationToken)) return;
        if(Submit(new JObject { ["kind"]="trade",["action"]="sellBudget",["token"]=budgetConfirmationToken,["selected"]=0 }))
        {
            budgetConfirmationToken=null;
            NegociationUIManager.Instance.OnConfirmPanelNo();
        }
    }
    private ushort Execute(byte[] payload)
    {
        if(!NativeCommandEnvelope.TryRead(payload,out var command)) return (ushort)ShopResult.Unavailable;
        var previousHand=hostHand;
        var previousUiExecution=RemoteUiExecution;
        var previousOrigins=remoteChoiceOrigins;
        ulong previousFrontend=frontendExecutingNonce; var previousFrontendKind=frontendExecutingKind;
        try
        {
            // Native group placement temporarily borrows selectedItems. Freeze
            // the actual local hand before entering any such UI execution scope.
            string kind=(string)command["kind"];
            if(kind=="partnerSetup") return ExecutePartnerSetup(command);
            if(kind is not ("ending" or "handRelease") && !EndingActive && !ValidOperationContext(command))
                return (ushort)ShopResult.Stale;
            if(!AcceptFrontendPrediction(command)) return (ushort)ShopResult.Stale;
            bool hand=command["hand"]!=null;
            hostHand=CaptureHostHand(!hand);
            using var actorScope=EnterPartnerOperation();
            RemoteUiExecution=this; remoteChoiceOrigins=ChoiceOrigins(command);
            ushort result=hand ? ExecuteHand(command) : ExecuteCore(command);
            if(result==(ushort)ShopResult.Applied && (string)command["kind"] is not ("handBegin" or "handRenew" or "handRelease")) prefetched.Clear();
            if(NativeBackgroundQa.Enabled && result!=(ushort)ShopResult.Applied) Debug.Log("[probably_coop QA] request rejected result="+result+" payload="+Encoding.UTF8.GetString(payload));
            return result;
        }
        finally { hostHand=previousHand; RemoteUiExecution=previousUiExecution; remoteChoiceOrigins=previousOrigins;
            frontendExecutingNonce=previousFrontend; frontendExecutingKind=previousFrontendKind; }
    }
    private ushort ExecuteCore(JObject command)
    {
        if((string)command["kind"]=="qaActorWound") return ExecuteQaActorWound();
        if((string)command["kind"]=="ending") return ExecuteEnding(command);
        if(NativeActorSession.Current is NativeActorSession actors && (actors.Terminal || actors.Faulted))
            return (ushort)ShopResult.Busy;
        if(EndingActive) return (ushort)ShopResult.Busy;
        if((bool)AccessTools.Field(typeof(MapUIManager),"inTransit").GetValue(MapUIManager.Instance)) return (ushort)ShopResult.Busy;
        if(NativeBackgroundQa.Enabled) Debug.Log("[probably_coop QA] native request kind="+(string)command["kind"]+" action="+(string)command["action"]+" id="+(string)command["id"]);
        if((string)command["kind"]=="shooting") return ExecuteShooting(command);
        if(ShootingOwnsCustomer && (string)command["kind"] is "trade" or "barterOffer" or "bargain" or "bell" or "security") return (ushort)ShopResult.Busy;
        if((string)command["kind"]=="choice") return ExecuteChoice(command);
        if((string)command["kind"]=="frontend") return ExecuteFrontend(command);
        if((string)command["kind"]=="service") return ExecuteService(command);
        if((string)command["kind"]=="containerDrop") return ExecuteContainerDrop(command);
        if((string)command["kind"]=="dialogue") return ExecuteDialogue(command);
        if((string)command["kind"]=="bell") return ExecuteBell(command);
        if((string)command["kind"]=="upgrade") return ExecuteWild(command);
        if((string)command["kind"]=="pc") return ExecutePc(command);
        if((string)command["kind"]=="phone") return ExecutePhone(command);
        if((string)command["kind"]=="security") return ExecuteReport(command);
        if((string)command["kind"]=="newspaper") return ExecuteNews();
        if((string)command["kind"]=="group") return ExecuteGroup(command);
        if((string)command["kind"]=="sort") return ExecuteSort(command);
        if((string)command["kind"]=="bulk") return ExecuteBulk(command);
        if((string)command["kind"]=="auxiliary") return ExecuteAuxiliary(command);
        if((string)command["kind"]=="item") return ExecuteItem(command);
        if((string)command["kind"]=="documentRead") return ExecuteDocumentRead(command);
        if((string)command["kind"]=="editor") return ExecuteEditor(command);
        if((string)command["kind"]=="storageTab") return ExecuteStorageTabRename(command);
        if((string)command["kind"]=="shutter") return ExecuteShutter(command);
        if((string)command["kind"]=="commissary") return ExecuteCommissary(command);
        if((string)command["kind"]=="bargain") return ExecuteBargain(command);
        if((string)command["kind"]=="barterOffer") return ExecuteBarterOffer(command);
        if((string)command["kind"]=="travel") return TravelRequest((string)command["destination"],2,(string)command["proof"],
            (uint)command["backpack"],(string)command["backpackProof"]);
        if((string)command["kind"]=="scavenge") return ScavengeRequest();
        if((string)command["kind"]=="move") return ExecuteMove(command);
        if((string)command["kind"]=="control") return ExecuteControl(command);
        if((string)command["kind"]!="trade") return (ushort)ShopResult.Unavailable;
        if((string)command["token"]!=TradeToken()) return (ushort)ShopResult.Stale;
        if(shop.Settling || NegociationUIManager.IsTradeLocked()) return (ushort)ShopResult.Busy;
        var store=PlayerStore.Instance; var blueprint=store.currentClientInstance?.GetClientBlueprint();
        if(blueprint==null || blueprint.isBusinessComplete || !store.isClientArrived) return (ushort)ShopResult.Unavailable;
        uint id=(uint)command["selected"];
        GameItem selected=null;
        if(id!=0 && (!model.Objects.TryGetValue(id,out var obj) || (selected=obj as GameItem)==null ||
            selected.parentInventory!=EmporiumEntry.Instance.backInvinvElement)) return (ushort)ShopResult.Stale;
        var ui=NegociationUIManager.Instance;
        var previous=store.currentNegociatedItem; var previousUi=ui.currentNegociatedItem;
        var field=AccessTools.Field(typeof(NegociationUIManager),"selectedItem"); var previousSelected=field.GetValue(ui);
        try
        {
            // Vanilla inventory callbacks require a valid counter selection even for
            // bulk actions. A guest's unopened tablet legitimately has no selection.
            store.currentNegociatedItem=selected ?? EmporiumEntry.Instance.backInvinvElement.childItems.LastOrDefault();
            ui.currentNegociatedItem=selected; field.SetValue(ui,selected);
            switch((string)command["action"])
            {
                case "buyAll":
                    if(blueprint.clientIntent is not (StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.SELLNBUY)) return (ushort)ShopResult.Unavailable;
                    var purchaseItems=(List<GameItem>)AccessTools.Method(typeof(NegociationUIManager),"GetNonOwnedBackInvItems").Invoke(null,null);
                    if(blueprint.isMultiBuyDisabled && purchaseItems.Count>1)
                        purchaseItems=new List<GameItem>{selected!=null && purchaseItems.Contains(selected) ? selected : purchaseItems[0]};
                    if(blueprint.isRegularSellDisable || purchaseItems.Count==0 ||
                        purchaseItems.Sum(i=>(long)(int)i.GetNegociatedValue())>store.playerCash)
                        return (ushort)ShopResult.Unavailable;
                    if(purchaseItems.Any(HandTouches)) return (ushort)ShopResult.Busy;
                    ui.OnBuyAllClick(); break;
                case "sellAll":
                    if(blueprint.clientIntent is not (StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELLNBUY) || !ui.IsClientHaveEnoughToBuy()) return (ushort)ShopResult.Unavailable;
                    var sellable=ui.GetSellableItem();
                    if(sellable.Count==0) return (ushort)ShopResult.Unavailable;
                    if(sellable.Any(HandTouches)) return (ushort)ShopResult.Busy;
                    ui.OnSellAllClick(); break;
                case "sellBudget":
                    if(blueprint.clientIntent is not (StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELLNBUY) ||
                        !blueprint.useClientBudget || ui.IsClientHaveEnoughToBuy()) return (ushort)ShopResult.Stale;
                    var saleItems=(List<GameItem>)AccessTools.Method(typeof(NegociationUIManager),"GetSellableBackInvItems").Invoke(null,new object[]{blueprint});
                    if(saleItems.Count==0) return (ushort)ShopResult.Unavailable;
                    if(saleItems.Any(HandTouches)) return (ushort)ShopResult.Busy;
                    ui.SellMultiple(saleItems,ignoreBudget:true); break;
                case "wholesale":
                    if(!blueprint.isOfferWholesale || EmporiumEntry.Instance.GetAllNonOwendItemCount()==0 ||
                        !store.CanPay(store.GetWholesalePrice())) return (ushort)ShopResult.Unavailable;
                    if(HandInside(EmporiumEntry.Instance.frontInvinvElement) || HandInside(EmporiumEntry.Instance.backInvinvElement)) return (ushort)ShopResult.Busy;
                    ui.OnWholesaleClick(); break;
                case "accept":
                    if(HandTouches(selected)) return (ushort)ShopResult.Busy;
                    if(blueprint.clientIntent is StoreClient.ClientIntent.BUY or StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.SELLNBUY)
                    {
                        if(selected==null) return (ushort)ShopResult.Unavailable;
                        bool selling=GeneralHelper.IsItemOwned(selected);
                        if(selling ? blueprint.clientIntent==StoreClient.ClientIntent.SELL || !store.CanSellItemFromTable() :
                            blueprint.clientIntent==StoreClient.ClientIntent.BUY || !store.CanBuyItemFromTable())
                            return (ushort)ShopResult.Unavailable;
                    }
                    else
                    {
                        if(HandInside(EmporiumEntry.Instance.backInvinvElement) ||
                            blueprint.clientIntent==StoreClient.ClientIntent.BARTER && HandInside(EmporiumEntry.Instance.frontInvinvElement))
                            return (ushort)ShopResult.Busy;
                        bool acceptable=blueprint.clientIntent switch {
                            StoreClient.ClientIntent.BARTER=>blueprint.isIntroduced && blueprint.barterOffer?.IsBarterAcceptable()==true,
                            StoreClient.ClientIntent.INFORMATION_DEALER=>blueprint.isIntroduced && store.CanPay(blueprint.serviceCost),
                            StoreClient.ClientIntent.GUNSMITH=>blueprint.gunOrderData?.IsAccepted()==true,
                            _=>false
                        };
                        if(!acceptable) return (ushort)ShopResult.Unavailable;
                    }
                    ui.OnOfferAcceptClick(); break;
                default: return (ushort)ShopResult.Unavailable;
            }
        }
        finally
        {
            var counter=EmporiumEntry.Instance.backInvinvElement;
            store.currentNegociatedItem=previous?.parentInventory==counter ? previous : counter.childItems.LastOrDefault();
            ui.currentNegociatedItem=previousUi?.parentInventory==counter ? previousUi : null;
            field.SetValue(ui,previousSelected is GameItem old && old.parentInventory==counter ? old : null); ui.RefreshUI();
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private int verifiedRecordPublish;
    private static EncodedSnapshot EncodeSnapshot(JObject snapshot,NativeDeferredFields fields,CancellationToken cancellation,NativeRecordBasis basis,ulong baseRevision,byte[] auditBasis)
    {
        var result=new EncodedSnapshot(); var timer=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result.Bytes=fields.Pack(snapshot,cancellation,true,basis,baseRevision,out result.Checkpoint,out result.Referenced,auditBasis);
            result.Elided=fields.ElidedRows;
            result.Markers=fields.Markers;
            result.ReusedMarkers=fields.ReusedMarkers;
        }
        catch(Exception error) { result.Error=error; }
        result.Milliseconds=timer.Elapsed.TotalMilliseconds;
        return result;
    }
    private static DecodedSnapshot DecodeSnapshot(byte[] compressed,NativeSnapshotIndex previous,CancellationToken cancellation,
        byte[] full,ulong fullRevision,NativeSnapshotIndex knownBasis)
    {
        var result=new DecodedSnapshot(); var timer=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            bool delta=NativeRecordFrame.IsDelta(compressed);
            result.Delta=delta;
            result.BasisRevision=fullRevision;
            if(delta && knownBasis==null)
            {
                if(full==null || !NativeRecordFrame.HasBase(compressed,fullRevision)) throw new InvalidDataException("Missing native full checkpoint");
                knownBasis=NativeSnapshotIndex.Build(NativeSnapshotCodec.Unpack(full,cancellation),null,cancellation,separateRecords:true);
            }
            result.Data=NativeSnapshotCodec.Unpack(delta ? NativeRecordFrame.Body(compressed,fullRevision) : compressed,cancellation);
            result.DecodeMilliseconds=timer.Elapsed.TotalMilliseconds;
            result.Index=NativeSnapshotIndex.Build(result.Data,previous,cancellation,separateRecords:true,basis:delta ? knownBasis : null);
            result.Basis=delta ? knownBasis : result.Index;
            result.BasisBytes=full;
            result.IndexMilliseconds=timer.Elapsed.TotalMilliseconds-result.DecodeMilliseconds;
        }
        catch(Exception error) { result.Error=error; }
        result.Milliseconds=timer.Elapsed.TotalMilliseconds;
        return result;
    }
}

internal static class NativeReplicaTooltip
{
    private sealed class Pair { internal RichText Normal,Advanced; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameItemElement,Pair> entries=new System.Runtime.CompilerServices.ConditionalWeakTable<GameItemElement,Pair>();
    internal static bool Has(GameItemElement item) => entries.TryGetValue(item,out _);
    internal static void Bind(GameItemElement item,RichText normal,RichText advanced)
    { entries.Remove(item); entries.Add(item,new Pair { Normal=normal,Advanced=advanced }); }
    [HarmonyPatch(typeof(GameItemElement),nameof(GameItemElement.GetTooltip))]
    private static class Normal
    { private static bool Prefix(GameItemElement __instance,ref RichText __result) => Read(__instance,false,ref __result); }
    [HarmonyPatch(typeof(GameItemElement),nameof(GameItemElement.GetTooltipAdvanced))]
    private static class Advanced
    { private static bool Prefix(GameItemElement __instance,ref RichText __result) => Read(__instance,true,ref __result); }
    private static bool Read(GameItemElement item,bool advanced,ref RichText result)
    {
        if((!NativeGuest.Active && !NativeLocalItemChooser.Owns(item)) || !entries.TryGetValue(item,out var pair)) return true;
        NativeWorld.Guest?.ObserveTooltip(item);
        result=NativeWorld.Guest?.CurrentFastTooltip(item,advanced) ?? (advanced ? pair.Advanced : pair.Normal);
        return false;
    }
}

[HarmonyPatch]
internal static class NativeTradeRequests
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(string name in new[] { "OnBuyAllClick","OnSellAllClick","OnWholesaleClick","OnOfferAcceptClick" })
            yield return AccessTools.Method(typeof(NegociationUIManager),name);
    }
    private static bool Prefix(MethodBase __originalMethod)
    {
        if(!NativeGuest.Active) return true;
        if(__originalMethod.Name=="OnSellAllClick" && !NegociationUIManager.Instance.IsClientHaveEnoughToBuy())
        { NativeWorld.Guest?.OpenBudgetConfirmation(); return false; }
        string action=__originalMethod.Name switch { "OnBuyAllClick"=>"buyAll","OnSellAllClick"=>"sellAll","OnWholesaleClick"=>"wholesale",_=>"accept" };
        NativeWorld.Guest?.RequestTrade(action); return false;
    }
}

[HarmonyPatch(typeof(NegociationUIManager),nameof(NegociationUIManager.OnConfirmPanelYes))]
internal static class NativeBudgetConfirmation
{
    private static bool Prefix()
    { if(!NativeGuest.Active) return true; NativeWorld.Guest?.ConfirmBudgetTrade(); return false; }
}
