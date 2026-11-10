# Actor mechanism checks

Ordinary cooperative runs now activate the two-actor model through `NativeActorSession`. These offline projects test mechanisms; passing them does not certify complete gameplay.

Run from the ProbablyCoop repository:

```powershell
dotnet run --project Tests/NativeActors/ProbablyCoop.NativeActors.Tests.csproj -c Release
dotnet run --project Tests/NativeActorActivities/ProbablyCoop.NativeActorActivities.Tests.csproj -c Release
dotnet run --project Tests/NativeActors/NightLogic/ProbablyCoop.ActorNight.LogicTests.csproj -c Release
dotnet run --project Tests/NativeActors/CheckpointLogic/CheckpointLogic.csproj -c Release
dotnet run --project Tests/NativeActors/CarryLogic/ProbablyCoop.ActorCarry.LogicTests.csproj -c Release
dotnet run --project Tests/NativeActors/CarryUiLogic/ProbablyCoop.ActorCarryUi.LogicTests.csproj -c Release
dotnet run --project Tests/NativeBarterOfferLogic/ProbablyCoop.NativeBarterOfferLogic.Tests.csproj -c Release
```

The main actor project resolves installed game assemblies through the repository's `PSGameDir` / `GameManaged` properties. It exercises real native data types, field shells, verified IL field/carry access, actor scope lifetime, projection preflight and ownership. It never constructs `PlayerStore`, accesses its singleton, invokes Unity engine methods, or reads/writes native saves. Emitted IL is not a Mono Harmony installation check. The activity project also uses field shells; its tests cover delayed callbacks, role-specific lifetime guards, personal notifications and perk/setup identity.

To check the Rev12 production entry points against the installed game assembly, pass a freshly built mod DLL to the main actor test project. This mode reads original IL and runs the mod's instruction rewrites without installing patches. It checks the purchase/wholesale/barter route to the guarded stash, the night and paper-window sequence, and the save/hydration anchors. The checkpoint logic suite separately tests reused slots, matching legacy markers and stale checkpoint identities using isolated fixtures.

```powershell
dotnet run --project Tests/NativeActors/ProbablyCoop.NativeActors.Tests.csproj -c Release -- <path-to-ProbablyCoop.dll>
```

NightLogic, CheckpointLogic, CarryLogic and CarryUiLogic use explicit native doubles. CheckpointLogic creates synthetic files under its own output directory. These results do not prove original-game overnight behavior, native UI layout, ES3 parsing or native save/load. CarryUiLogic additionally accepts a compiled Mod DLL as its sole argument to check the actual game types and raw panel-wrapper identities; that mode still does not invoke Unity.

Real regression requires two prepared copies with distinct Unity product identities and LocalLow profiles. `Run-NativeBackground.ps1` verifies isolation, uses a private Windows desktop, archives evidence and restores only its disposable QA saves. It does not activate the user's desktop or send system keyboard/mouse input. The current ordinary actor gates are:

1. `-FreshRun -Windowed -SaveRoundTrip`: original new-run menus, Partner perk picker, first-day progression and first native checkpoint.
2. `-ActorSeedDirectory <successful-fresh-evidence> -Windowed -SaveRoundTrip -Reconnect`: actual loaded actor checkpoint, independent carry/consumption, original controls, shared journeys, three nights and Guest process restart. The QA fixture explicitly advances to day 6 and provides mechanism-test inventory; it is not a claim of naturally playing days 2 through 5.
3. `-ActorSeedDirectory <successful-full-evidence> -ActorRestoreOnly -Windowed -SaveRoundTrip`: a new Host process loads the archived day-9 native save and a new Guest joins. Exact personal states, both nonempty nested bags, native IDs, setup receipt and native file bytes must match. No fixture is reseeded. The Host's real runtime Stop must exit before actor cleanup, without rewriting the checkpoint.

All commands also require `-StageRoot`, `-ProductPrefix` and a new `-EvidenceDirectory`. Optional `-LanAddress <local-IPv4>` exercises the direct-IP transport; two processes on one computer are still not a two-device LAN test. `Compare-ActorReload.py --self-test <directory>` validates synthetic acceptance/rejection cases only.

For bounded lifecycle debugging, `-ActorSeedDirectory <successful-fresh-evidence> -LifecycleOnly -Windowed -SaveRoundTrip` reuses the original document/service orders, joint journeys and three-night assertions. It skips the separate frontend and reconnect suite and is explicitly **not** a substitute for any of the three delivery gates above.

`-MechanismsOnly -Windowed -SaveRoundTrip` with an actor seed also checks the original chemistry recipe and output controls, portable water purification, and gun attachment installation and removal. It compares consumed material, product, water purity, purifier durability and gun reliability on both peers. These use the original input handlers on the private desktop, without physical mouse input.

The same run checks a preset with two items and a saved tab, then transfers the same two items through two linked hopper pairs. The Guest exits while aiming and again after a shot is accepted. The runner records both exits and starts new Guest processes. The Host must finish each incident once, and each new Guest must reject a replay of its previous command.

The old R2 `-ActorFields -ActorProjection` input slice belongs to historical architecture evidence. It is not the current production activation path or a substitute for the ordinary fresh/full/reload gates. Report `PASS`, `FAIL` or `NOT_RUN` against the exact package and actual completed run; never promote field-shell or synthetic evidence to gameplay acceptance.
