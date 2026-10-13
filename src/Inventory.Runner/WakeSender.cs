using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Inventory.Runner
{
    /// <summary>
    /// Weckrelais: schickt Wake-on-LAN-Pakete in die Netze, an die dieser Rechner angeschlossen ist. Der Server bittet
    /// darum, wenn ein ausgeschalteter Rechner in einem Subnetz steht, das seine eigenen Broadcasts nicht erreichen.
    /// </summary>
    internal static class WakeSender
    {
        private const int Port = 9;

        /// <summary>Liest MAC-Adressen aus einer durch Komma getrennten Liste; Einträge, die keine sind, entfallen.</summary>
        public static List<byte[]> ParseMacs(string? list)
        {
            var macs = new List<byte[]>();
            foreach (var entry in (list ?? "").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var hex = entry.Replace(":", "").Replace("-", "").Replace(".", "");
                if (hex.Length != 12)
                    continue;
                var mac = new byte[6];
                var valid = true;
                for (var i = 0; i < 6 && valid; i++)
                    valid = byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mac[i]);
                if (valid)
                    macs.Add(mac);
            }
            return macs;
        }

        /// <summary>Sechsmal FF, danach sechzehnmal die MAC-Adresse.</summary>
        public static byte[] Packet(byte[] mac)
        {
            var packet = new byte[6 + 16 * 6];
            for (var i = 0; i < 6; i++)
                packet[i] = 0xFF;
            for (var i = 0; i < 16; i++)
                Array.Copy(mac, 0, packet, 6 + i * 6, 6);
            return packet;
        }

        /// <summary>Broadcast-Adresse des Subnetzes.</summary>
        public static IPAddress Broadcast(IPAddress address, IPAddress mask)
        {
            var bytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            for (var i = 0; i < bytes.Length && i < maskBytes.Length; i++)
                bytes[i] |= (byte)~maskBytes[i];
            return new IPAddress(bytes);
        }

        /// <summary>Sendet über jede aktive IPv4-Schnittstelle und liefert eine Zusammenfassung für die Ausgabe des Schritts.</summary>
        /// <exception cref="InvalidOperationException">Die Liste enthält keine MAC-Adresse, oder kein Paket ließ sich senden.</exception>
        public static string Send(string? macList)
        {
            var macs = ParseMacs(macList);
            if (macs.Count == 0)
                throw new InvalidOperationException("Keine gültige MAC-Adresse angegeben");

            var sent = 0;
            var interfaces = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                        continue;
                    try
                    {
                        using (var client = new UdpClient(new IPEndPoint(unicast.Address, 0)))
                        {
                            client.EnableBroadcast = true;
                            var targets = new List<IPAddress> { IPAddress.Broadcast };
                            if (unicast.IPv4Mask != null && !unicast.IPv4Mask.Equals(IPAddress.Any))
                                targets.Add(Broadcast(unicast.Address, unicast.IPv4Mask));
                            foreach (var mac in macs)
                            {
                                var packet = Packet(mac);
                                foreach (var target in targets)
                                {
                                    client.Send(packet, packet.Length, new IPEndPoint(target, Port));
                                    sent++;
                                }
                            }
                        }
                        interfaces++;
                    }
                    catch (SocketException)
                    {
                        // Schnittstelle ohne Verbindung: die übrigen bleiben.
                    }
                }
            }
            if (sent == 0)
                throw new InvalidOperationException("Kein Paket gesendet: keine Netzwerkschnittstelle mit IPv4-Adresse");
            return $"{sent} Pakete für {macs.Count} MAC-Adresse(n) über {interfaces} Adresse(n) gesendet";
        }
    }
}
