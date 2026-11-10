NativeActorNight isolated logic checks

Run from the Mod root:
dotnet run --project Tests/NativeActors/NightLogic/ProbablyCoop.ActorNight.LogicTests.csproj -c Release

This suite links the production NativeActorNight source and uses explicit
HealthData, LotteryHelper and Harmony test doubles in Logic.cs. Its 45 assertions
cover actor scopes, one shared draw, repeated-day rejection, terminal handling,
partial failure and the IL replacement algorithm.

PASS does not prove native HealthData behavior, Harmony runtime patching,
cooperative gameplay, Steam networking, or save/load. No game DLL is referenced,
copied or executed, and no game profile or save is read or written.

Original frozen probe: Codex work/NativeActorNightCheck/Logic.cs
SHA256 3885EABE946C0D5D1804B2B4158AB33A6764FD90AA2E642E1B172C8DB135183A
