This isolated net9.0 suite links the production NativeActorCarry.cs and explicit
game/Unity doubles. It verifies actor slot routing, original slot identity,
nonreplicated hidden rendering parents, rule attachment, selected drafts,
capacity/remainder accounting, no shared-ground cleanup, and lifecycle guards.
It does not load native game assemblies, run Unity, or touch a save.

Run: dotnet run --project Tests/NativeActors/CarryLogic/ProbablyCoop.ActorCarry.LogicTests.csproj -c Release

ActorCarryAccessTests is the separate real-game metadata/typed-wrapper suite.
Actual constructor rendering and native transfer behavior still require the
isolated game regression runner owned by the main task.
