using System.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    // Direct feed into a production machine can consume the source and
    // create several outputs. Let the native host allocate them together.
    internal void RequestProductionTarget(GameItem source, GameItem machine)
    {
        var intent = new ActivityIntent(ActivityAction.ProductionTarget, ProductionProof(source, machine),
            source.uniqueId, machine.uniqueId);
        if (!intent.Valid) return;
        if (host) ExecuteProductionTarget(SyncHost.HostPeer, intent);
        else Session.Request(AuthorityAction.Activity, StoreStation.GetDayCounter(), activity: intent);
    }

    private ulong ProductionProof(GameItem source, GameItem machine)
    {
        ulong sourceProof = inventory.ControlProof(source), machineProof = inventory.ControlProof(machine);
        if (sourceProof == 0 || machineProof == 0) return 0;
        var writer = new SyncWriter();
        writer.ULong(sourceProof);
        writer.ULong(machineProof);
        return ActivityProof(writer.ToArray());
    }

    private void ExecuteProductionTarget(byte actor, ActivityIntent intent)
    {
        var actors = NativeActorSession.Current;
        if (actors == null || actors.Terminal || actors.Faulted || actors.NightActive ||
            !inventory.TryFindAccessible(intent.Owner, actor, out var source) ||
            !inventory.TryFindAccessible((int)intent.Value, actor, out var machine) ||
            !machine.IsTag(MachineAutoButcher.AUTO_BUTCHER_TAG) || TouchesHand(new[] { source, machine }, source, actor) ||
            ProductionProof(source, machine) != intent.Proof || !BeginAuthorityAction()) return;
        bool completed = false;
        try
        {
            using (actors.Enter(ToSlot(actor)))
            {
                if (!source.MayTarget(machine) || !source.CanTarget(machine)) return;
                source.Target(machine);
                completed = true;
            }
        }
        finally { EndAuthorityAction(completed); }
    }

    internal void RequestMachine(GameItem owner, int index, PixelElement widget, float first = 0, float second = 0)
    {
        var command = new MachineIntent(owner.uniqueId, index, NativeMachineControls.Kind(widget),
            inventory.ControlProof(owner), first, second);
        if (!command.Valid || !GeneralHelper.IsItemOwned(owner)) return;
        bool sent = Session.Request(AuthorityAction.Machine, StoreStation.GetDayCounter(), machine: command);
#if PROBABLY_COOP_GRID_QA
        UnityEngine.Debug.Log($"[probably_coop MACHINE] requested {command.Control} item={command.Owner} sent={sent}");
#endif
    }

    internal void RequestMachineOption(GameItem owner, MachineControl control, string option = "")
    {
        var command = new MachineIntent(owner.uniqueId, 0, control, inventory.ControlProof(owner), choice: option);
        if (command.Valid) Session.Request(AuthorityAction.Machine, StoreStation.GetDayCounter(), machine: command);
    }

    private void ExecuteMachine(MachineIntent command)
    {
        var actors = NativeActorSession.Current;
        if (actors == null || actors.Terminal || actors.Faulted ||
            !inventory.TryFindAccessible(command.Owner, NetSession.GuestPeer, out var owner) ||
            !GeneralHelper.IsItemOwned(owner) || TouchesHand(new[] { owner }) ||
            inventory.ControlProof(owner) != command.Proof)
        {
#if PROBABLY_COOP_GRID_QA
            UnityEngine.Debug.Log($"[probably_coop MACHINE] refused {command.Control} item={command.Owner} due to stale context");
#endif
            return;
        }
        if (!BeginAuthorityAction()) return;
        bool finished = false;
        try
        {
            using (actors.Enter(NativeActorRun.Slot.Partner))
                finished = ApplyMachineCommand(owner, command);
#if PROBABLY_COOP_GRID_QA
            UnityEngine.Debug.Log($"[probably_coop MACHINE] executed {command.Control} item={command.Owner} changed={finished}");
#endif
        }
        finally { EndAuthorityAction(finished); }
    }

    private static bool ApplyMachineCommand(GameItem owner, MachineIntent command)
    {
        if (command.Control == MachineControl.Toggle)
        {
            if (!owner.CanToggleSlotItem()) return false;
            owner.ToggleSlotItem();
            return true;
        }
        var controls = NativeMachineControls.Read(owner);
        if (command.Control == MachineControl.Recipe)
        {
            if (!controls.Any(NativeMachineControls.OpensRecipePanel) ||
                !ChemFinisherRecipe.chemFinisherRecipes.Any(recipe => recipe.identifier == command.Choice)) return false;
            var ui = ChemFinisherUIManager.Instance;
            var localDraft = ui.currentItem;
            try { ui.currentItem = owner; ui.OnChemRecipeSelected(command.Choice); }
            finally { ui.currentItem = localDraft; }
            return true;
        }
        return command.Index < controls.Count && NativeMachineControls.Kind(controls[command.Index]) == command.Control &&
            NativeMachineControls.Execute(controls[command.Index], command);
    }
}
