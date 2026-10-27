using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

/// <summary>
/// One mesh group of a model the player may pick.
/// </summary>
public class MeshGroupItem
{
    /// <summary>
    /// The name the menu shows.
    /// </summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>
    /// The index of the mesh group, the bit in the mask of the model.
    /// </summary>
    [JsonPropertyName("index")] public int Index { get; set; }

    /// <summary>
    /// The name the model gave to the mesh group.
    /// </summary>
    [JsonPropertyName("group")] public string Group { get; set; } = "";

    /// <summary>
    /// True when the catalogue lets the player change the mesh group. The
    /// server owner can change this in the mesh group config; the whole family
    /// of mesh groups is opened or closed together.
    /// </summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>
/// A family of mesh groups the player picks from. The members of a family are
/// variants of the same part of the model, so only one of them is worn at a
/// time.
/// </summary>
public class MeshGroupCategory
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("items")] public List<MeshGroupItem> Items { get; set; } = [];
}

/// <summary>
/// The mesh groups of one model.
/// </summary>
public class MeshGroupModel
{
    /// <summary>
    /// The mask the model is worn with by default.
    /// </summary>
    [JsonPropertyName("default")] public ulong DefaultMask { get; set; }

    /// <summary>
    /// The mesh groups of the model, grouped by family.
    /// </summary>
    [JsonPropertyName("categories")] public List<MeshGroupCategory> Categories { get; set; } = [];
}

/// <summary>
/// The mesh groups of the models of the server, read from the catalogue files
/// of the server owner and the catalogues the plugin builds from the addon
/// VPKs.
/// </summary>
/// <remarks>
/// The game does not tell a plugin which mesh groups a model has, the names of
/// them live in the model file itself (m_meshGroups of the DATA block) and
/// only the mask of them is reachable at runtime. A catalogue is therefore
/// built offline from the model files, see tools/build_meshgroups.py, or by the
/// plugin itself when the Source2Viewer CLI sits next to it, see
/// MeshGroupCatalogueSync.
/// <para>
/// The plugin ships no catalogue of its own: it reads every JSON file of the
/// "catalogue" directory next to PlayerModelChanger.json, the "auto" folder
/// first and the files of the server owner on top of it. A server without any
/// catalogue file simply has no mesh groups the player may change.
/// </para>
/// <para>
/// A model that is not in either catalogue keeps the mesh groups it was made
/// with. That is what the original cs2 models do: they are not in the
/// catalogue, so the player cannot change anything about them.
/// </para>
/// </remarks>
public static class MeshGroupCatalog
{
    private static readonly Dictionary<string, MeshGroupModel> Models = new();

    /// <summary>
    /// The number of models in the catalogue.
    /// </summary>
    public static int Count => Models.Count;

    /// <summary>
    /// Reads the catalogue: every JSON file of the catalogue directory, which
    /// adds models or replaces the mesh groups of one.
    /// </summary>
    /// <param name="moduleDirectory">
    /// The plugin directory, used to find the catalogue directory. A null
    /// directory leaves the catalogue empty.
    /// </param>
    /// <remarks>
    /// The catalogue is read on every call, so a plugin reload picks up the
    /// files the plugin and the server owner put next to the configuration.
    /// </remarks>
    public static void Load(string? moduleDirectory, ILogger? logger = null)
    {
        Models.Clear();

        var files = 0;
        var added = 0;
        var replaced = 0;
        var directory = ExternalDirectory(moduleDirectory);
        if (directory != null)
        {
            // The catalogues the plugin writes itself are read first, the files
            // of the server owner are read on top of them, so they win.
            LoadExternalDirectory(Path.Join(directory, "auto"), logger, ref files, ref added, ref replaced);
            LoadExternalDirectory(directory, logger, ref files, ref added, ref replaced);
        }

        var suffix = files > 0 ? $" ({files} catalogue file(s), {added} models added, {replaced} replaced)" : "";
        logger?.LogInformation($"[PlayerModelChanger] Mesh group catalogue: {Models.Count} models{suffix}.");
    }

    /// <summary>
    /// The catalogue directory of the server owner, or null when there is no
    /// module directory to resolve it from.
    /// </summary>
    public static string? ExternalDirectory(string? moduleDirectory)
        => string.IsNullOrEmpty(moduleDirectory)
            ? null
            : Path.Join(MeshGroupConfig.DirectoryPath(moduleDirectory), "catalogue");

    /// <summary>
    /// Reads every JSON file of one catalogue directory, in name order, so a
    /// file that sorts later wins over an earlier one.
    /// </summary>
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

    /// <summary>
    /// The mesh groups of the model with the given path, or null when the model
    /// is not in the catalogue.
    /// </summary>
    public static MeshGroupModel? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Models.TryGetValue(Normalize(path), out var model) ? model : null;
    }

    /// <summary>
    /// Fills the mesh groups of the model from the catalogue.
    /// </summary>
    /// <param name="config">
    /// The mesh group families the server owner lets the player change. A
    /// family that is not in there keeps the value of the model.
    /// </param>
    /// <returns>
    /// True when the player may change something about the model.
    /// </returns>
    /// <remarks>
    /// A model that brings its own mesh groups in the configuration keeps them,
    /// the configuration wins over the catalogue.
    /// </remarks>
    public static bool ApplyTo(Model model, MeshGroupConfig config)
    {
        if (model.Meshgroups.Count > 0) return false;

        var entry = Get(model.Path);
        if (entry == null || entry.Categories.Count == 0) return false;

        foreach (var category in entry.Categories)
        {
            if (!config.IsEnabled(model.Name, category.Name, IsEnabledByDefault(category)))
            {
                // The families the player may not change keep the value of the
                // model, so every mesh group of them is fixed.
                foreach (var item in category.Items)
                {
                    if (!model.FixedMeshgroups.ContainsKey(item.Index))
                    {
                        model.FixedMeshgroups[item.Index] = (int)((entry.DefaultMask >> item.Index) & 1);
                    }
                }
                continue;
            }

            // The suffix tells the menu how the entries of the category behave:
            // one of them at a time ("@opradio") or every one of them on its
            // own (no suffix).
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

    /// <summary>
    /// True when the catalogue opens the whole family by default.
    /// </summary>
    /// <remarks>
    /// The catalogue marks every mesh group of a family the same way, so the
    /// first one stands for the family. A family of which only some mesh groups
    /// are open is closed, the player could not reach the rest of it anyway.
    /// </remarks>
    public static bool IsEnabledByDefault(MeshGroupCategory category)
        => category.Items.Count > 0 && category.Items.All(item => item.Enabled);

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
}
