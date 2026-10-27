using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

/// <summary>
/// The mesh groups the player may change, per model.
/// </summary>
/// <remarks>
/// The file belongs to the server owner: for every model of
/// PlayerModelChanger.json it holds every family of mesh groups of that model
/// and whether the player may change it.
/// <para>
/// The model is named the way it is named in PlayerModelChanger.json, and a
/// family is named the way the model names it ("Ears", "defusekit", ...). All
/// the mesh groups of a family share the one entry, so the server owner turns
/// the whole family on or off at once and never has to write the raw names of
/// the model ("Ears_@0_#&amp;1", ...) down.
/// </para>
/// <para>
/// A family that is off keeps the value the model was made with, the player
/// cannot change it. The families that belong to the model itself (the view it
/// is drawn in, the face and the defuse kit of the character) are off by
/// default.
/// </para>
/// </remarks>
public class MeshGroupConfig
{
    /// <summary>
    /// The directory of the files the server owner edits, next to
    /// PlayerModelChanger.json.
    /// </summary>
    public static string DirectoryPath(string moduleDirectory)
        => Path.Join(moduleDirectory, "../../configs/plugins/PlayerModelChanger-RE");

    /// <summary>
    /// The path of the file, next to PlayerModelChanger.json.
    /// </summary>
    public static string FilePath(string moduleDirectory)
        => Path.Join(DirectoryPath(moduleDirectory), "MeshGroups.json");

    // model name -> mesh group family -> the player may change it
    private readonly Dictionary<string, Dictionary<string, bool>> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The number of models the server owner wrote down.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Reads the file of the server owner. A file that cannot be read is left
    /// alone, the defaults of the catalogue are used until it is fixed.
    /// </summary>
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

    /// <summary>
    /// True when the player may change the mesh groups of the family.
    /// </summary>
    /// <param name="fallback">
    /// The value of the catalogue, used when the server owner did not write
    /// the family down.
    /// </param>
    public bool IsEnabled(string modelName, string family, bool fallback)
    {
        if (!_entries.TryGetValue(modelName.Trim(), out var groups))
        {
            return fallback;
        }
        return groups.TryGetValue(family, out var enabled) ? enabled : fallback;
    }

    /// <summary>
    /// Writes the file with the mesh groups of the models of the
    /// configuration. The choice of the server owner is kept, a family that is
    /// new gets the value of the catalogue.
    /// </summary>
    /// <returns>
    /// True when the file was written.
    /// </returns>
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
            // Nothing to do when the file already says the same.
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
                    // A file that cannot be read is written again below.
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
                // The names of the mesh groups hold characters that would be
                // escaped otherwise, the file belongs to the server owner and
                // has to stay readable.
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
