using System.Globalization;
using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>Wertet die Regeln dynamischer Sammlungen gegen einen Rechner (mit geladener Software) aus.</summary>
public static class CollectionEvaluator
{
    public static bool Matches(Collection collection, Computer computer)
    {
        if (collection.Rules.Count == 0)
            return false;
        return collection.MatchAll
            ? collection.Rules.All(r => Matches(r, computer))
            : collection.Rules.Any(r => Matches(r, computer));
    }

    public static bool Matches(CollectionRule rule, Computer computer) => rule.Field switch
    {
        RuleField.ComputerName => Text(rule, computer.Name),
        RuleField.NameMismatch => Flag(rule, computer.NameMismatch),
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
        RuleField.LocalAdmin => AnyOrNone(rule, computer.LocalAdmins.Select(a => a.Name)),
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
            RuleOperator.Equals => actual.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.NotEquals => !actual.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => actual.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            // Ohne Wert gibt es nichts zu vergleichen: ein nicht gescannter Rechner ist weder "kleiner" noch "größer".
            RuleOperator.LessThan => actual.Length > 0 && CompareVersions(actual, value) < 0,
            RuleOperator.GreaterOrEqual => actual.Length > 0 && CompareVersions(actual, value) >= 0,
            _ => false,
        };
    }

    public static int CompareVersions(string? left, string? right) => VersionComparer.Compare(left, right);
}
