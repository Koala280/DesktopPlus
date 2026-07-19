using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Helpers for discovering the machine's LAN address so the companion URL/QR and the
    /// TLS certificate's SAN list point at something the phone can actually reach.
    /// Crucially, this must avoid virtual adapters (Hyper-V / WSL / VPN), whose private
    /// addresses are not reachable from a phone on the real Wi-Fi.
    /// </summary>
    internal static class CompanionNetwork
    {
        private static readonly string[] VirtualMarkers =
        {
            "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "vbox",
            "wsl", "default switch", "loopback", "tap-windows", "tunnel", "vpn"
        };

        /// <summary>All usable IPv4 addresses, physical adapters first, link-local excluded.</summary>
        public static List<IPAddress> GetLocalIPv4Addresses()
        {
            var collected = new List<(IPAddress Address, bool Physical)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    bool physical = !IsVirtual(ni);
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(addr.Address)) continue;
                        if (IsLinkLocal(addr.Address)) continue;
                        collected.Add((addr.Address, physical));
                    }
                }
            }
            catch
            {
                // Best effort only; fall back to loopback elsewhere.
            }

            return collected
                .OrderByDescending(x => x.Physical)
                .Select(x => x.Address)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// The address the phone should actually use. Determined from the OS routing table
        /// (the source address used to reach the internet), which reliably resolves to the
        /// real LAN adapter rather than a Hyper-V/WSL/VPN virtual one.
        /// </summary>
        public static string? GetPrimaryLanAddress()
        {
            var outbound = GetOutboundAddress();
            if (outbound != null && !IsLinkLocal(outbound))
            {
                return outbound.ToString();
            }

            var addresses = GetLocalIPv4Addresses();
            var preferred = addresses.FirstOrDefault(IsPrivate) ?? addresses.FirstOrDefault();
            return preferred?.ToString();
        }

        private static IPAddress? GetOutboundAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // No packets are sent: connecting a UDP socket only triggers a local route
                // lookup, yielding the source IP the OS would use for outbound traffic.
                socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
                return (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsVirtual(NetworkInterface ni)
        {
            string haystack = (ni.Name + " " + ni.Description).ToLowerInvariant();
            return VirtualMarkers.Any(marker => haystack.Contains(marker));
        }

        private static bool IsLinkLocal(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }

        private static bool IsPrivate(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            return false;
        }
    }
}
