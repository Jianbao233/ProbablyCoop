using System;
using System.IO;
using System.Runtime.Loader;

// Newtonsoft stays in the installed Managed directory. All native types in
// Logic.cs are explicit doubles; nothing reads or changes a player's save.
internal static class CheckpointLogicEntry
{
    private static void Main()
    {
        string managed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-managed-path.txt")).Trim();
        AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "Newtonsoft.Json"
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Newtonsoft.Json.dll")) : null;
        CheckpointLogicTests.RunAll();
    }
}
