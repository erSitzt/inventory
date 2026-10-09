using Inventory.Data;
using Inventory.Data.Services;
using Inventory.Remoting;


namespace Inventory.Tests;

public class AuditLinkTests
{
    [Fact]
    public async Task Names_in_audit_details_become_links_and_deployment_numbers_only_for_deployments()
    {
        using var db = new TestDb();
        var repo = Directory.CreateTempSubdirectory("inventory-audit-").FullName;
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var packages = new PackageService(db, new PackageRepository(repo));
        var collections = new CollectionService(db);
        var schedules = new ScheduleService(db, collections, null!, null!, TimeProvider.System);
        var pc = (await computers.ImportAsync("pc-01.corp.local pc-010"))[0];
        var zip = await packages.SaveAsync(null, "7-Zip", "26.04", null);
        var map = await AuditLinkMap.LoadAsync(computers, packages, collections, schedules);

        var segments = map.Segments("#5 7-Zip 26.04 on pc-01, PC-010", "Deployment started");
        Assert.Equal(["#5|deployments/5", " ", "7-Zip|packages/" + zip.Id, " 26.04 on ", "pc-01|computers/" + pc.Id, ", ", "PC-010|computers/" + (pc.Id + 1)],
            segments.Select(s => s.Href is null ? s.Text : $"{s.Text}|{s.Href}"));

        // Ohne Deployment-Bezug bleibt "#5" Text; Namen mitten in Wörtern werden nicht verlinkt.
        Assert.Equal(["Token #5 for xpc-01y"], map.Segments("Token #5 for xpc-01y", "API token created").Select(s => s.Text));
        Directory.Delete(repo, recursive: true);
    }
}
