namespace Inventory.Server.Components.Shared;

public enum RemoteAccessKind
{
    /// <summary>Windows-Remoteunterstützung anbieten (msra /offerRA).</summary>
    Assistance,
    /// <summary>RDP-Sitzung des angemeldeten Benutzers spiegeln.</summary>
    Shadow,
}
