using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Cs2Menus;

namespace PlayerModelChanger;

internal static class Cs2MenusBootstrap
{
    private const string PluginName = "cs2menus";

    private const int MaxDepth = 6;

    public sealed class Result
    {
        public bool Loaded { get; init; }

        public string? Path { get; init; }

        public string? Error { get; init; }

        public IReadOnlyList<string> Probed { get; init; } = Array.Empty<string>();
    }

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
