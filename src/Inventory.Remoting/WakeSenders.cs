using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Inventory.Core;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Schickt Magic Packets per UDP, zusätzlich als Broadcast über jede eigene IPv4-Schnittstelle.</summary>
public sealed class UdpWakeSender : IWakeSender
{
    public async Task<int> SendAsync(byte[] packet, IReadOnlyCollection<IPAddress> addresses, CancellationToken ct = default)
    {
        var sent = 0;
        // Der Broadcast 255.255.255.255 verlässt den Server nur über eine Schnittstelle; deshalb je Schnittstelle gebunden senden.
        foreach (var local in LocalAddresses())
        {
            try
            {
                using var bound = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                await bound.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, WakeOnLan.Port), ct);
                sent++;
            }
            catch (SocketException)
            {
                // Schnittstelle ohne Verbindung: die übrigen Wege bleiben.
            }
        }

        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        foreach (var address in addresses)
        {
            try
            {
                await client.SendAsync(packet, new IPEndPoint(address, WakeOnLan.Port), ct);
                sent++;
            }
            catch (SocketException)
            {
                // Kein Weg in dieses Netz.
            }
        }
        return sent;
    }

    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}

/// <summary>Sendet nichts und merkt sich, was gesendet worden wäre; für den Simulationsmodus und Tests.</summary>
public sealed class SimulatedWakeSender : IWakeSender
{
    public ConcurrentQueue<(byte[] Packet, IReadOnlyCollection<IPAddress> Addresses)> Sent { get; } = new();

    public Task<int> SendAsync(byte[] packet, IReadOnlyCollection<IPAddress> addresses, CancellationToken ct = default)
    {
        Sent.Enqueue((packet, addresses));
        while (Sent.Count > 100)
            Sent.TryDequeue(out _);
        return Task.FromResult(addresses.Count + 1);
    }
}
