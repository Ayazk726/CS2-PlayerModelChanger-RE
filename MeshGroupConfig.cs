using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

public class MeshGroupConfig
{
    public static string DirectoryPath(string moduleDirectory)
        => Path.Join(moduleDirectory, "../../configs/plugins/PlayerModelChanger-RE");

    public static string FilePath(string moduleDirectory)
        => Path.Join(DirectoryPath(moduleDirectory), "MeshGroups.json");

    private readonly Dictionary<string, Dictionary<string, bool>> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public static MeshGroupConfig Load(string moduleDirectory, ILogger? logger)
    {
        var config = new MeshGroupConfig();
        if (string.IsNullOrEmpty(moduleDirectory))
        {
            return config;
        }

        var path = FilePath(moduleDirectory);
        if (!File.Exists(path))
        {
            return config;
        }

        try
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(json);
            if (entries == null)
            {
                return config;
            }
            foreach (var entry in entries)
            {
                config._entries[entry.Key.Trim()] = new Dictionary<string, bool>(entry.Value, StringComparer.OrdinalIgnoreCase);
            }
            logger?.LogInformation($"[PlayerModelChanger] Mesh group config: {config._entries.Count} models read.");
        }
        catch (Exception ex)
        {
            logger?.LogError($"[PlayerModelChanger] The mesh group config could not be read: {ex.GetType().Name}: {ex.Message}. It is left as it is, the defaults are used.");
        }
        return config;
    }

    public bool IsEnabled(string modelName, string family, bool fallback)
    {
        if (!_entries.TryGetValue(modelName.Trim(), out var groups))
        {
            return fallback;
        }
        return groups.TryGetValue(family, out var enabled) ? enabled : fallback;
    }

    public static bool Write(string moduleDirectory, IEnumerable<Model> models, MeshGroupConfig config, ILogger? logger)
    {
        if (string.IsNullOrEmpty(moduleDirectory))
        {
            return false;
        }

        var entries = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var model in models)
        {
            var entry = MeshGroupCatalog.Get(model.Path);
            if (entry == null)
            {
                continue;
            }

            var groups = new Dictionary<string, bool>();
            foreach (var category in entry.Categories)
            {
                groups[category.Name] = config.IsEnabled(model.Name, category.Name, MeshGroupCatalog.IsEnabledByDefault(category));
            }
            if (groups.Count == 0)
            {
                continue;
            }
            entries[model.Name] = groups;
        }

        var path = FilePath(moduleDirectory);
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(File.ReadAllText(path));
                    if (existing != null && SameEntries(existing, entries))
                    {
                        return false;
                    }
                }
                catch
                {
                }
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var content = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(path, content);
            logger?.LogInformation($"[PlayerModelChanger] Mesh group config written ({entries.Count} models).");
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError($"[PlayerModelChanger] The mesh group config could not be written: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool SameEntries(Dictionary<string, Dictionary<string, bool>> a, Dictionary<string, Dictionary<string, bool>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var entry in b)
        {
            if (!a.TryGetValue(entry.Key, out var groups) || groups.Count != entry.Value.Count)
            {
                return false;
            }
            foreach (var group in entry.Value)
            {
                if (!groups.TryGetValue(group.Key, out var enabled) || enabled != group.Value)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
