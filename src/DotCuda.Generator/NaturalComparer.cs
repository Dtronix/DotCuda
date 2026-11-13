using System.Text.RegularExpressions;

namespace DotCuda.Generator;

/// <summary>Orders strings with embedded numbers numerically: "v9.9" &lt; "v9.14", "12.6" &lt; "12.10".</summary>
sealed partial class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [GeneratedRegex(@"\d+|\D+")] private static partial Regex Chunks();

    public int Compare(string? x, string? y)
    {
        var a = Chunks().Matches(x ?? "");
        var b = Chunks().Matches(y ?? "");
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var (p, q) = (a[i].Value, b[i].Value);
            int c = char.IsDigit(p[0]) && char.IsDigit(q[0])
                ? (p.Length != q.Length ? p.TrimStart('0').Length.CompareTo(q.TrimStart('0').Length) : string.CompareOrdinal(p, q))
                : string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }
}
