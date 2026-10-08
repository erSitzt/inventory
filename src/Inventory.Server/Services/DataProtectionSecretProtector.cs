using Inventory.Core.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Inventory.Server.Services;

/// <summary>Verschlüsselt über ASP.NET Core Data Protection; die Schlüssel sind unter Windows per DPAPI (Maschine) geschützt.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Inventory.Credentials.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
