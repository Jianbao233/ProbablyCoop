using System.Collections.Generic;

namespace ProbablyCoop.Api;

/// <summary>
/// Describes the ordered native storage children of a custom machine wrapper.
/// Implement on the wrapper that owns these slots; return actual graph
/// children and avoid creating, mutating, or reparenting them in this getter.
/// </summary>
public interface ICoopNativeStorageLayout
{
    IReadOnlyList<GameInventory> CoopStorage { get; }
}
