using Inventory.Core;
using Inventory.Data.Services;
using Inventory.Server.Components.Dialogs;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Inventory.Server.Services;

/// <summary>
/// „Updates installieren“ für bestimmte Rechner: legt bei Bedarf das passende Windows-Update-Paket an und öffnet den
/// Bereitstellungsdialog mit diesen Rechnern. Nur für Administratoren, weil dabei ein Paket entstehen kann.
/// </summary>
public sealed class UpdateInstaller(PackageService packages, IDialogService dialogs, ISnackbar snackbar, Auditor audit, Access access, IStringLocalizer<SharedResource> L)
{
    public async Task OpenAsync(IReadOnlyCollection<int> computerIds, bool securityOnly)
    {
        if (computerIds.Count == 0 || !await access.DemandAdminAsync())
            return;
        try
        {
            var (package, created) = await packages.EnsurePendingUpdatesPackageAsync(securityOnly, approvedBy: await audit.UserNameAsync());
            if (created)
                await audit.LogAsync(L["Package created"], package.Name);
            var parameters = new DialogParameters<DeployDialog> { { d => d.PackageId, package.Id }, { d => d.ComputerIds, computerIds.ToList() } };
            await dialogs.ShowAsync<DeployDialog>(L["Deploy \"{0}\"", package.Name], parameters, new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small });
        }
        catch (InvalidOperationException ex)
        {
            snackbar.Add(ex.Message, Severity.Error);
        }
    }
}
