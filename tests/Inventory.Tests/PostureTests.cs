using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;

namespace Inventory.Tests;

public class PostureTests
{
    private static InventoryData Scan(Action<SecurityPosture>? change = null)
    {
        var posture = new SecurityPosture
        {
            RdpEnabled = true, RdpNla = true, UacEnabled = true, UacLevel = 2, Smb1Enabled = false, CredentialGuard = true, MemoryIntegrity = true,
            LsaProtection = true, BuiltinAdminEnabled = false, BuiltinAdminPasswordAgeDays = 400, WindowsActivated = true,
        };
        change?.Invoke(posture);
        return new InventoryData
        {
            ComputerName = "PC-01",
            Security = new SecurityStatus { Posture = posture },
            LocalUsers = [new LocalUserItem { Name = "Administrator", Disabled = true, PasswordAgeDays = 400, LastLogonUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) }],
        };
    }

    [Fact]
    public async Task Posture_is_stored_and_changes_are_recorded_while_unread_values_keep_their_previous_state()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var store = new InventoryStore(db);
        var id = (await computers.ImportAsync("pc-01"))[0].Id;

        // Ein Scan ohne und der erste mit diesen Angaben sind die Ausgangslage.
        var first = Scan();
        first.Security!.Posture = null;
        await store.SaveAsync(id, first);
        await store.SaveAsync(id, Scan());
        Assert.Empty(await store.ChangesAsync(id));

        await store.SaveAsync(id, Scan(p =>
        {
            p.RdpNla = false;
            p.Smb1Enabled = true;
            p.BuiltinAdminEnabled = true;
            // Ließ sich diesmal nicht lesen.
            p.CredentialGuard = null;
            p.WindowsActivated = null;
        }));

        var detail = (await computers.GetDetailAsync(id))!;
        Assert.False(detail.RdpNla);
        Assert.True(detail.Smb1Enabled);
        Assert.True(detail.CredentialGuard);
        Assert.True(detail.WindowsActivated);
        Assert.Equal(2, detail.UacLevel);
        var user = Assert.Single(detail.LocalUsers);
        Assert.Equal(400, user.PasswordAgeDays);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), user.LastLogonUtc);

        Assert.Equal(
        [
            "Built-in administrator account|disabled|active",
            "Remote Desktop network level authentication|on|off",
            "SMB 1|off|on",
        ], (await store.ChangesAsync(id)).Select(c => $"{c.Change.Item}|{c.Change.OldValue}|{c.Change.NewValue}").Order());
    }

    [Fact]
    public void Rules_match_posture_and_treat_unknown_as_not_matching()
    {
        static bool Matches(RuleField field, Computer computer, string value = "true", RuleOperator op = RuleOperator.Equals) =>
            CollectionEvaluator.Matches(new CollectionRule { Field = field, Operator = op, Value = value }, computer);

        var risky = new Computer
        {
            Name = "a", RdpEnabled = true, RdpNla = false, UacEnabled = false, Smb1Enabled = true, CredentialGuard = false, LsaProtection = false,
            BuiltinAdminEnabled = true, BuiltinAdminPasswordAgeDays = 400, WindowsActivated = false,
        };
        var unknown = new Computer { Name = "b" };
        foreach (var field in new[] { RuleField.RdpEnabled, RuleField.RdpWithoutNla, RuleField.UacOff, RuleField.Smb1Enabled, RuleField.BuiltinAdminEnabled, RuleField.NotActivated })
        {
            Assert.True(Matches(field, risky), field.ToString());
            Assert.False(Matches(field, unknown), field.ToString());
        }
        Assert.False(Matches(RuleField.CredentialGuard, risky));
        Assert.True(Matches(RuleField.LsaProtection, risky, "false"));
        Assert.True(Matches(RuleField.BuiltinAdminPasswordAgeDays, risky, "365", RuleOperator.GreaterOrEqual));
        Assert.False(Matches(RuleField.BuiltinAdminPasswordAgeDays, unknown, "365", RuleOperator.GreaterOrEqual));
        // Remotedesktop mit NLA ist kein Treffer für "ohne NLA".
        Assert.False(Matches(RuleField.RdpWithoutNla, new Computer { Name = "c", RdpEnabled = true, RdpNla = true }));
    }
}
