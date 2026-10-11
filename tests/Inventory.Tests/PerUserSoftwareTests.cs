using Inventory.Contracts;
using Inventory.Core;
using Inventory.Core.Model;

namespace Inventory.Tests;

public class PerUserSoftwareTests
{
    private const string Anna = "S-1-5-21-1-2-3-1001";
    private const string Ben = "S-1-5-21-1-2-3-1002";

    private static ComputerSoftware User(string name, string? sid, string version = "1.0") => new() { Name = name, Version = version, PerUser = true, UserSid = sid };

    private static InventoryData Scan(params (string Sid, bool Loaded)[] profiles) => new()
    {
        Software = [new SoftwareItem { Name = "7-Zip", Version = "24.08" }],
        UserProfiles = [.. profiles.Select(p => new UserProfileState { Sid = p.Sid, Loaded = p.Loaded })],
    };

    [Fact]
    public void Programs_of_a_logged_off_user_are_kept_and_marked()
    {
        var current = Scan((Anna, false));
        Assert.Equal(1, PerUserSoftware.CarryOver([User("VS Code", Anna), new ComputerSoftware { Name = "Altes Programm", Version = "1" }], current));
        var kept = current.Software.Single(s => s.Name == "VS Code");
        Assert.True(kept is { PerUser: true, CarriedOver: true, UserSid: Anna });
        // Für alle Benutzer installierte Programme sind immer lesbar und werden nie übernommen.
        Assert.DoesNotContain(current.Software, s => s.Name == "Altes Programm");
    }

    [Fact]
    public void A_readable_profile_is_authoritative_and_a_deleted_profile_takes_its_programs_along()
    {
        var current = Scan((Anna, true));
        current.Software.Add(new SoftwareItem { Name = "Zoom", Version = "6", PerUser = true, UserSid = Anna });
        Assert.Equal(0, PerUserSoftware.CarryOver([User("VS Code", Anna), User("Teams", Ben)], current));
        Assert.Equal(["7-Zip", "Zoom"], current.Software.Select(s => s.Name));
    }

    [Fact]
    public void Only_the_unreadable_profiles_are_carried_over_when_several_users_exist()
    {
        var current = Scan((Anna, true), (Ben, false));
        current.Software.Add(new SoftwareItem { Name = "VS Code", Version = "1.0", PerUser = true, UserSid = Anna });
        Assert.Equal(1, PerUserSoftware.CarryOver([User("VS Code", Ben), User("Teams", Ben), User("Zoom", Anna)], current));
        // VS Code ist schon über Annas Profil erfasst und erscheint nicht doppelt; Zoom hat Anna wirklich entfernt.
        Assert.Equal(["7-Zip", "Teams", "VS Code"], current.Software.Select(s => s.Name).Order());
    }

    [Fact]
    public void Entries_without_a_user_are_kept_only_while_no_profile_is_readable_and_old_runners_change_nothing()
    {
        var nobody = Scan((Anna, false));
        Assert.Equal(1, PerUserSoftware.CarryOver([User("VS Code", null)], nobody));
        var someone = Scan((Anna, true));
        Assert.Equal(0, PerUserSoftware.CarryOver([User("VS Code", null)], someone));
        var oldRunner = new InventoryData();
        Assert.Equal(0, PerUserSoftware.CarryOver([User("VS Code", Anna)], oldRunner));
        Assert.Empty(oldRunner.Software);
    }
}
