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
    public async Task Several_computers_get_the_same_credentials_and_go_back_to_the_default()
    {
        using var db = new TestDb();
        var service = new CredentialService(db, new ReversingProtector());
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var imported = await computers.ImportAsync("dmz-02\ndmz-01\npc-03");
        await service.SaveAsync(null, "Standard", @"CORP\deploy", "eins", null);
        var dmz = await service.SaveAsync(null, "DMZ", @".\admin", "zwei", null);
        var ids = imported.Where(c => c.Name.StartsWith("dmz", StringComparison.Ordinal)).Select(c => c.Id).ToList();
        async Task<string[]> PasswordsAsync()
        {
            var passwords = new List<string>();
            foreach (var id in ids)
                passwords.Add((await service.ResolveAsync(id))!.Password);
            return [.. passwords];
        }

        Assert.Equal(["dmz-01", "dmz-02"], await computers.SetCredentialAsync(ids, dmz.Id));
        Assert.Equal(["zwei", "zwei"], await PasswordsAsync());
        // Die übrigen Rechner bleiben beim Standard.
        Assert.Equal("eins", (await service.ResolveAsync(imported.Single(c => c.Name == "pc-03").Id))!.Password);
        // Wer die Zugangsdaten schon hat, gilt nicht als geändert (Meldung und Protokoll).
        var pc03 = imported.Single(c => c.Name == "pc-03").Id;
        Assert.Equal(["pc-03"], await computers.SetCredentialAsync([.. ids, pc03], dmz.Id));
        Assert.Empty(await computers.SetCredentialAsync([.. ids, pc03], dmz.Id));

        await computers.SetCredentialAsync(ids, null);
        Assert.Equal(["eins", "eins"], await PasswordsAsync());

        // Zwischendurch gelöschte Zugangsdaten: nichts wird geändert.
        await service.DeleteAsync(dmz.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => computers.SetCredentialAsync(ids, dmz.Id));
        Assert.Equal(["eins", "eins"], await PasswordsAsync());
    }

    [Fact]
    public async Task New_credential_requires_password()
    {
        using var db = new TestDb();
        var service = new CredentialService(db, new ReversingProtector());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(null, "Admin", "user", "", null));
    }

    [Fact]
    public async Task The_default_can_only_be_deleted_as_the_last_and_the_directory_setting_follows()
    {
        using var db = new TestDb();
        var service = new CredentialService(db, new ReversingProtector());
        var settings = new SettingsService(db);
        var first = await service.SaveAsync(null, "Standard", @"CORP\a", "x", null);
        var second = await service.SaveAsync(null, "AD", @"CORP\b", "y", null);
        await settings.SetAsync(Inventory.Core.Model.SettingKeys.AdSyncCredentialId, second.Id.ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(first.Id));
        await service.DeleteAsync(second.Id);
        // Die Synchronisation zeigt nicht länger auf Zugangsdaten, die es nicht mehr gibt.
        Assert.Null(await settings.GetAsync(Inventory.Core.Model.SettingKeys.AdSyncCredentialId));
        await service.DeleteAsync(first.Id);
        Assert.Empty(await service.ListAsync());
    }
}
