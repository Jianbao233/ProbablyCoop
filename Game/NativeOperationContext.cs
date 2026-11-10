using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private readonly OperationContext operationContext = new OperationContext();
    private ulong observedOperationContext;

    private ulong ObserveOperationContext() => operationContext.Observe(
        StoreStation.Instance.dayCounter, (int)PlayerStore.Instance.storeState,
        MapUIManager.Instance.Current?.Id ?? "",
        (bool)HarmonyLib.AccessTools.Field(typeof(MapUIManager), "inTransit").GetValue(MapUIManager.Instance));

    private void SealOperation(JObject command)
    {
        if (command["context"] == null) command["context"] = observedOperationContext;
    }
    private static bool RequiresQuietUi(JObject command)=>(string)command["kind"] is "shutter" or "ending" ||
        (string)command["kind"]=="travel" && (string)command["destination"]!="cancel";

    private void BeginOperationProjection(ulong generation)
    {
        RestorePlacementPreviews();
        if (observedOperationContext != 0 && observedOperationContext != generation)
        {
            uiRequests.Retire();
            labelRequest=phoneDialRequest=0;
            phoneDialPending=phoneCloseRequested=false;
            phoneStopProof=null;
            ResetDynamicChoice();
            ClearFrontends();
            CloseTravelPreparation();
            CancelAllHandActions();
            placementPreviews.Clear();
            controlPreviews.Clear();
            controlDrafts.Clear();
            continuousPreviews.Clear();
        }
        observedOperationContext = generation;
    }

    private bool ValidOperationContext(JObject command) =>
        command["context"]?.Type == JTokenType.Integer &&
        ulong.TryParse(command["context"].ToString(), out ulong generation) &&
        generation != 0 && generation == ObserveOperationContext();
}
