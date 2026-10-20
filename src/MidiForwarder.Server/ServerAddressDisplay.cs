using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MidiForwarder.Core;

namespace MidiForwarder.Server;

internal static class ServerAddressDisplay
{
    internal static string GetText(string listenUrl)
    {
        try
        {
            Uri endpoint = TcpEndpoint.Parse(listenUrl);
            IPAddress bindAddress = TcpEndpoint.ParseListenAddress(endpoint.Host);
            if (!bindAddress.Equals(IPAddress.Any) && !bindAddress.Equals(IPAddress.IPv6Any))
            {
                string address = FormatAddress(bindAddress, endpoint.Port);
                return IPAddress.IsLoopback(bindAddress) ? $"{address} (this computer only)" : address;
            }

            string[] addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == bindAddress.AddressFamily
                    && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal)
                .Select(address => FormatAddress(address, endpoint.Port))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return addresses.Length == 0 ? "No active network address" : string.Join(Environment.NewLine, addresses);
        }
        catch (NetworkInformationException)
        {
            return "Network addresses unavailable";
        }
        catch (InvalidOperationException)
        {
            return "Save a valid listen address in Settings";
        }
    }

    private static string FormatAddress(IPAddress address, int port) => address.AddressFamily == AddressFamily.InterNetworkV6
        ? $"tcp://[{address}]:{port}" : $"tcp://{address}:{port}";
}
