# ProbablyCoop Extension API v1

Use the extension API when a Mod adds its own shared action or small state that
ProbablyCoop cannot derive from the original game. Examples include accepting a
new kind of contract and displaying its current terms. Use the game's normal
item registration and inventory paths for objects that those paths already
support.

第三方 Mod 可注册自己的共享动作和少量共享数据。比如新合同的接受按钮和合同进度。
作者负责规则、校验和存档，ProbablyCoop 负责把请求交给房主、按顺序去重和恢复客机界面。
原版库存、可识别的窗口和普通物品仍走共用路径。

## Choose what to integrate / 先判断哪里需要接入

| Mod behavior / 功能 | Integration / 接法 |
| --- | --- |
| Original game item registration, supported slots, and normal inventory actions / 原版物品、已支持的原版槽位和常规拿放 | Reuse those game entry points. Let ProbablyCoop replicate the resulting items and positions. / 走原版入口，联机层同步物品和位置。 |
| Custom shared button, batch move, currency, unlock, order, or quest / 自定义共享按钮、批量动作、货币、订单和任务 | Register the intent as an action, validate it on the host, and publish the small result the guest needs. / 把操作意图注册为动作，由房主校验后执行，再投影客机需要的小型状态。 |
| Local layout, language, audio, or animation / 各端界面、语言、音效和动画 | Render from the facts the local client has. The guest receives the result of shared actions and may never invoke the original host-side event. / 各端根据已收到的事实刷新。客机可能只收到成交结果，原版成交回调只在房主触发。 |

The v1 API carries actions and state. Keep real storage in supported native
inventory nodes. A custom container layout needs a separate structural adapter.
Connect a custom control's click handler to `Request`.

v1 提供共享动作和状态同步。实际储物请使用联机已支持的原版库存节点。自定义按钮可以用
`Request` 提交动作。可以用下文的接口向联机层声明自定义布局下的原版储物节点。

## Guest-local item windows / 客机本地物品窗口

Use this optional service if the original item Activate command opens a local
window, such as a manual or a settings panel. The publisher must own the
item's source tag. Set it with the game's `ModHelper.SetSource` when creating
the item. Keep the original item ability checks enabled so the native context
menu offers the action.

如果物品的原版「激活」只负责打开说明或设置窗口，可以声明一个本机服务。
创建物品时用游戏的 `ModHelper.SetSource` 标记所属 Mod，并保留原版激活条件，
让右键菜单仍能显示该动作。

```csharp
// OnEnable
ModHelper.Expose(MyModId, CoopExtensionApi.LocalItemActivationService,
    (Func<GameItem, bool>)(item =>
    {
        if (item.identifier != MyTerminalItemId) return false;
        OpenTerminalWindow(item);
        return true;
    }));

// OnDisable
ModHelper.Expose(MyModId, CoopExtensionApi.LocalItemActivationService, null);
```

The guest calls the service on the game thread for their own accessible item.
Return `true` after handling the click locally. Return `false` for items that
should follow the ordinary activation path. Host and single-player actions
keep the Mod's original handler. Inventory, money, and story changes use
the shared action API below.

客机只对自己能访问且已拥有的物品，在游戏线程调用物品所属 Mod 的服务。
本地处理后返回 `true`，其他物品返回 `false` 继续走普通激活流程。
房主与单人仍使用 Mod 原有处理。库存、现金和剧情改动通过下文的共享动作提交。

## Custom storage wrappers / 自定义储物布局

A custom `PixelElement` that owns native storage can implement
`ProbablyCoop.Api.ICoopNativeStorageLayout`. Return all of its direct graph
children in display order from `IReadOnlyList<GameInventory> CoopStorage`.
They must be the real instances of `GameSlotInventory`, `GameGridInventory`,
or `GameGridScrollableInventory`. The getter must be read-only. A cached array
is sufficient. The cooperative view draws the slots with a native vertical
layout and routes access through the ordinary ownership, lock, and inventory
checks. Up to 24 distinct slots can be declared. The owning Mod must be enabled.

自定义 `PixelElement` 如果直接拥有原版储物节点，可以实现
`ProbablyCoop.Api.ICoopNativeStorageLayout`，按显示顺序返回完整的直接子节点。
支持原版单格、网格和可滚动库存，最多 24 个不同的真实节点。读取属性时无需创建、
复制或重新挂载库存。客机会用原版纵向布局显示这些真实槽位，拿放仍由房主按普通
归属、锁定和容量规则校验。界面的自定义按钮、批量动作和特殊规则仍可登记为扩展动作。

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

// OnDisable: use the same named handler instance.
ModHelper.OffMessage(MyModId, CoopExtensionApi.DiscoveryChannel, Discover);
currentCoopSession = null;
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
| `ReadState` | Host's native actor and location context | Return a quick, read-only summary of current shared gameplay state. Use a stable textual encoding and omit presentation and sensitive data. Called during capture, before and after `Execute`, and when host UI reads `CurrentState`. Use your Mod's model as the source for shared state and avoid inventory scans and file reads. |
| `Execute` | Host | Validate the permitted action, parsed payload, actor, relevant scene, funds, ownership, and other current game preconditions. Return `Rejected`, `Stale`, or `Busy` before changing anything if a check fails. Apply a valid action once and return `Applied`. |
| `ProjectState` | Guest | Receive the complete extension state after the guest's native location has been set. A location change can deliver the same revision again so you can rebind local UI. This runs during projection, before the handle can read the newly applied state or submit another action. Make this callback idempotent and limit it to local presentation. |
| `Attach` | Both | Store the handle before its initial state is available. It becomes ready when the current shop has usable state. The host can use the handle while waiting for a guest. A new guest session receives a new handle and a full current projection. |

Each extension must declare 1 to 32 unique action IDs. An ID or protocol is at most 64 ASCII
letters, digits, dots, dashes, or underscores. A shop supports up to 16
extensions, each with at most 4,096 UTF-8 bytes of state. Action payloads are at
most 1,024 UTF-8 bytes and must fit the existing 4,096-byte serialized command
envelope. The API passes strings and registered action IDs and leaves data
parsing to the author. Use your own fixed data schema. No remote assembly,
method, or type lookup is part of that protocol.

Persist durable progress using your own normal Mod save data on the host. The
extension snapshot and its revision are current room state. They are carried to
rejoining guests and they do not create a new kind of saved game. Returning a
rejection requires leaving the game unchanged. ProbablyCoop checks that the
state reported by `ReadState` is identical when `Execute` declines a request.
A mismatch or an exception stops the run through fault recovery without
saving the current scene. Validate first and keep accepted mutations bounded
and synchronous. Include all relevant shared state in `ReadState` so a
rejected operation cannot silently change it.

## Submit an action from your UI

Keep the revision of the state that the panel actually rendered. A host panel
can read `CurrentState` when it refreshes. On the guest, save the revision
supplied to `ProjectState`.

```csharp
var session = currentCoopSession;
if (session?.IsReady == true)
{
    ShowPending();
    var submission = session.Request(
        "accept", selectedContractId, displayedRevision,
        result =>
        {
            if (ReferenceEquals(currentCoopSession, session))
                UpdateMyActionFeedback(result);
        });
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
callback before `Request` returns `Completed`. An action that faults reports `Failed`
and the host must restore the last verified checkpoint. Set UI pending state before calling
`Request`. A guest receives `Queued`. On
success its callback runs after the authoritative projection containing the
receipt has been applied. A queued request can also finish with a rejection,
`Stale`, `Busy`, or `Disconnected`. After a disconnect, read the recovered
state before deciding what to do with an unfinished operation.

Use the handle on the game thread. Use `IsReady` to enable the action button.
`IsClosed`, an additive v1 property, identifies a permanently expired handle.
Discard that handle and wait for the next `Attach` in a cooperative UI.
`IsReady == false` can also mean initial loading or applying a projection.
Use your own original entry point only when the current game is known to be
single-player. Call `CurrentState` and `Request` from UI handlers or regular
game-thread updates outside the extension callbacks.

客机界面保存最近一次显示的状态版本，提交时带上该版本。房主的完成回调可能在
`Request` 返回前就执行，所以提交前先开启等待提示。`IsReady` 决定按钮是否可用，
`IsClosed` 用来丢弃已经过期的会话。客机正在加载、掉线或更新投影时，停用共享操作，
等新的会话或状态到达。共享状态回调在主线程执行，`ReadState` 始终在房主的角色与地点上下文中读取本 Mod 的数据，
`ProjectState` 在客机的原版地点就绪后刷新界面。地点切换时，相同版本的状态也可能再次传入，供 Mod 重新绑定本地界面。
`Execute` 拒绝请求后，`ReadState` 返回的共享状态必须和执行前一致。若状态已改变或执行抛错，合作档会进入无需写档的故障恢复流程。请把会被动作改变的共享数据纳入 `ReadState`。

## Customer appearance on the guest

ProbablyCoop resolves the portrait key through the original `SpriteDict.ShowClientSprite`
and `GetSprite` path. A cosmetic Mod whose usual entry point is customer generation
can also listen for `probably_coop:customer-appearance-v1` with `ModHelper.OnMessage`.
The payload is `CoopCustomerAppearance`. Its `PortraitKey` is the requested asset key
and `Restoring` indicates the first projected appearance after joining or reloading.
The guest can apply a local visual override to `StoreClientMono.Instance.image`
inside this callback. Remove the listener on disable. This message is local to the
guest and requires no shared-action registration.

客机通过原版 `SpriteDict.ShowClientSprite` 和 `GetSprite` 查找立绘。如果美化 Mod
原先是在生成顾客时换图，也可以用 `ModHelper.OnMessage` 监听本地消息
`probably_coop:customer-appearance-v1`。参数 `CoopCustomerAppearance` 提供立绘资源键
`PortraitKey` 和是否为入房或读档后的首次恢复 `Restoring`。回调内可以调整客机自己的
`StoreClientMono.Instance.image`。停用 Mod 时移除监听。这个消息只在本机触发，无需登记共享动作。

## Starting careers / 开局职业

Register the scenario with the game's `ModHelper.AddStartingScenario` first. To list it in a paired opening, expose the scenario ID, exact net cash cost, and any incompatible career IDs. Both players need the same enabled package. Use `ModHelper.IsStartingScenario(id)` to check both careers in your `onStart` callback and in `OnGameLoadedEarly` or later load callbacks. `GetStartingScenarioId()` returns the career currently initializing during its `onStart` callback and the host's career during normal play.

先用游戏的 `ModHelper.AddStartingScenario` 登记职业，再声明职业 ID、实际现金扣款和互斥职业。双方需要启用同一份 Mod。在 `onStart` 和从 `OnGameLoadedEarly` 开始的读档回调中，可以用 `ModHelper.IsStartingScenario(id)` 查询两人所选职业。`GetStartingScenarioId()` 在 `onStart` 内返回正在初始化的职业，正常游玩时返回房主所选职业。

```csharp
const string CareerId = "example_brewer";
ModHelper.AddStartingScenario(MyModId, CareerId, () => "Brewer",
    () => "Start with supplies for your shop.",
    new Func<string>[] { () => "Processed juice" },
    onStart: () =>
    {
        GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.invElement, DirectoryMaster.Item("processed_juice"));
        PlayerStore.Instance.playerCash -= 100;
    });
ModHelper.Expose(MyModId, CoopExtensionApi.OpeningCareersService,
    new[] { new CoopOpeningCareer(CareerId, cashCost: 100) });
```

The host invokes the original `onStart` synchronously once per distinct selected career. The two careers share one shop and one wallet. Declare a positive net cost for a charge, zero for a free career, or a negative net cost for a grant. A callback must finish within the original new-game initialization without scheduling another save or reinitializing the shop. A callback failure or cash mismatch stops the opening before its first save. Continue to use the shared action API for new runtime behavior. The base game's service capability rules apply.

房主对每个不同的已选职业同步执行原版 `onStart` 一次，店铺和钱包共用。扣款填正数，免费填零，奖励现金填负数。回调应在原版开局初始化内完成，不安排第二次保存或重置店铺。回调抛错或实际现金与声明不符会在首次保存前停止开局。新增游玩行为仍走共享动作接口。此服务遵循游戏的能力声明规则。

## Starting perks / 开局天赋

Register each perk using the game's `ModHelper.AddStartingPerk`. Expose its ID and
scope so either player can choose it in the original opening picker when both
players have the same enabled Mod package. Native points, slots, and
incompatibilities still apply.

用游戏的 `ModHelper.AddStartingPerk` 登记天赋，再声明天赋 ID 和作用范围。双方启用了同一份 Mod 包时，可在原版双人开局界面选择。点数、栏位和互斥继续走原版规则。

```csharp
ModHelper.Expose(MyModId, CoopExtensionApi.OpeningPerksService,
    new Dictionary<string, CoopOpeningPerkScope>
    {
        ["example_careful_hands"] = CoopOpeningPerkScope.Personal,
        ["example_shop_service"] = CoopOpeningPerkScope.SharedShop
    });
```

Use `ModHelper.IsStartingPerkActive(id)` in your gameplay rules. A
`Personal` perk follows the local player and the actor executing a cooperative
action on the host. A `SharedShop` perk applies to the shop if either player
chose it. For custom grants, shop effects, and state changes, use your original
game hooks together with the shared action interface. The cooperative save records the selected provider. The room check requires
its active package on both ends. Both players can update that package together.

玩法代码可以用 `ModHelper.IsStartingPerkActive(id)` 查询。`Personal` 跟随本地角色，房主处理联机动作时跟随实际操作者。`SharedShop` 是全店天赋，任一玩家选择后全店有效。自定义赠品、全店效果和后续状态修改可以通过原版钩子与共享动作接口实现。合作档会记录提供已选天赋或职业的 Mod。重新入房时，两端须启用同一份包。双方可以一起更新。

## Verify an adapter

Build the independent example in `Tests/ModExtensionProbe/` with
`-p:CoopApiAssembly=<absolute-path-to-ProbablyCoop.dll>`. It registers a counter
through the same game-local messaging API as a third-party Mod. For the automated
probe, build ProbablyCoop with `-p:ProbablyCoopGridQa=true` and install the output
from `torelease/qa/ProbablyCoop` in both isolated QA profiles. With the example
activated on both ends, add `--probably-coop-qa-extension-probe` to both test
launches. The partner requests one increment, checks
that the successful reply observes the new count, and retries using the old
revision to verify rejection. Relaunch the partner with
`--probably-coop-qa-extension-probe --probably-coop-qa-extension-rejoin` to verify
the current state. Launching a separate QA guest without the extension exercises
the missing-Mod check in its log.
