using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class CredentialTests
{
    [Fact]
    public async Task Password_is_stored_protected_and_resolved_in_plaintext()
    {
        using var db = new TestDb();
        var service = new CredentialService(db, new ReversingProtector());
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var computer = (await computers.ImportAsync("pc-01"))[0];

        var saved = await service.SaveAsync(null, "Admin", @"CORP\deploy", "geheim", null);

        Assert.True(saved.IsDefault);
        Assert.NotEqual("geheim", saved.ProtectedPassword);
        var resolved = await service.ResolveAsync(computer.Id);
        Assert.Equal(@"CORP\deploy", resolved!.UserName);
        Assert.Equal("geheim", resolved.Password);
    }

    [Fact]
    public async Task Computer_specific_credential_wins_over_default_and_empty_password_keeps_old()
    {
        using var db = new TestDb();
        var service = new CredentialService(db, new ReversingProtector());
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var computer = (await computers.ImportAsync("pc-01"))[0];
        await service.SaveAsync(null, "Standard", @"CORP\deploy", "eins", null);
        var special = await service.SaveAsync(null, "Spezial", @".\admin", "zwei", null);
        await computers.UpdateAsync(computer.Id, null, null, special.Id);

        await service.SaveAsync(special.Id, "Spezial", @".\administrator", "", "geändert");

        var resolved = await service.ResolveAsync(computer.Id);
        Assert.Equal(@".\administrator", resolved!.UserName);
        Assert.Equal("zwei", resolved.Password);

        await service.DeleteAsync(special.Id);
        Assert.Equal("eins", (await service.ResolveAsync(computer.Id))!.Password);
    }

    [Fact]
    public async Task New_credential_requires_password()
    {
        using var db = new TestDb();
        var service = new CredentialService(db, new ReversingProtector());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(null, "Admin", "user", "", null));
    }
}
