using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int auxStep;
    private double auxUntil,auxNext;
    private bool posterBefore;
    private bool RunAuxiliaryQa(ReplicaLink link,double now)
    {
        if(auxStep==9) return true;
        if(now<auxNext) return false;
        var calendar=AdvCalendarUIManager.Instance;
        if(auxStep==0)
        {
            StoreUIManager.Instance.storeCalendar.GetComponent<Interactable>().OnClick();
            Require(calendar.UIPanel.activeSelf && QaWaiting(link),"original calendar interaction did not open and request authoritative scheduling");
            auxStep=1; auxUntil=now+15; return false;
        }
        Require(now<auxUntil,"native auxiliary UI timeout step="+auxStep);
        if(auxStep==1 || auxStep==4)
        {
            if(QaWaiting(link)) return false;
            Require(QaResult(link)==(ushort)ShopResult.Applied,"auxiliary native query rejected: "+QaResult(link));
            auxStep++; auxNext=now+1; return false;
        }
        if(auxStep==2)
        {
            Require(StoreStation.Instance.storeEventManager.futurEvents.Any(e=>e.identifier=="rent_day"),"native calendar future rent projection missing");
            calendar.OnStatusButtonClicked(); auxStep=3; auxNext=now+0.5; return false;
        }
        if(auxStep==3)
        {
            Require(calendar.currentCreditTMP.text.StartsWith(PlayerStore.Instance.playerCash.ToString(),StringComparison.Ordinal),"native status credit text differs");
            // Native panel refresh destroys old rows at end-of-frame. A snapshot may
            // refresh this panel in the same Update as the runner; wait for that render boundary.
            if(calendar.activePerksContainer.GetComponentsInChildren<StartingPerkElement>().Length!=PlayerStore.Instance.startingPerks.Count)
            { auxNext=now+0.1; return false; }
            Record("PASS native-calendar-status","original calendar click, host-only calendar scheduling, native status fields and complete starting perks");
            calendar.CloseUI(); ClienteleUIManager.Instance.OpenUI();
            Require(QaWaiting(link),"native clientele panel did not request host forecast");
            auxStep=4; auxUntil=now+15; return false;
        }
        if(auxStep==5)
        {
            Require(NativeWorld.Guest.ClientDisplayName("ulDiscerningShopper")!="ulDiscerningShopper","native clientele names missing");
            Require(!string.IsNullOrEmpty(ClienteleUIManager.Instance.normalCustomerTitleTMP.text),"native clientele count missing");
            var report=new JObject { ["cash"]=calendar.currentCreditTMP.text,["normalCustomers"]=ClienteleUIManager.Instance.normalCustomerTitleTMP.text,
                ["forecast"]=ClienteleUIManager.Instance.normalCustomerTMP.text,["calendarEvents"]=JArray.FromObject(StoreStation.Instance.storeEventManager.futurEvents.Select(e=>e.identifier).ToArray()) };
            ModHelper.WriteModDataFileText("probably_coop","qa-auxiliary.json",report.ToString());
            Record("PASS native-clientele","original panel and native text builders using authoritative forecast and names");
            NativeOffscreenCapture.Capture("qa-auxiliary"); ClienteleUIManager.Instance.CloseUI();
            posterBefore=StorePoster.Instance.isPulled; StorePoster.Instance.OnClick(); auxStep=6; auxNext=now+1; return false;
        }
        if(auxStep==6)
        {
            Require(StorePoster.Instance.isPulled!=posterBefore,"replication reset local hidden-storage view");
            StorePoster.Instance.OnClick();
            Require(MachineReplica().contentWindow.IsVisible(),"auxiliary panel interaction closed independent machine window");
            Record("PASS local-panel-independence","calendar/clientele/poster views coexist with native guest production window");
            var paper=NativeWorld.Guest.Objects.Values.OfType<GameItem>().First(i=>i.identifier=="newspaper");
            Require(paper.CanActivateSlotItem(),"native newspaper cannot be read"); paper.ActivateSlotItem();
            Require(NewsUIManager.Instance.uiGameObject.activeSelf,"original newspaper click did not open native newspaper");
            auxStep=7; auxNext=now+1; auxUntil=now+15; return false;
        }
        if(QaWaiting(link)) return false;
        var news=NewsUIManager.Instance;
        var articles=news.uiGameObject.GetComponentsInChildren<NewsUIElement>().Where(e=>e.savedStoreEvent!=null).ToArray();
        if(articles.Length==0) { auxNext=now+0.2; return false; }
        foreach(var article in articles)
        {
            var authority=StoreStation.Instance.storeEventManager.GetActiveEvents().FirstOrDefault(e=>e.identifier==article.savedStoreEvent?.identifier);
            Require(authority!=null && article.title.text==authority.newsName && article.textContent.text.Contains(authority.newsDescription),"native newspaper article differs from shared event");
        }
        NativeOffscreenCapture.Capture("qa-newspaper"); news.CloseUI();
        Record("PASS native-newspaper","original clickable and native article layouts render host event titles, descriptions and duration; independent local close");
        auxStep=9; return true;
    }
}
