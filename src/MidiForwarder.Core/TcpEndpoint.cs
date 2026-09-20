using System.Net;

namespace MidiForwarder.Core;

public static class TcpEndpoint
{
    public static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            || uri.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("Enter a TCP address such as tcp://192.168.2.155:5180.");
        }

        return uri;
    }

    public static IPAddress ParseListenAddress(string host) => host switch
    {
        "0.0.0.0" or "*" or "+" => IPAddress.Any,
        "::" => IPAddress.IPv6Any,
        _ when IPAddress.TryParse(host, out IPAddress? address) => address,
        _ => throw new InvalidOperationException("The server listen address must use an IP address, such as tcp://0.0.0.0:5180."),
    };
}
