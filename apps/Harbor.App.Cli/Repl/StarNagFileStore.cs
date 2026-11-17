// StarNagFileStore.cs — issue #1490: Infrastructure half of the star reminder.
// The CellForge (Presentation) card depends only on a save delegate; the file
// bytes live here, in the composition-root assembly, so the renderer stays
// free of filesystem I/O (PresentationCapabilityRules) and AOT-reflection-free.

using Harbor.Abstractions.Filesystem;
using Harbor.Tui.CellForge.StarNag;

namespace Harbor.App.Cli.Repl;

/// <summary>File-backed persistence for the star-nag launch counter.</summary>
internal static class StarNagFileStore
{
    /// <summary>Path of the persisted counters under the Harbor home directory.</summary>
    private static string StorePath => Path.Combine(HarborPaths.GetHarborHome(), "starnag.txt");

    /// <summary>Creates the production card wired to this store.</summary>
    internal static StarNagCard CreateCard()
    {
        string path = StorePath;
        return new StarNagCard(StarNagCard.DefaultInterval, Load(path), state => Save(path, state));
    }

    /// <summary>Loads the counters, degrading to "never shown" on any IO failure.</summary>
    private static StarNagState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return StarNagState.Empty;
            }

            string[] parts = File.ReadAllText(path).Trim().Split(';');
            if (parts.Length != 3)
            {
                return StarNagState.Empty;
            }

            int launches = int.TryParse(parts[0], out int parsedLaunches) ? parsedLaunches : 0;
            bool dismissed = bool.TryParse(parts[1], out bool parsedDismissed) && parsedDismissed;
            int lastShownAt = int.TryParse(parts[2], out int parsedLast) ? parsedLast : 0;
            return new StarNagState(launches, dismissed, lastShownAt);
        }
        catch (IOException)
        {
            return StarNagState.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return StarNagState.Empty;
        }
    }

    /// <summary>Saves the counters; a reminder must never break startup.</summary>
    private static void Save(string path, StarNagState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, $"{state.Launches};{state.Dismissed};{state.LastShownAt}");
        }
        catch (IOException)
        {
            // A missed reminder is acceptable; a broken startup is not.
        }
        catch (UnauthorizedAccessException)
        {
            // A missed reminder is acceptable; a broken startup is not.
        }
    }
}
