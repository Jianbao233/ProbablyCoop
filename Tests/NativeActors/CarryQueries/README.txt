Pass the path of an independently built ProbablyCoop.dll:

dotnet run --project Tests/NativeActors/CarryQueries/ProbablyCoop.ActorCarryQueries.Tests.csproj -c Release -- <path-to-ProbablyCoop.dll>

This suite loads real installed native metadata in place, validates the six
audited afterhourWindow graph consumers, exercises copied IL metadata and
malformed-contract rejection, and calls the compiled Mod's graph facade against
uninitialized native field shells and explicit slot-search doubles.

It does not patch/run the game, invoke native constructors, run Unity rendering,
or read/write native saves. Slot search priority tests use explicit doubles;
they do not prove native transfer/animation behavior in the running game.
