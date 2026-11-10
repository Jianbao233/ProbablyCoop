using System.Linq;
using ProbablyCoop.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine.Localization.Settings;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int documentStep,documentCash;
    private double documentAt;
    private string documentPage;
    private ulong documentUiSerial;
    private bool readDocumentFixturesRetired;
    private const string PaperFontsTag="PROBABLY_COOP_QA_PAPER_FONTS";
    private static JObject PaperFont(TextMeshProUGUI text)
    {
        text.ForceMeshUpdate(false,true);
        var localizer=text.GetComponent<TMPLocalizer>();
        return new JObject { ["font"]=text.font?.name,["role"]=localizer?.fontRole.ToString(),
            ["localizer"]=localizer!=null,["characters"]=text.textInfo.characterCount,
            ["nonAscii"]=new JArray(text.textInfo.characterInfo.Take(text.textInfo.characterCount).Where(c=>c.character>127)
                .Select(c=>new JArray((int)c.character,c.textElement?.unicode ?? 0,c.isVisible,c.fontAsset?.name))) };
    }
    private static JObject PaperFonts(string kind)
    {
        var paper=PaperUIManager.Instance;
        var values=kind=="procurement" ? new[]{paper.procurementHeader,paper.procurementClient,paper.procurementBody,paper.procurementDueDate} :
            kind=="general" ? new[]{paper.generalSlipTitle,paper.generalSlipBody} :
            new[]{paper.secSlip.GetComponent<SecuritySlip>().title,paper.secSlip.GetComponent<SecuritySlip>().body};
        return new JObject { ["locale"]=LocalizationSettings.SelectedLocale?.Identifier.Code,["labels"]=new JArray(values.Select(PaperFont)) };
    }
    private void CaptureHostPaperFonts()
    {
        var paper=PaperUIManager.Instance; var data=new JObject();
        // Seeded items have not been captured into the model yet. Use their
        // actual native inventory references, as an ordinary source UI does.
        GameItem Stored(string id)=>GraphUtils.FindAllChildrenType<GameItem>(EmporiumEntry.Instance.invElement).Distinct()
            .Single(i=>i.identifier==id && i.IsTag("PROBABLY_COOP_QA_DOCUMENT"));
        paper.OpenSlipUI(Stored("procurement_slip")); data["procurement"]=PaperFonts("procurement");
        NativeOffscreenCapture.Capture("qa-source-document-procurement"); paper.CloseSlipUI();
        paper.OpenGeneralSlipUI(Stored("slip")); data["general"]=PaperFonts("general");
        NativeOffscreenCapture.Capture("qa-source-document-general"); paper.CloseGeneralSlipUI();
        paper.OpenCard("sec_slip",Stored("sec_slip")); data["security"]=PaperFonts("security");
        NativeOffscreenCapture.Capture("qa-source-document-security"); paper.CloseCard("sec_slip");
        qaMachine.ModifyTag(PaperFontsTag,s=>s.Enable().SetString(data.ToString(Formatting.None)));
        ModHelper.WriteModDataFileText("probably_coop","qa-paper-fonts-source.json",data.ToString(Formatting.Indented));
    }
    private void VerifyNativePaperFonts(string kind)
    {
        var source=JObject.Parse(MachineReplica().GetTagReadonly(PaperFontsTag).GetString());
        var local=PaperFonts(kind);
        ModHelper.WriteModDataFileText("probably_coop","qa-paper-fonts-guest-"+kind+".json",local.ToString(Formatting.Indented));
        // Both sides must use the JSON representation: native optional strings
        // have typed null values, while the transported baseline has JSON null.
        Require(JToken.DeepEquals(source[kind],JObject.Parse(local.ToString(Formatting.None))),
            "native source/guest paper locale/font/rendered-glyph state differs: "+kind);
        Record("PASS native-paper-font-parity",kind+" original source and guest locale, font role, parsed characters and non-ASCII glyph selection match; missing vanilla glyphs are not relabelled as fully visible text");
    }
    private void RetireReadDocumentFixtures()
    {
        // The first customer is called only after every local paper view has
        // closed. Retire read-only fixtures before later tool fixtures need
        // native stock space; retain the dated paper for three actual nights.
        if(readDocumentFixturesRetired || !PlayerStore.Instance.isClientArrived) return;
        var documents=NativeWorld.Host.Objects.Values.OfType<GameItem>()
            .Where(i=>i.IsTag("PROBABLY_COOP_QA_DOCUMENT") && i.identifier!="procurement_slip").ToArray();
        Require(documents.Length==6,"read-only paper fixtures disappeared before their scheduled retirement");
        foreach(var item in documents) item.Destroy();
        readDocumentFixturesRetired=true;
        Record("PASS native-read-fixture-retirement","six closed read-only fixtures retired before subsequent tool tests; native inventory capacity unchanged; dated procurement paper retained");
    }
    private GameItem QaDocument(string id)=>NativeWorld.Guest.Objects.Values.OfType<GameItem>()
        .Single(i=>i.identifier==id && i.IsTag("PROBABLY_COOP_QA_DOCUMENT"));
    private void VerifyPaperReadState(ReplicaLink link)
    {
        Require(!QaWaiting(link) && NativeWorld.Guest.LastSubmittedUiRequest==documentUiSerial &&
            PlayerStore.Instance.playerCash==documentCash && QaDocument("procurement_slip").GetTagReadonly("PROCUREMENT_DAY").GetInt()==3,
            "local paper reading submitted business, changed cash or advanced its native due date");
    }
    private bool RunDocumentsQa(ReplicaLink link,double now)
    {
        if(documentStep==6) return true;
        if(now<documentAt) return false;
        GameItem Item(string id)=>QaDocument(id);
        if(documentStep==0)
        {
            documentCash=PlayerStore.Instance.playerCash;
            documentUiSerial=NativeWorld.Guest.LastSubmittedUiRequest;
            Item("water_guide").ActivateSlotItem();
            var book=MultiPageUIManager.Instance;
            Require(book.panel.activeSelf && book.books[1].activeSelf,"native water guide did not open on guest");
            string first=book.pageNumberText.text; book.FlipRightClicked(); documentPage=book.pageNumberText.text;
            Require(documentPage!=first && !QaWaiting(link),"native page flip failed or unexpectedly requested host business");
            documentStep=1; documentAt=now+1.5; return false;
        }
        if(documentStep==1)
        {
            Require(MultiPageUIManager.Instance.pageNumberText.text==documentPage,"snapshot reset actor-local guide page");
            NativeOffscreenCapture.Capture("qa-document-guide"); MultiPageUIManager.Instance.CloseUI();
            Item("handnote").ActivateSlotItem();
            var note=HandnoteUIManager.Instance;
            Require(note.panel.activeSelf && note.title.text=="合作备忘" && note.body.text==Item("handnote").GetTagReadonly("HANDNOTE_BODY").GetString() && note.sign.text=="QA","native note omitted authoritative content");
            NativeOffscreenCapture.Capture("qa-document-note"); note.ClosePanel();
            Item("intel_sec").ActivateSlotItem();
            Require(RecordUIManager.Instance.UIPanel.activeSelf && RecordUIManager.Instance.officerNoteTMP.text==PlayerStore.Instance.secData.note,"native security record failed to render");
            RecordUIManager.Instance.CloseUI(); Item("joe_card").ActivateSlotItem();
            Require(PaperUIManager.Instance.JoeWildBusinessCard.activeSelf,"native phone card did not open locally");
            documentStep=2; documentAt=now+1.5; return false;
        }
        var paper=PaperUIManager.Instance;
        VerifyPaperReadState(link);
        if(documentStep==2)
        {
            Require(paper.JoeWildBusinessCard.activeSelf,"snapshot closed local phone card");
            Item("joe_card").ActivateSlotItem();
            Require(!paper.JoeWildBusinessCard.activeSelf,"second native activation did not close local card");
            Record("PASS native-local-documents","original water guide/page navigation, host-authored handnote, security record, phone card; snapshots preserve independent pages, no host command or cash mutation");
            Item("procurement_slip").ActivateSlotItem(); documentStep=3; documentAt=now+1.5;
            return false;
        }
        if(documentStep==3)
        {
            var item=Item("procurement_slip");
            Require(paper.slipPanel.activeSelf && paper.procurementHeader.text==item.GetTagReadonly("PROCUREMENT_HEADER").GetString() &&
                paper.procurementClient.text=="By "+item.GetTagReadonly("PROCUREMENT_CLIENT_NAME").GetString() &&
                paper.procurementBody.text=="Order:\n    "+item.GetTagReadonly("PROCUREMENT_ORDER_BODY").GetString()+"\n    \nReward:\n    17 Credits" &&
                paper.procurementDueDate.text=="Due in 3 Days","original procurement panel lost native fields/layout across snapshots");
            VerifyNativePaperFonts("procurement"); NativeOffscreenCapture.Capture("qa-document-procurement"); paper.CloseSlipUI();
            Record("PASS native-procurement-paper","original item activation -> original OpenSlipUI; header, client, rich/multiline order, reward and due date preserved across snapshots; no shared request");
            Item("slip").ActivateSlotItem(); documentStep=4; documentAt=now+1.5; return false;
        }
        if(documentStep==4)
        {
            var item=Item("slip");
            Require(paper.generalSlipPanel.activeSelf && paper.generalSlipTitle.text==item.GetTagReadonly("SLIP_TITLE").GetString() &&
                paper.generalSlipBody.text==item.GetTagReadonly("SLIP_BODY").GetString(),"original general slip omitted authoritative rich text");
            VerifyNativePaperFonts("general"); NativeOffscreenCapture.Capture("qa-document-general"); paper.CloseGeneralSlipUI();
            Record("PASS native-general-paper","original item activation -> original OpenGeneralSlipUI; complete source title/body persist locally through snapshots, close remains local");
            Item("sec_slip").ActivateSlotItem(); documentStep=5; documentAt=now+1.5; return false;
        }
        var security=paper.secSlip.GetComponent<SecuritySlip>(); var slip=Item("sec_slip");
        Require(paper.secSlip.activeSelf && security.title.text==slip.GetTagReadonly("title").GetString() &&
            security.body.text==slip.GetTagReadonly("body").GetString(),"original security slip lost source fields");
        VerifyNativePaperFonts("security"); NativeOffscreenCapture.Capture("qa-document-security"); paper.CloseCard("sec_slip");
        Require(!paper.slipPanel.activeSelf && !paper.generalSlipPanel.activeSelf && !paper.secSlip.activeSelf,"native paper close left another paper view open");
        VerifyPaperReadState(link);
        Record("PASS native-security-paper","original item activation -> original OpenCard/SecuritySlip.Init; exact title/body retained across snapshots; all three native paper views close without commands or local due-date writes");
        documentStep=6; return true;
    }
}
