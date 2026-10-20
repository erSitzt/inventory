using System.Globalization;
using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>Wertet die Regeln dynamischer Sammlungen gegen einen Rechner (mit geladener Software) aus.</summary>
public static class CollectionEvaluator
{
    /// <param name="context">Die übrigen Sammlungen, für Regeln "Mitglied der Sammlung"; ohne sie passt eine solche Regel auf nichts.</param>
    public static bool Matches(Collection collection, Computer computer, RuleContext? context = null) => Matches(collection, computer, context, 0);

    internal static bool Matches(Collection collection, Computer computer, RuleContext? context, int depth)
    {
        if (collection.Rules.Count == 0)
            return false;
        return collection.MatchAll
            ? collection.Rules.All(r => Matches(r, computer, context, depth))
            : collection.Rules.Any(r => Matches(r, computer, context, depth));
    }

    public static bool Matches(CollectionRule rule, Computer computer, RuleContext? context = null, int depth = 0) => rule.Field switch
    {
        RuleField.ComputerName => Text(rule, computer.Name),
        RuleField.NameMismatch => Flag(rule, computer.NameMismatch),
        RuleField.MissingInDirectory => Flag(rule, computer.MissingInDirectory),
        RuleField.DirectoryInactive => Flag(rule, computer.DirectoryInactive),
        RuleField.Online => Flag(rule, computer.IsOnline),
        RuleField.Scanned => Flag(rule, computer.LastScanUtc is not null),
        RuleField.OsName => Text(rule, computer.OsName),
        RuleField.OsVersion => Text(rule, computer.OsVersion),
        RuleField.OsRelease => Text(rule, computer.OsRelease),
        RuleField.Manufacturer => Text(rule, computer.Manufacturer),
        RuleField.Model => Text(rule, computer.Model),
        RuleField.LoggedOnUser => Text(rule, computer.LoggedOnUser),
        RuleField.MemoryGb => Text(rule, computer.MemoryBytes is { } bytes
            ? Math.Round(bytes / 1024d / 1024 / 1024).ToString(CultureInfo.InvariantCulture) : null),
        RuleField.Software => Software(rule, computer),
        RuleField.FirewallOff => Flag(rule, computer.FirewallOff),
        RuleField.AntivirusProblem => Flag(rule, computer.AntivirusProblem),
        RuleField.RebootRequired => Flag(rule, computer.UpdateRebootRequired == true),
        RuleField.PendingUpdates => Text(rule, computer.UpdatePendingCount?.ToString(CultureInfo.InvariantCulture)),
        RuleField.DaysSinceUpdate => Text(rule, computer.UpdateLastInstallUtc is { } installed
            ? ((int)(DateTime.UtcNow - installed).TotalDays).ToString(CultureInfo.InvariantCulture) : null),
        RuleField.RdpEnabled => Known(rule, computer.RdpEnabled),
        // Ohne Remotedesktop stellt sich die Frage nach NLA nicht; mit Remotedesktop muss der NLA-Wert bekannt sein.
        RuleField.RdpWithoutNla => Known(rule, computer.RdpEnabled == false ? false : computer.RdpEnabled is null ? null : !computer.RdpNla),
        RuleField.UacOff => Known(rule, !computer.UacEnabled),
        RuleField.Smb1Enabled => Known(rule, computer.Smb1Enabled),
        RuleField.CredentialGuard => Known(rule, computer.CredentialGuard),
        RuleField.MemoryIntegrity => Known(rule, computer.MemoryIntegrity),
        RuleField.LsaProtection => Known(rule, computer.LsaProtection),
        RuleField.BuiltinAdminEnabled => Known(rule, computer.BuiltinAdminEnabled),
        RuleField.BuiltinAdminPasswordAgeDays => Text(rule, computer.BuiltinAdminPasswordAgeDays?.ToString(CultureInfo.InvariantCulture)),
        RuleField.NotActivated => Known(rule, !computer.WindowsActivated),
        RuleField.PendingSecurityUpdates => Text(rule, computer.UpdatePendingSecurityCount?.ToString(CultureInfo.InvariantCulture)),
        RuleField.Hotfix => AnyOrNone(rule, computer.Hotfixes.Select(h => h.HotfixId)),
        RuleField.PendingUpdate => AnyOrNone(rule, computer.PendingUpdates.SelectMany(u => new[] { u.Title }.Concat((u.KbIds ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries)))),
        RuleField.DirectoryOu => AnyOrNone(rule, new[] { computer.DirectoryOu, computer.DirectoryPath }.Where(v => v is not null).Select(v => v!)),
        RuleField.DirectoryGroup => AnyOrNone(rule, computer.DirectoryGroupNames),
        RuleField.DaysSinceDirectoryLogon => Text(rule, computer.DirectoryLastLogonUtc is { } logon
            ? ((int)(DateTime.UtcNow - logon).TotalDays).ToString(CultureInfo.InvariantCulture) : null),
        RuleField.DirectoryDescription => Text(rule, computer.DirectoryDescription),
        RuleField.Tag => AnyOrNone(rule, computer.TagList),
        RuleField.Location => Text(rule, computer.Location),
        RuleField.Owner => Text(rule, computer.Owner),
        RuleField.CostCenter => Text(rule, computer.CostCenter),
        RuleField.InventoryNumber => Text(rule, computer.InventoryNumber),
        RuleField.PrimaryUser => Text(rule, computer.PrimaryUser),
        RuleField.WarrantyDaysLeft => Number(rule, computer.WarrantyDaysLeft(DateOnly.FromDateTime(DateTime.Now))),
        RuleField.Domain => Text(rule, computer.Domain),
        RuleField.SerialNumber => Text(rule, computer.SerialNumber),
        RuleField.CpuName => Text(rule, computer.CpuName),
        RuleField.OsArchitecture => Text(rule, computer.OsArchitecture),
        RuleField.IpAddress => Text(rule, computer.IpAddress),
        RuleField.FirmwareType => Text(rule, computer.FirmwareType),
        RuleField.Gpu => Text(rule, computer.Gpu),
        RuleField.DeviceType => computer.ChassisType is not null && Text(rule, Chassis.Kind(computer.ChassisType, computer.Model).ToString()),
        RuleField.DiskFreeGb => Number(rule, computer.Disks.FirstOrDefault(d => d.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase)) is { } system
            ? (int)(system.FreeBytes / (1L << 30)) : null),
        RuleField.BatteryHealthPercent => Number(rule, computer.BatteryHealthPercent),
        RuleField.DaysSinceBoot => Number(rule, computer.LastBootUtc is { } boot ? (int)(DateTime.UtcNow - boot).TotalDays : null),
        RuleField.ScanAgeDays => Number(rule, computer.LastScanUtc is { } scan ? (int)(DateTime.UtcNow - scan).TotalDays : null),
        RuleField.AgentPresent => Flag(rule, computer.AgentKeyHash is not null),
        RuleField.SecureBoot => Known(rule, computer.SecureBoot),
        RuleField.TpmReady => Known(rule, computer.TpmPresent == false ? false : computer.TpmReady),
        RuleField.SystemDriveProtected => Known(rule, computer.SystemDriveProtected),
        // Ein gemeldetes Problem zählt immer; "kein Problem" gilt nur, wenn jeder Datenträger seinen Zustand genannt hat.
        RuleField.DiskHealthProblem => Known(rule, computer.PhysicalDisks.Any(d => d.Health is not (null or "Healthy")) ? true
            : computer.PhysicalDisks.Count == 0 || computer.PhysicalDisks.Any(d => d.Health is null) ? null : false),
        RuleField.Service => Service(rule, computer),
        RuleField.InCollection => context is not null && context.InCollection(rule, computer, depth),
        RuleField.LocalAdmin => AnyOrNone(rule, computer.LocalAdmins.Select(a => a.Name)),
        RuleField.App => AnyOrNone(rule, computer.Apps.SelectMany(a => a.DisplayName is null ? [a.Name] : new[] { a.Name, a.DisplayName })),
        _ => false,
    };

    private static bool Software(CollectionRule rule, Computer computer)
    {
        // Verneinte Operatoren bedeuten "kein Programm passt", nicht "irgendein anderes Programm ist installiert".
        var negated = rule.Operator is RuleOperator.NotContains or RuleOperator.NotEquals;
        var positive = new CollectionRule
        {
            Operator = rule.Operator switch
            {
                RuleOperator.NotContains => RuleOperator.Contains,
                RuleOperator.NotEquals => RuleOperator.Equals,
                var other => other,
            },
            Value = rule.Value,
        };
        var found = computer.Software.Any(s =>
            Text(positive, s.Name)
            && (string.IsNullOrWhiteSpace(rule.Value2) || CompareVersions(s.Version, rule.Value2) < 0));
        return negated ? !found : found;
    }

    /// <summary>Dienst mit diesem Namen (und optional diesem Zustand) vorhanden; verneint: keiner.</summary>
    private static bool Service(CollectionRule rule, Computer computer)
    {
        var negated = rule.Operator is RuleOperator.NotContains or RuleOperator.NotEquals;
        var positive = new CollectionRule { Operator = negated ? RuleOperator.Equals : rule.Operator, Value = rule.Value };
        // Verneint heißt "kein solcher Dienst vorhanden"; ein Zustand hat dabei keine Bedeutung.
        var state = negated || string.IsNullOrWhiteSpace(rule.Value2) ? null : rule.Value2.Trim();
        var found = computer.Services.Any(s =>
            (Text(positive, s.Name) || Text(positive, s.DisplayName))
            && (state is null || string.Equals(s.State, state, StringComparison.OrdinalIgnoreCase)));
        return negated ? !found : found;
    }

    /// <summary>Positive Operatoren: mindestens ein Eintrag passt. Verneinte: kein Eintrag passt.</summary>
    private static bool AnyOrNone(CollectionRule rule, IEnumerable<string> values)
    {
        var negated = rule.Operator is RuleOperator.NotContains or RuleOperator.NotEquals;
        var positive = new CollectionRule
        {
            Operator = rule.Operator switch { RuleOperator.NotContains => RuleOperator.Contains, RuleOperator.NotEquals => RuleOperator.Equals, var other => other },
            Value = rule.Value,
        };
        var found = values.Any(v => Text(positive, v));
        return negated ? !found : found;
    }

    /// <summary>Ein nicht ermittelter Wert passt auf keine Regel, weder auf "ja" noch auf "nein".</summary>
    private static bool Known(CollectionRule rule, bool? actual) => actual is { } value && Flag(rule, value);

    /// <summary>Zahlenvergleich, der auch mit negativen Werten umgehen kann; ohne Wert passt nichts.</summary>
    private static bool Number(CollectionRule rule, int? actual)
    {
        if (actual is not { } value || !int.TryParse(rule.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var expected))
            return false;
        return rule.Operator switch
        {
            RuleOperator.LessThan => value < expected,
            RuleOperator.GreaterOrEqual => value >= expected,
            RuleOperator.Equals => value == expected,
            RuleOperator.NotEquals => value != expected,
            _ => false,
        };
    }

    private static bool Flag(CollectionRule rule, bool actual)
    {
        var expected = rule.Value.Trim().ToLowerInvariant() is "true" or "ja" or "1";
        return rule.Operator == RuleOperator.NotEquals ? actual != expected : actual == expected;
    }

    private static bool Text(CollectionRule rule, string? actual)
    {
        var value = rule.Value.Trim();
        actual ??= "";
        return rule.Operator switch
        {
            RuleOperator.Contains => actual.Contains(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.NotContains => !actual.Contains(value, StringComparison.OrdinalIgnoreCase),
            // Ein * im Wert steht für beliebig viele Zeichen: "PC-*-01" oder "*laptop*".
            RuleOperator.Equals => value.Contains('*') ? Wildcard(value, actual) : actual.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.NotEquals => !(value.Contains('*') ? Wildcard(value, actual) : actual.Equals(value, StringComparison.OrdinalIgnoreCase)),
            RuleOperator.StartsWith => actual.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            // Ohne Wert gibt es nichts zu vergleichen: ein nicht gescannter Rechner ist weder "kleiner" noch "größer".
            RuleOperator.LessThan => actual.Length > 0 && CompareVersions(actual, value) < 0,
            RuleOperator.GreaterOrEqual => actual.Length > 0 && CompareVersions(actual, value) >= 0,
            _ => false,
        };
    }

    private static bool Wildcard(string pattern, string actual) =>
        System.Text.RegularExpressions.Regex.IsMatch(actual, "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Child-Tabellen des Rechners, die ein Feld zum Auswerten braucht.</summary>
    public static IEnumerable<string> RequiredData(RuleField field) => field switch
    {
        RuleField.Software => [nameof(Computer.Software)],
        RuleField.LocalAdmin => [nameof(Computer.LocalAdmins)],
        RuleField.App => [nameof(Computer.Apps)],
        RuleField.Hotfix => [nameof(Computer.Hotfixes)],
        RuleField.PendingUpdate => [nameof(Computer.PendingUpdates)],
        RuleField.Service => [nameof(Computer.Services)],
        RuleField.DiskFreeGb => [nameof(Computer.Disks)],
        RuleField.DiskHealthProblem => [nameof(Computer.PhysicalDisks)],
        _ => [],
    };

    public static int CompareVersions(string? left, string? right) => VersionComparer.Compare(left, right);

}

/// <summary>Alle Sammlungen, damit eine Regel auf die Mitgliedschaft in einer anderen Sammlung prüfen kann.</summary>
/// <param name="collections">Mit geladenen Mitgliedern (für statische Sammlungen).</param>
public sealed class RuleContext(IEnumerable<Collection> collections)
{
    /// <summary>So tief dürfen Sammlungen ineinander verweisen; das begrenzt auch den Schaden eines Kreises, den das Speichern nicht verhindert hat.</summary>
    public const int MaxDepth = 3;

    private readonly Dictionary<int, Collection> _byId = collections.ToDictionary(c => c.Id);
    private readonly Dictionary<int, HashSet<int>> _staticMembers = [];

    public Collection? Find(int id) => _byId.GetValueOrDefault(id);

    internal bool InCollection(CollectionRule rule, Computer computer, int depth)
    {
        var negated = rule.Operator is RuleOperator.NotEquals or RuleOperator.NotContains;
        // Eine gelöschte oder zu tief verschachtelte Sammlung hat keine Mitglieder; auch "nicht Mitglied" soll dann nicht alle treffen.
        if (!int.TryParse(rule.Value.Trim(), out var id) || !_byId.TryGetValue(id, out var target) || (target.IsDynamic && depth >= MaxDepth))
            return false;
        bool member;
        if (target.IsDynamic)
        {
            member = CollectionEvaluator.Matches(target, computer, this, depth + 1);
        }
        else
        {
            if (!_staticMembers.TryGetValue(id, out var members))
                _staticMembers[id] = members = target.Members.Select(m => m.Id).ToHashSet();
            member = members.Contains(computer.Id);
        }
        return negated ? !member : member;
    }

    /// <summary>Kennungen der Sammlungen, auf die diese Sammlung direkt verweist.</summary>
    public static IEnumerable<int> References(Collection collection) =>
        collection.Rules.Where(r => r.Field == RuleField.InCollection).Select(r => int.TryParse(r.Value.Trim(), out var id) ? id : 0).Where(id => id > 0).Distinct();

    /// <summary>Die Sammlung selbst und alle, auf die sie direkt oder über andere verweist.</summary>
    public List<Collection> Closure(Collection collection)
    {
        var result = new Dictionary<int, Collection> { [collection.Id] = collection };
        var pending = new Queue<Collection>([collection]);
        while (pending.Count > 0)
        {
            foreach (var id in References(pending.Dequeue()))
            {
                if (_byId.TryGetValue(id, out var next) && result.TryAdd(id, next))
                    pending.Enqueue(next);
            }
        }
        return [.. result.Values];
    }

    /// <summary>Länge der längsten Verweiskette ab dieser Sammlung; -1, wenn sie im Kreis führt.</summary>
    public int Depth(Collection collection) => Depth(collection, []);

    private int Depth(Collection collection, HashSet<int> path)
    {
        if (!path.Add(collection.Id))
            return -1;
        var deepest = 0;
        foreach (var id in References(collection))
        {
            if (!_byId.TryGetValue(id, out var next))
                continue;
            var below = Depth(next, path);
            if (below < 0)
                return -1;
            deepest = Math.Max(deepest, below + 1);
        }
        path.Remove(collection.Id);
        return deepest;
    }
}
