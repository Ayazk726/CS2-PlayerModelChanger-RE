using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

public class MeshGroupItem
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("group")] public string Group { get; set; } = "";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class MeshGroupCategory
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("items")] public List<MeshGroupItem> Items { get; set; } = [];
}

public class MeshGroupModel
{
    [JsonPropertyName("default")] public ulong DefaultMask { get; set; }

    [JsonPropertyName("categories")] public List<MeshGroupCategory> Categories { get; set; } = [];
}

public static class MeshGroupCatalog
{
    private static readonly Dictionary<string, MeshGroupModel> Models = new();

    public static int Count => Models.Count;

    public static void Load(string? moduleDirectory, ILogger? logger = null)
    {
        Models.Clear();

        var files = 0;
        var added = 0;
        var replaced = 0;
        var directory = ExternalDirectory(moduleDirectory);
        if (directory != null)
        {
            LoadExternalDirectory(Path.Join(directory, "auto"), logger, ref files, ref added, ref replaced);
            LoadExternalDirectory(directory, logger, ref files, ref added, ref replaced);
        }

        var suffix = files > 0 ? $" ({files} catalogue file(s), {added} models added, {replaced} replaced)" : "";
        logger?.LogInformation($"[PlayerModelChanger] Mesh group catalogue: {Models.Count} models{suffix}.");
    }

    public static string? ExternalDirectory(string? moduleDirectory)
        => string.IsNullOrEmpty(moduleDirectory)
            ? null
            : Path.Join(MeshGroupConfig.DirectoryPath(moduleDirectory), "catalogue");

    private static void LoadExternalDirectory(string directory, ILogger? logger, ref int files, ref int added, ref int replaced)
    {
        if (!Directory.Exists(directory)) return;
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex)
        {
            logger?.LogError($"[PlayerModelChanger] The mesh group catalogue directory could not be read: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.')) continue;
            try
            {
                var catalogue = JsonSerializer.Deserialize<Dictionary<string, MeshGroupModel>>(File.ReadAllText(path));
                if (catalogue == null) continue;
                files++;
                foreach (var entry in catalogue)
                {
                    var key = Normalize(entry.Key);
                    if (Models.ContainsKey(key))
                    {
                        replaced++;
                    }
                    else
                    {
                        added++;
                    }
                    Models[key] = entry.Value;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError($"[PlayerModelChanger] The mesh group catalogue file '{name}' could not be read: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static MeshGroupModel? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Models.TryGetValue(Normalize(path), out var model) ? model : null;
    }

    public static bool ApplyTo(Model model, MeshGroupConfig config)
    {
        if (model.Meshgroups.Count > 0) return false;

        var entry = Get(model.Path);
        if (entry == null || entry.Categories.Count == 0) return false;

        foreach (var category in entry.Categories)
        {
            if (!config.IsEnabled(model.Name, category.Name, IsEnabledByDefault(category)))
            {
                foreach (var item in category.Items)
                {
                    if (!model.FixedMeshgroups.ContainsKey(item.Index))
                    {
                        model.FixedMeshgroups[item.Index] = (int)((entry.DefaultMask >> item.Index) & 1);
                    }
                }
                continue;
            }

            var key = category.Type == "opradio" ? category.Name + "@opradio" : category.Name;

            var data = new Dictionary<string, int>();
            foreach (var item in category.Items)
            {
                data[item.Label] = item.Index;
            }
            model.Meshgroups[key] = JsonSerializer.SerializeToElement(data);
        }

        model.DefaultMeshgroupMask = entry.DefaultMask;
        return model.Meshgroups.Count > 0;
    }

    public static bool IsEnabledByDefault(MeshGroupCategory category)
        => category.Items.Count > 0 && category.Items.All(item => item.Enabled);

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
}
