using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class CheckpointPreparation
    {
        internal readonly List<TooltipTicket> Tickets = new List<TooltipTicket>();
        internal readonly Dictionary<GameItemElement,(TooltipSample Sample, ulong Version)> Samples =
            new Dictionary<GameItemElement,(TooltipSample, ulong)>();
        internal readonly CaptureChangeJournal<GameItem> Changes = new CaptureChangeJournal<GameItem>();
        internal readonly double Deadline;
        internal readonly ulong Context;
        internal int Next, Uses;

        internal CheckpointPreparation(double now, ulong context)
        {
            Deadline = now + 2.5;
            Context = context;
        }
    }

    private CheckpointPreparation checkpointPreparation;
    private bool checkpointPreparationDone;
#if PROBABLY_COOP_GRID_QA
    private static readonly bool VerifyPreparedCheckpointTooltips =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-checkpoint-oracle") >= 0;
#endif

    // Pre-read native item descriptions and canonical proofs on the Unity
    // thread over several updates. The full snapshot still reads current item
    // fields, permissions and ownership together before it goes to the encoder.
    private bool PrepareJoinCheckpoint(double now)
    {
        if (checkpointPreparationDone) return true;
        if (!HasActorProjection || !NativeCaptureMutationHooks.Ready || NativePlatform.IsAndroid ||
            FullCaptureRequested || NativeCaptureComparison.Current != null || HasExternalCaptureWriters || HasOpaqueTooltipHooks())
        {
            checkpointPreparation = null;
            return checkpointPreparationDone = true;
        }

        try
        {
            using var timing = NativePerformance.Measure("host.checkpoint_preparation", NativePerformance.FrameWork.Tooltip);
            using var site = EnterExpeditionSite(NativeActorRun.Slot.Partner);
            if (checkpointPreparation == null)
            {
                var plan = new CheckpointPreparation(now, operationContext.Generation);
                var ownership = CaptureOwnershipGraph((item, root, key) =>
                {
                    if (item is GameItemElement element && CanPrepareCheckpointTooltip(element) &&
                        plan.Tickets.Count < NativeSnapshotPages.MaxRecords)
                        plan.Tickets.Add(new TooltipTicket(element, root, key));
                });
                if (ownership.PlacedItemCount < DenseTooltipThreshold || plan.Tickets.Count == 0)
                    return checkpointPreparationDone = true;
                checkpointPreparation = plan;
            }

            var preparation = checkpointPreparation;
            if (preparation.Context != operationContext.Generation)
            {
                checkpointPreparation = null;
                return checkpointPreparationDone = true;
            }
            if (now >= preparation.Deadline) return checkpointPreparationDone = true;

            long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 1000;
            for (int count = 0; count < 24 && preparation.Next < preparation.Tickets.Count; count++)
            {
                var ticket = preparation.Tickets[preparation.Next++];
                var item = ticket.Item;
                if (CanPrepareCheckpointTooltip(item) && Attached(ticket) &&
                    (!denseReads.TryGetValue(item, out var saved) || !saved.Value.SamePlacement(item, ticket.RootItem)))
                {
                    var pair = EvaluateTooltip(item);
                    preparation.Changes.Observe(item, item);
                    preparation.Changes.Observe(item, item.parentInventory);
                    preparation.Changes.Observe(item, ticket.RootItem);
                    preparation.Changes.Observe(item, item.state);
                    preparation.Changes.Observe(item, item.modifiedState);
                    preparation.Changes.Observe(item, item.shape);
                    preparation.Changes.Observe(item, item.modifiedShape);
                    foreach (var tag in item.state.dict.Values) preparation.Changes.Observe(item, tag);
                    foreach (var tag in item.modifiedState.dict.Values) preparation.Changes.Observe(item, tag);
                    preparation.Samples[item] = (new TooltipSample(ticket, pair, now), preparation.Changes.Version(item));
                    using (EnterPartnerOperation()) ContentsProof(item);
                }
                if (Stopwatch.GetTimestamp() >= end) break;
            }
            return checkpointPreparationDone = preparation.Next == preparation.Tickets.Count;
        }
        catch (Exception error)
        {
            checkpointPreparation = null;
            UnityEngine.Debug.LogWarning($"[probably_coop] Native checkpoint preparation skipped ({error.GetType().Name}).");
            return checkpointPreparationDone = true;
        }
    }

    private static bool CanPrepareCheckpointTooltip(GameItemElement item) =>
        item.GetType() == typeof(GameItemElement) && item.unitCount > 0 && item.contentWindow == null &&
        item.examineWindow == null && item.itemFeatures.Count == 0 && item.tooltipRichTextFunc == null;

    private bool TryPreparedCheckpointTooltip(TooltipTicket ticket, out (RichText Normal, RichText Advanced) pair)
    {
        pair = default;
        var preparation = checkpointPreparation;
        var item = ticket.Item;
        if (preparation == null || !preparation.Samples.TryGetValue(item, out var sample) || Focused(item) ||
            !CanPrepareCheckpointTooltip(item) || !preparation.Changes.IsCurrent(item, sample.Version) ||
            !sample.Sample.CurrentContext(item, ticket.RootItem)) return false;

        pair = sample.Sample.Pair;
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && VerifyPreparedCheckpointTooltips && preparation.Uses < 32)
        {
            var original = EvaluateTooltip(item);
            if (!NativeMirrorProbe.SameTooltip(pair.Normal, original.Normal) ||
                !NativeMirrorProbe.SameTooltip(pair.Advanced, original.Advanced))
                throw new InvalidOperationException("Prepared tooltip differs from the live native read");
            if (preparation.Uses == 31)
                UnityEngine.Debug.Log("[probably_coop QA] PASS prepared-checkpoint-tooltip 32 same-context native description pairs");
        }
#endif
        preparation.Uses++;
        return true;
    }

    private void FinishCheckpointPreparation()
    {
        if (checkpointPreparation is not { } preparation) return;
        checkpointPreparation = null;
        if (NativePerformance.Profiling)
            UnityEngine.Debug.Log($"[probably_coop PERF] checkpoint preparation read={preparation.Samples.Count} used={preparation.Uses}");
    }
}
