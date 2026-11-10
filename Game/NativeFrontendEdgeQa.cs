using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int frontendEdgeStep;
    private static string FrontendExchangeRows()=>string.Join("|",ExchangeUIManager.Instance.promotionalSection
        .GetComponentsInChildren<ExchangeElement>(true).OrderBy(row=>row.identifier,StringComparer.Ordinal)
        .Select(row=>row.identifier+":"+row.gameObject.activeSelf));
    private static void OriginalConfigure(GameItem item)=>AccessTools.Method(typeof(MachineryButton),"OnSelect")
        .Invoke(FrontendConfigure(item),null);
    // Deliberately invalid proof, through the production prediction/submission
    // path. No gameplay callback or authority result is simulated here.
    private static void StaleFrontendConfigure(GameItem item)
    {
        var world=NativeWorld.Guest;
        Require(world.TryData(FrontendConfigure(item),out var data),"missing original configure metadata");
        var command=new JObject { ["kind"]="control",["id"]=data["entity"],["owner"]=data["owner"],["token"]="obsolete-qa-proof" };
        Require((bool)AccessTools.Method(typeof(NativeWorld),"SubmitDirect").Invoke(world,new object[]{command}),
            "cannot issue rejected frontend prediction");
    }
    private bool RunFrontendEdgeCases(ReplicaLink link,double now)
    {
        var world=NativeWorld.Guest; var chem=ChemFinisherUIManager.Instance; var exchange=ExchangeUIManager.Instance;
        switch(frontendEdgeStep)
        {
            case 0:
                frontendExchange=FrontendItem("QA_FRONTEND_EXCHANGE");
                frontendExchange.ActivateSlotItem();
                Require(exchange.panel.activeSelf && QaWaiting(link) &&
                    FrontendExchangeRows()==MachineReplica().GetTagReadonly("QA_NATIVE_EXCHANGE_ROWS").GetString(),
                    "original exchange panel did not immediately compute the host promotion rows");
                exchange.ClosePanel(); frontendEdgeStep=1; return false;
            case 1:
                Require(!exchange.panel.activeSelf,"authority confirmation reopened exchange closed before its reply");
                frontendExchange.ActivateSlotItem(); frontendEdgeStep=2; return false;
            case 2:
                Require(exchange.panel.activeSelf && FrontendExchangeRows()==MachineReplica().GetTagReadonly("QA_NATIVE_EXCHANGE_ROWS").GetString(),
                    "original exchange promotional visibility differs from authority");
                NativeOffscreenCapture.Capture("qa-native-exchange"); exchange.ClosePanel();
                Record("PASS native-frontend-exchange","original item activation and panel compute promotion rows from shared data locally; close before receipt stays closed; host panel independent");
                OriginalConfigure(frontendChem); frontendEdgeStep=3; return false;
            case 3:
                if(!FrontendListReady(now)) return false;
                frontendHostChem=FrontendItem("QA_FRONTEND_HOST_CHEM");
                StaleFrontendConfigure(frontendHostChem);
                Require(chem.UIPanel.activeSelf && ReferenceEquals(chem.currentItem,frontendHostChem) && QaWaiting(link),
                    "rejected request did not exercise an immediate original frontend prediction");
                frontendEdgeStep=4; return false;
            case 4:
                Require(QaResult(link)==(ushort)ShopResult.Stale && chem.UIPanel.activeSelf && ReferenceEquals(chem.currentItem,frontendChem),
                    "rejected native opening failed to restore the prior authoritative target");
                StaleFrontendConfigure(frontendHostChem);
                OriginalConfigure(frontendChem);
                Require(chem.UIPanel.activeSelf && ReferenceEquals(chem.currentItem,frontendChem) && QaWaiting(link),
                    "a prior pending frontend prevented an independent replacement opening");
                frontendEdgeStep=5; return false;
            case 5:
                Require(QaResult(link)==(ushort)ShopResult.Applied && chem.UIPanel.activeSelf && ReferenceEquals(chem.currentItem,frontendChem),
                    "late rejected opening overwrote the newer original frontend");
                OriginalConfigure(frontendChem); chem.HideUI(); frontendEdgeStep=6; return false;
            case 6:
                Require(!chem.UIPanel.activeSelf,"authority confirmation reopened a native panel closed before receipt");
                Record("PASS native-frontend-prediction-rejection","real stale proof restores prior lease; old rejection cannot overwrite newer opening; original close before ACK retained");
                frontendEdgeStep=99; return true;
            default: return true;
        }
    }
}
