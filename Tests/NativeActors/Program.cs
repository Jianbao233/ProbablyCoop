using System.Runtime.Loader;

// Resolve installed reference assemblies in place. Never copy game DLLs into
// a test or release package. The probes never construct PlayerStore, access its
// singleton, run Unity methods, or read/write any native save.
var managed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-managed-path.txt")).Trim();
if (!System.IO.Directory.Exists(managed)) throw new DirectoryNotFoundException(managed);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
if (args.Length != 0) return Rev13EntryPoints.Run(args);
ActorStateTests.Run();
ActorRunProbe.Run();
ActorProjectionProbe.Run();
var inventoryResult = ActorInventoryTests.RunAll();
var fieldResult = ActorFieldTests.RunAll();
var carryResult = ActorCarryAccessTests.RunAll();
return inventoryResult == 0 && fieldResult == 0 && carryResult==0 ? 0 : 1;
