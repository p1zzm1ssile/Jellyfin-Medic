using System.Reflection;

namespace JellyfinMedic.Services;

public class ChangelogEntry
{
    public string Version { get; set; } = string.Empty;

    public List<string> Lines { get; set; } = new();
}

/// <summary>
/// Reads CHANGELOG.md (embedded in the plugin) so the "what's new" panel can show what changed,
/// without needing the internet. Returns the most recent few versions.
/// </summary>
public static class ChangelogReader
{
    public static List<ChangelogEntry> Read(int max = 5)
    {
        string text = Embedded();
        var entries = new List<ChangelogEntry>();
        ChangelogEntry? current = null;

        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                // "## [1.0.3] – 2026-10-03"
                int open = line.IndexOf('[');
                int close = line.IndexOf(']');
                string version = open >= 0 && close > open ? line[(open + 1)..close] : line[3..].Trim();
                current = new ChangelogEntry { Version = version };
                entries.Add(current);
            }
            else if (current is not null && line.Length > 0 && !line.StartsWith("# ", StringComparison.Ordinal) && !line.StartsWith("[", StringComparison.Ordinal))
            {
                current.Lines.Add(line);
            }
        }

        return entries.Take(max).ToList();
    }

    private static string Embedded()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
        }
        catch
        {
            // Fall through to the built-in note.
        }

        return "## Latest\n- See the full changelog on GitHub.";
    }
}
