using Microsoft.Extensions.Localization;

namespace Inventory.Core;

/// <summary>
/// Localizer ohne Übersetzungen: liefert den englischen Quelltext. Dient als Ersatz, wenn ein Dienst ohne
/// Dependency Injection entsteht (Tests, Werkzeuge); im Server liefert DI den echten <see cref="IStringLocalizer{T}"/>.
/// </summary>
public sealed class PassthroughLocalizer : IStringLocalizer<SharedResource>
{
    public static readonly PassthroughLocalizer Instance = new();

    public LocalizedString this[string name] => new(name, name, resourceNotFound: true);

    public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(System.Globalization.CultureInfo.CurrentCulture, name, arguments), resourceNotFound: true);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
