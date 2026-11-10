using System;
using System.Collections.Generic;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Explicitly reviewed presentation-only callbacks from MiscItemDirectory.
    // Do not run arbitrary item factories/delegates on the replica: e.g. scratch
    // tickets, stamps and exchange orders perform business as well as opening UI.
    private static readonly Dictionary<string,Action<GameItem>> documentViews=new Dictionary<string,Action<GameItem>> {
        ["newspaper"]=_=>NewsUIManager.Instance.ToggleUI(),
        ["intel_sec"]=_=>RecordUIManager.Instance.OpenUI(),
        ["tutorial_page"]=i=>PaperUIManager.Instance.OpenUI(i.GetTagReadonly("TUTORIAL_PAGE_TITLE").GetString(),i.GetTagReadonly("TUTORIAL_PAGE_BODY").GetString(),i.GetTagReadonly("TUTORIAL_PAGE_SIGNATURE").GetString()),
        ["lottery_info"]=_=>PaperUIManager.Instance.OpenLotteryInfo(),
        ["cigarette_guide"]=_=>PaperUIManager.Instance.OpenCigaretteGuide(),
        ["cigarette_color"]=_=>PaperUIManager.Instance.OpenCigaretteColor(),
        ["stamp_guide"]=_=>PaperUIManager.Instance.OpenStampGuide(),
        ["injector_guide"]=_=>PaperUIManager.Instance.OpenInjectorGuide(),
        ["aug_id_guide"]=_=>PaperUIManager.Instance.OpenAugIdGuide(),
        ["hand_guide"]=_=>MultiPageUIManager.Instance.OpenUI(6),
        ["fuel_guide"]=_=>MultiPageUIManager.Instance.OpenUI(0),
        ["ribwich_guide"]=_=>MultiPageUIManager.Instance.OpenUI(4),
        ["water_guide"]=_=>MultiPageUIManager.Instance.OpenUI(1),
        ["hydroponic_guide"]=_=>MultiPageUIManager.Instance.OpenUI(2),
        ["chemistry_guide"]=_=>MultiPageUIManager.Instance.OpenUI(3),
        ["ingot_book"]=_=>MultiPageUIManager.Instance.OpenUI(5),
        ["wine_book"]=_=>MultiPageUIManager.Instance.OpenUI(7),
        ["logo_checker"]=_=>RepublicLogoUIManager.Instance.OpenPanel(),
        ["mentor_contract"]=_=>PaperUIManager.Instance.OpenContract(),
        ["deed"]=_=>PaperUIManager.Instance.OpenDeed(),
        ["procurement_slip"]=i=>PaperUIManager.Instance.OpenSlipUI(i),
        ["slip"]=i=>PaperUIManager.Instance.OpenGeneralSlipUI(i),
        ["handnote"]=i=>HandnoteUIManager.Instance.OpenPanel(i),
        ["joe_card"]=_=>PaperUIManager.Instance.ToggleJoeCard(),
        ["card_brewer"]=_=>PaperUIManager.Instance.OpenBrewerCard(),
        ["card_mining"]=_=>PaperUIManager.Instance.OpenMiningCard(),
        ["card_pharma"]=_=>PaperUIManager.Instance.OpenPharmaCard(),
        ["card_chem"]=_=>PaperUIManager.Instance.OpenChemCard(),
        ["card_rev"]=_=>PaperUIManager.Instance.OpenCard("card_rev"),
        ["sec_slip"]=i=>PaperUIManager.Instance.OpenCard("sec_slip",i),
        ["wanted_paper"]=_=>PaperUIManager.Instance.OpenWantedPaper()
    };
    private bool OpenLocalDocument(GameItem item)
    {
        if(!documentViews.TryGetValue(item.identifier,out var open)) return false;
        if(Accessible(item.parentInventory) && ItemQuery(item,"MayActivateSlotItem") && ItemQuery(item,"CanActivateSlotItem"))
        {
            open(item);
            if(item.IsTag("TUTORIAL_PAPER_TAG")) QueueDocumentRead(item);
        }
        return true;
    }
}
