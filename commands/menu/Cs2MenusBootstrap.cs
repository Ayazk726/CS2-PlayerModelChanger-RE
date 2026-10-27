using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Cs2Menus;

namespace PlayerModelChanger;

/// <summary>
/// Finds the mm-cs2menus native binary and hands it to the CSS bridge.
///
/// The bridge's own <c>LoadDefault()</c> resolves the binary from
/// <c>Server.GameDirectory</c> and assumes that folder is the <c>csgo/</c> one.
/// CounterStrikeSharp reports the parent <c>game/</c> directory instead, so that
/// lookup misses the file and the bridge never loads - every menu command then
/// degrades to "unavailable".
///
/// This resolves the binary over a set of roots instead: the game directory, the
/// plugin's own directory and their parents, each with and without a <c>csgo/</c>
/// folder in it. That covers both layouts and any custom install path.
/// </summary>
internal static class Cs2MenusBootstrap
{
    private const string PluginName = "cs2menus";

    /// <summary>How many parent folders above a root are probed.</summary>
    private const int MaxDepth = 6;

    public sealed class Result
    {
        /// <summary>True when the native library was loaded and the bridge can use it.</summary>
        public bool Loaded { get; init; }

        /// <summary>The absolute path of the binary that was loaded.</summary>
        public string? Path { get; init; }

        /// <summary>Why the load failed, when it did.</summary>
        public string? Error { get; init; }

        /// <summary>The paths that were probed, for the log.</summary>
        public IReadOnlyList<string> Probed { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The folder cs2menus installs its binary into, relative to a csgo root.
    /// </summary>
    public static string BinaryRelativePath()
    {
        string platform;
        string ext;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platform = "win64";
            ext = ".dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            platform = "osx64";
            ext = ".dylib";
        }
        else
        {
            platform = "linuxsteamrt64";
            ext = ".so";
        }

        return Path.Combine("addons", PluginName, "bin", platform, PluginName + ext);
    }

    /// <summary>
    /// Load the cs2menus binary found under one of <paramref name="roots"/>.
    /// The first candidate that loads wins.
    /// </summary>
    public static Result Load(IEnumerable<string> roots)
    {
        string relative = BinaryRelativePath();
        var probed = new List<string>();
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            foreach (string dir in EnumerateSearchDirs(root))
            {
                string candidate = Path.Combine(dir, relative);
                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        string? foundButFailed = null;

        foreach (string candidate in candidates)
        {
            probed.Add(candidate);

            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                if (Cs2MenusBridge.Load(candidate))
                {
                    return new Result { Loaded = true, Path = candidate, Probed = probed };
                }
            }
            catch (Exception ex)
            {
                foundButFailed = $"{candidate} ({ex.GetType().Name}: {ex.Message})";
                continue;
            }

            foundButFailed = $"{candidate} (its C ABI does not match the installed bridge)";
        }

        return new Result
        {
            Loaded = false,
            Error = foundButFailed ?? "no cs2menus binary found",
            Probed = probed,
        };
    }

    /// <summary>
    /// The directories to probe for the csgo root: the given root and its parents,
    /// each with and without a "csgo" folder in it.
    /// </summary>
    private static IEnumerable<string> EnumerateSearchDirs(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        DirectoryInfo? dir = null;
        try
        {
            dir = new DirectoryInfo(root);
        }
        catch (Exception)
        {
            // An unusable path, nothing to probe from it.
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int depth = 0; depth < MaxDepth && dir != null; depth++, dir = dir.Parent)
        {
            string[] dirs = { dir.FullName, Path.Combine(dir.FullName, "csgo") };
            foreach (string candidate in dirs)
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }
}
