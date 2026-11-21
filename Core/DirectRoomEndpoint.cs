using System;
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
        return firstOctet is > 0 and < 224 && (firstOctet != 127 || address.Equals(IPAddress.Loopback));
    }

    internal static bool TryParseIpv4(string text, out IPAddress address) =>
        IPAddress.TryParse(text, out address) && ValidHostAddress(address) &&
        address.ToString() == text;

    // PSL has no advertised address. A PSI room can listen on loopback for a
    // local UDP forwarder and still uses the regular network-room transport.
    internal static bool TryParseCode(string code, out IPAddress advertised, out int port, out Guid token)
    {
        advertised = null;
        port = 0;
        token = Guid.Empty;
        if (code == null || code.Length > 64) return false;
        string[] parts = code.Split('-');
        if (parts.Length == 3 && parts[0] == "PSL")
            return TryParsePort(parts[1], out port) && Guid.TryParseExact(parts[2], "N", out token) && token != Guid.Empty;
        return parts.Length == 4 && parts[0] == "PSI" && TryParseIpv4(parts[1], out advertised) &&
            TryParsePort(parts[2], out port) && Guid.TryParseExact(parts[3], "N", out token) && token != Guid.Empty;
    }

    internal static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    internal static bool TryParseMappedEndpoint(string text, out IPEndPoint endpoint)
    {
        endpoint = null;
        int separator = text.IndexOf(':');
        if (separator < 0 || !TryParseIpv4(text.Substring(0, separator).Trim(), out var address) || IPAddress.IsLoopback(address) ||
            !TryParsePort(text.Substring(separator + 1).Trim(), out int port)) return false;
        endpoint = new IPEndPoint(address, port);
        return true;
    }
}
