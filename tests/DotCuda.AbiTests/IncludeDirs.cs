namespace DotCuda.AbiTests;

/// <summary>Resolves config include directories the way the generator does: environment variables, then '*' segments
/// matched to the highest version-sorted directory. Returns null when the directory doesn't exist here.</summary>
static class IncludeDirs
{
    public static string? Resolve(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.Length == 0 || expanded.Contains('%')) return null;
        var parts = expanded.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        bool drive = parts[0].EndsWith(':');
        string? current = drive ? parts[0] + Path.DirectorySeparatorChar : Path.GetPathRoot(expanded) is { Length: > 0 } r ? r : ".";
        foreach (var part in parts.Skip(drive ? 1 : 0))
        {
            if (current is null) break;
            current = part.Contains('*')
                ? (Directory.Exists(current) ? Directory.GetDirectories(current, part).OrderBy(VersionKey).LastOrDefault() : null)
                : Path.Combine(current, part);
        }
        return current is not null && Directory.Exists(current) ? current : null;
    }

    /// <summary>"v9.14" sorts after "v9.9": compare the numbers in the name numerically.</summary>
    static string VersionKey(string path) =>
        System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(path), @"\d+", m => m.Value.PadLeft(10, '0'));
}
