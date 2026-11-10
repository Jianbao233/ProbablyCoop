using System;

namespace ProbablyCoop.Core;

internal static class DisposableProduct
{
    internal static bool BlocksAccountWrites(string product) =>
        product?.StartsWith("Probably Stolen Coop ", StringComparison.Ordinal) == true;
}
