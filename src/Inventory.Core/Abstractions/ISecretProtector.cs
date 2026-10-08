namespace Inventory.Core.Abstractions;

/// <summary>Ver- und Entschlüsselung von Geheimnissen, die in der Datenbank liegen.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
