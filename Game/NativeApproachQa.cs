using System.Collections.Generic;
using DG.Tweening;
using Newtonsoft.Json;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly List<object> approachFrames = new List<object>();
    private readonly List<Vector3> approachPositions = new List<Vector3>();
    private int approachCaptures;
    private double approachStarted;
    private float approachDuration;
    private bool approachComplete;

    private bool ObserveNativeApproach(double now)
    {
        if (approachComplete) return true;
        var actor = StoreClientMono.Instance;
        if (actor == null || !actor.gameObject.activeInHierarchy) return false;
        var position = actor.transform.position;
        var path = actor.GetComponent<DOTweenPath>();
        if (actor.IsMoving())
        {
            if (approachFrames.Count == 0)
            {
                approachStarted = now;
                approachDuration = path.tween.Duration(false);
            }
            if (approachFrames.Count < 240)
                approachFrames.Add(new { frame=Time.frameCount, ms=(now-approachStarted)*1000,
                    x=position.x, y=position.y, red=actor.image.color.r, progress=path.tween.ElapsedPercentage() });
            if (approachPositions.Count == 0 || Vector3.SqrMagnitude(position-approachPositions[approachPositions.Count-1]) > 0.000001f)
                approachPositions.Add(position);
            if (approachCaptures == 0 || approachCaptures == 1 && path.tween.ElapsedPercentage() >= 0.45f)
            {
                NativeOffscreenCapture.Capture("qa-client-enter-"+approachCaptures);
                approachCaptures++;
            }
            return false;
        }
        if (!PlayerStore.Instance.isClientArrived) return false;
        Require(approachFrames.Count >= 3 && approachPositions.Count >= 3 && approachCaptures == 2,
            "guest skipped native approach frames or silhouette progression");
        double elapsed = now - approachStarted;
        Require(elapsed >= approachDuration*0.7 && elapsed <= approachDuration+1,
            "guest approach duration differed from the original path duration");
        NativeOffscreenCapture.Capture("qa-client-enter-2");
        ModHelper.WriteModDataFileText("probably_coop", "qa-input-approach.json", JsonConvert.SerializeObject(new {
            originalDuration=approachDuration, observedDuration=elapsed, distinctPositions=approachPositions.Count, frames=approachFrames
        }, Formatting.Indented));
        Record("PASS native-approach-frames", $"original path duration={approachDuration:F3}s observed={elapsed:F3}s distinctPositions={approachPositions.Count}; silhouette, middle and counter frames rendered");
        approachComplete = true;
        return true;
    }
}
