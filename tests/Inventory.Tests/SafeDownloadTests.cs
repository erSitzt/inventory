using System.Net;
using Inventory.Server.Mcp;

namespace Inventory.Tests;

/// <summary>Downloads für Pakete von Assistenten erreichen keine internen Ziele, außer ein Administrator hat sie erlaubt.</summary>
public class SafeDownloadTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("10.0.1.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("140.82.112.3", true)]
    [InlineData("2606:4700::1111", true)]
    public void Only_public_addresses_are_reachable_by_default(string address, bool allowed) =>
        Assert.Equal(allowed, SafeDownloads.Allowed("example", IPAddress.Parse(address), []));

    [Fact]
    public void Internal_hosts_need_an_entry_by_name_address_or_network()
    {
        var internalServer = IPAddress.Parse("10.0.1.5");
        Assert.True(SafeDownloads.Allowed("nexus.corp.example", internalServer, ["nexus.corp.example"]));
        Assert.True(SafeDownloads.Allowed("other", internalServer, ["10.0.1.5"]));
        Assert.True(SafeDownloads.Allowed("other", internalServer, ["10.0.1.0/24"]));
        Assert.False(SafeDownloads.Allowed("other", internalServer, ["10.0.2.0/24", "nexus.corp.example"]));
        // Loopback und Metadaten bleiben gesperrt, auch wenn jemand sie einträgt.
        Assert.False(SafeDownloads.Allowed("localhost", IPAddress.Loopback, ["localhost", "127.0.0.1"]));
        Assert.False(SafeDownloads.Allowed("meta", IPAddress.Parse("169.254.169.254"), ["169.254.0.0/16"]));
    }

    [Fact]
    public void Logged_urls_lose_their_query_and_credentials() =>
        Assert.Equal("https://artifacts.example:8443/builds/app.msi",
            SafeDownloads.Redact(new Uri("https://user:secret@artifacts.example:8443/builds/app.msi?token=abc&sig=xyz")));

    [Fact]
    public async Task A_download_to_an_internal_address_is_refused_at_connect_time()
    {
        using var client = SafeDownloads.Create(() => []);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/file.msi"));
        Assert.Contains("may not reach", ex.Message);
    }
}
