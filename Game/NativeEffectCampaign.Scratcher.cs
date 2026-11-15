using HarmonyLib;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private static readonly System.Reflection.FieldInfo ScratchRevealed = AccessTools.Field(typeof(ScratchInput), "revealed");
    private GameItem visibleTicket;
    private int visibleSymbols, visibleReveals;

    private static bool ApplyScratch(GameItem ticket, ItemUse use, byte slot)
    {
        if (!ticket.IsTag(ScratcherHelper.SCRATCHER_TAG)) return false;
        if (use == ItemUse.OpenScratcher)
        {
            if (ScratcherHelper.IsAnySymbolScratched(ticket) || HealthData.GetLuck() <= LotteryHelper.GetSpawnLuck(ticket))
                return false;
            ScratcherHelper.TryRerollForBetterLuck(ticket);
            return true;
        }
        if (use == ItemUse.StartScratch && !ticket.IsTag(ScratcherHelper.SCRATCHER_NEW_TAG) ||
            use == ItemUse.RevealSymbol && IsRevealed(ticket, slot)) return false;

        var ui = ScratcherUIManager.Instance;
        var previous = ui.currentActiveScratcherItem;
        try
        {
            // Run the game's counters and prize calculation without opening
            // this player's window or replacing its bound ticket.
            ui.currentActiveScratcherItem = ticket;
            ui.scratchInputs[0].OnAnyScratch();
            if (use == ItemUse.RevealSymbol) ui.OnScratchComplete(slot);
        }
        finally { ui.currentActiveScratcherItem = previous; }
        return true;
    }

    internal static bool IsRevealed(GameItem ticket, int slot) =>
        ticket.GetTagReadonly(ScratcherHelper.SCRATCHER_STATE + slot).GetInt() == 1;

    private void UpdateScratchView()
    {
        var ui = ScratcherUIManager.Instance;
        var ticket = ui?.currentActiveScratcherItem;
        if (ticket == null || !ui.ui.activeSelf)
        {
            visibleTicket = null;
            return;
        }
        if (!inventory.CanInteract(ticket))
        {
            ui.CloseUI();
            visibleTicket = null;
            return;
        }

        int symbols = 0, reveals = 0;
        for (int slot = 1; slot <= 3; slot++)
        {
            symbols = symbols * 8 + ticket.GetTagReadonly(ScratcherHelper.SCRATCHER_SYMBOL + slot).GetInt();
            if (IsRevealed(ticket, slot)) reveals |= 1 << (slot - 1);
        }
        bool sameTicket = ReferenceEquals(visibleTicket, ticket);
        if (!sameTicket || symbols != visibleSymbols) ui.SetPrizeSprite();
        int newReveals = sameTicket ? reveals & ~visibleReveals : reveals;
        for (int index = 0; index < 3; index++)
            if ((newReveals & (1 << index)) != 0 && !(bool)ScratchRevealed.GetValue(ui.scratchInputs[index]))
                ui.scratchInputs[index].RevealAll();
        visibleTicket = ticket;
        visibleSymbols = symbols;
        visibleReveals = reveals;
    }
}
