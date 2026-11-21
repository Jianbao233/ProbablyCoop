# ProbablyCoop Extension API v1

Use the extension API when a Mod adds its own shared action or small state that
ProbablyCoop cannot derive from the original game. Examples include accepting a
new kind of contract and displaying its current terms. Use the game's normal
item registration and inventory paths for objects that those paths already
support.

第三方 Mod 可注册自己的共享动作和少量共享数据。比如新合同的接受按钮和合同进度。
作者负责规则、校验和存档，ProbablyCoop 负责把请求交给房主、按顺序去重和恢复客机界面。
原版库存、可识别的窗口和普通物品仍走共用路径。

## Registration

Reference the matching `ProbablyCoop.dll` at build time with `Private=false`,
and declare `probably_coop` in `<Prerequisites>`. Ship your own DLL only. A Mod
that also supports playing without co-op can keep this code in a separate
adapter package.

In `OnEnable`, subscribe on the game thread to
`CoopExtensionApi.DiscoveryChannel` with the ID of your own Mod. Remove the same
handler in `OnDisable`. Use `ModHelper.SendMessage` to return one
`CoopExtensionRegistration` synchronously when the sender is `probably_coop`.
The original game's local Mod message system validates the caller's ID.

```csharp
using ProbablyCoop.Api;

// Subscribe from your IMod implementation.
ModHelper.OnMessage(MyModId, CoopExtensionApi.DiscoveryChannel, Discover);

void Discover(string sender, object _)
{
    if (sender != "probably_coop") return;
    var registration = new CoopExtensionRegistration(
        "contracts-v1",
        new[] { "accept" },
        ReadCurrentContractState,
        ExecuteWithCurrentRules,
        UpdateLocalContractPanel,
        session => currentCoopSession = session);
    ModHelper.SendMessage(MyModId, CoopExtensionApi.RegistrationChannel, registration);
}
```

Both peers register the same Mod ID, manifest version, protocol and action
names. The join menu checks for differences before the partner chooses perks
or the native shop opens. A mismatch shows the relevant Mod ID in the join
panel. The host keeps the existing room. Only Mods that register participate in
this check.
Restart the room after changing any enabled gameplay Mods or their protocol.

## Implement your callbacks

| Callback | Runs on | Contract |
| --- | --- | --- |
| `ReadState` | Host's native actor context | Return a quick, read-only summary of current shared gameplay state. Use a stable textual encoding and omit presentation and sensitive data. Called during normal capture and before execution, so avoid inventory scans or file reads. |
| `Execute` | Host | Validate the permitted action, parsed payload, actor, relevant scene, funds, ownership, and other current game preconditions. Return `Rejected`, `Stale`, or `Busy` before changing anything if a check fails. Apply a valid action once and return `Applied`. |
| `ProjectState` | Guest | Receive the complete current extension state. Make this callback idempotent. Update the local view and show the corresponding native model. The guest callback should have no gameplay or save effects. |
| `Attach` | Both | Keep the issued session handle. It becomes ready when the current shop has usable state and expires on leaving it. The host can use the handle while waiting for a guest. A new guest session gets a new handle and a full current projection. |

Each extension has up to 32 action IDs. An ID or protocol is at most 64 ASCII
letters, digits, dots, dashes, or underscores. A shop supports up to 16
extensions, each with at most 4,096 UTF-8 bytes of state. Action payloads are at
most 1,024 UTF-8 bytes and must fit the existing 4,096-byte serialized command
envelope. The API passes strings and registered action IDs and leaves data
parsing to the author. Use your own fixed data schema. No remote assembly,
method, or type lookup is part of that protocol.

Persist durable progress using your own normal Mod save data on the host. The
extension snapshot and its revision are current room state. They are carried to
rejoining guests and they do not create a new kind of saved game. Returning a
rejection requires leaving the game unchanged. A throwing action has no
transactional rollback and the cooperative run enters its existing fault
recovery path. Validate first and keep accepted mutations bounded and synchronous.

## Submit an action from your UI

```csharp
if (currentCoopSession?.IsReady == true && currentCoopSession.CurrentState is { } state)
{
    var submission = currentCoopSession.Request(
        "accept", selectedContractId, state.Revision,
        result => UpdateMyActionFeedback(result));
    if (submission is not (CoopActionSubmission.Queued or CoopActionSubmission.Completed))
        ShowActionIsUnavailable();
}
```

Capture the extension revision corresponding to what the player selected. A
competing change makes an older revision stale. For an action that depends on
other original game state, perform those checks in your own host callback.
`Execute` gets the actual room actor from ProbablyCoop. The guest cannot choose
its actor, and the host runs it in the partner's native context.

The host executes its own request synchronously and calls the optional completion
callback before `Request` returns `Completed`. A guest receives `Queued`. On
success its callback runs after the authoritative projection containing the
receipt has been applied. A queued request can also finish with a rejection,
`Stale`, `Busy`, or `Disconnected`. After a disconnect, read the recovered
state before deciding what to do with an unfinished operation.

The callbacks run on the game thread. The extension should use its own normal
single-player entry points when there is no co-op session. A closing or unready
partner session must never fall back to executing shared game actions locally.

## Verify an adapter

Build the independent example in `Tests/ModExtensionProbe/` with
`-p:CoopApiAssembly=<absolute-path-to-ProbablyCoop.dll>`. It registers a counter
through the same game-local messaging API as a third-party Mod. In isolated QA
with the package activated on both ends, launch both with
`--probably-coop-qa-extension-probe`. The partner requests one increment, checks
that the successful reply observes the new count, and retries using the old
revision to verify rejection. Relaunch the partner with
`--probably-coop-qa-extension-probe --probably-coop-qa-extension-rejoin` to verify
the current state. Launching a separate QA guest without the extension exercises
the missing-Mod check in its log.
