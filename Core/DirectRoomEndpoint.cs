using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ProbablyCoop.Core;

internal static class DirectRoomEndpoint
{
    internal static bool ValidHostAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte firstOctet = address.GetAddressBytes()[0];
        return firstOctet is > 0 and < 224;
    }

    internal static bool TryParseIpv4(string text, out IPAddress address) =>
        IPAddress.TryParse(text, out address) && ValidHostAddress(address) &&
        !IPAddress.IsLoopback(address) && address.ToString() == text;

    internal static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    internal static bool TryParseMappedEndpoint(string text, out IPEndPoint endpoint)
    {
        endpoint = null;
        int separator = text.IndexOf(':');
        if (separator < 0 || !TryParseIpv4(text.Substring(0, separator).Trim(), out var address) ||
            !TryParsePort(text.Substring(separator + 1).Trim(), out int port)) return false;
        endpoint = new IPEndPoint(address, port);
        return true;
    }
}
