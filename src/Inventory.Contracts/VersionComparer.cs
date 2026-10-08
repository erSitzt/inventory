using System;
using System.Linq;

namespace Inventory.Contracts
{
    public static class VersionComparer
    {
        private static readonly char[] Separators = { '.', '-', '_', ' ', ',' };

        /// <summary>Vergleicht Versionen abschnittsweise numerisch (1.10 &gt; 1.9); fehlende Abschnitte zählen als 0, Nicht-Ziffern werden ignoriert.</summary>
        public static int Compare(string? left, string? right)
        {
            var a = Parts(left);
            var b = Parts(right);
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var x = i < a.Length ? a[i] : 0;
                var y = i < b.Length ? b[i] : 0;
                if (x != y)
                    return x < y ? -1 : 1;
            }
            return 0;
        }

        private static long[] Parts(string? version) =>
            (version ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => new string(p.Where(c => c >= '0' && c <= '9').ToArray()))
                .Select(digits => long.TryParse(digits, out var n) ? n : 0)
                .ToArray();
    }
}
