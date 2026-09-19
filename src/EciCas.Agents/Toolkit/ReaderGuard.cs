using System.Net;
using System.Net.Sockets;

namespace EciCas.Agents.Toolkit;

/// <summary>
/// The rule that keeps "read that page" from becoming "read anything this
/// machine can reach". Enforced where the socket is opened, on the addresses
/// the connection will actually use, so a public name that resolves to a
/// private address, a DNS rebind, and a redirect to http://localhost all hit
/// the same wall. A check on the URL string alone would miss every one of them.
/// </summary>
public static class ReaderGuard
{
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or >= 224 => true,
                100 => bytes[1] is >= 64 and <= 127,
                169 => bytes[1] == 254,
                172 => bytes[1] is >= 16 and <= 31,
                192 => bytes[1] == 168 || (bytes[1] == 0 && bytes[2] == 0),
                _ => false,
            };
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (bytes[0] & 0xFE) == 0xFC;
    }

    /// <summary>Only http and https, and never a bare private address or localhost typed into the URL.</summary>
    public static bool IsAllowedUrl(Uri uri) =>
        uri.Scheme is "http" or "https"
        && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !(IPAddress.TryParse(uri.Host, out var literal) && IsBlocked(literal));

    /// <summary>A handler whose every connection, redirects included, is checked against <see cref="IsBlocked"/>.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        MaxAutomaticRedirections = 3,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var resolved = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var allowed = resolved.Where(a => !IsBlocked(a)).ToArray();
            if (allowed.Length == 0)
            {
                throw new HttpRequestException($"'{context.DnsEndPoint.Host}' resolves to a private address; not reading it.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
