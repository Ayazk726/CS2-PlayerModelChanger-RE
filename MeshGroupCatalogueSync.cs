using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

public sealed class MeshGroupCatalogueSync : IDisposable
{
    private static readonly string[] CliCandidates =
    [
        "Source2Viewer-CLI.exe",
        "Source2Viewer-CLI",
        "cli-windows-x64/Source2Viewer-CLI.exe",
        "cli-windows-x64/Source2Viewer-CLI",
        "cli-linux-x64/Source2Viewer-CLI",
    ];
    private const string CatalogueFolder = "auto";
    private const string CacheFile = ".cache";

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan FreshFileWindow = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions CatalogueOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _moduleDirectory;
    private readonly string _gameDirectory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _configuredWorkshopIds;
    private readonly IReadOnlyList<string> _configuredRoots;
    private readonly Action _onCatalogueChanged;
    private readonly CancellationTokenSource _cts = new();

    private sealed class CacheEntry
    {
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("mtime")] public long Mtime { get; set; }
        [JsonPropertyName("models")] public int Models { get; set; }
        [JsonPropertyName("file")] public string File { get; set; } = "";
    }

    private sealed class SyncOutcome
    {
        public bool Changed { get; set; }
        public bool CacheChanged { get; set; }
        public int UpdatedAddons { get; set; }
        public int UpdatedModels { get; set; }
    }

    public MeshGroupCatalogueSync(string moduleDirectory, string gameDirectory, ILogger logger, IReadOnlyList<string> configuredWorkshopIds, IReadOnlyList<string> configuredRoots, Action onCatalogueChanged)
    {
        _moduleDirectory = moduleDirectory;
        _gameDirectory = gameDirectory;
        _logger = logger;
        _configuredWorkshopIds = configuredWorkshopIds;
        _configuredRoots = configuredRoots;
        _onCatalogueChanged = onCatalogueChanged;
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        try
        {
            if (_configuredWorkshopIds.Count == 0 && _configuredRoots.Count == 0)
            {
                LogInfo("Mesh group catalogue sync disabled: set MeshgroupCatalogueWorkshopIds or MeshgroupCatalogueVpkDirs to enable it.");
                return;
            }

            var cli = FindCli(_moduleDirectory);
            if (cli == null)
            {
                LogInfo("Mesh group catalogue sync disabled: Source2Viewer-CLI(.exe) is not next to the plugin DLL.");
                return;
            }
            CheckCliDependencies(cli);
            LogInfo($"Mesh group catalogue sync: using {Path.GetFileName(cli)} from the plugin directory.");

            var catalogueDirectory = MeshGroupCatalog.ExternalDirectory(_moduleDirectory);
            if (string.IsNullOrEmpty(catalogueDirectory))
            {
                LogError("Mesh group catalogue sync: the catalogue directory could not be resolved.");
                return;
            }
            var autoDirectory = Path.Join(catalogueDirectory, CatalogueFolder);
            Directory.CreateDirectory(autoDirectory);
            var cachePath = Path.Join(autoDirectory, CacheFile);
            var cache = ReadCache(cachePath);

            var totalAddons = 0;
            var totalModels = 0;
            var totalChanged = false;

            var outcome = await RunOnceAsync(cli, autoDirectory, cache, _configuredWorkshopIds.Count == 0, logMissing: true, token);
            totalChanged |= outcome.Changed;
            totalAddons += outcome.UpdatedAddons;
            totalModels += outcome.UpdatedModels;
            if (outcome.Changed || outcome.CacheChanged)
            {
                WriteCache(cachePath, cache);
            }
            if (outcome.Changed)
            {
                _onCatalogueChanged();
            }

            if (totalChanged)
            {
                LogInfo($"Mesh group catalogue sync finished ({totalAddons} addon(s), {totalModels} models, {started.Elapsed.TotalSeconds:0}s).");
            }
            else
            {
                LogInfo("Mesh group catalogue sync finished (nothing to update).");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogError($"Mesh group catalogue sync failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task<SyncOutcome> RunOnceAsync(string cli, string autoDirectory, Dictionary<string, CacheEntry> cache, bool scanAll, bool logMissing, CancellationToken token)
    {
        var outcome = new SyncOutcome();
        var roots = ResolveRoots();
        List<(string Path, string Name)> vpks;
        List<string> missing;
        if (scanAll)
        {
            vpks = FindVpks(roots);
            missing = [];
        }
        else
        {
            vpks = ResolveConfiguredAddons(roots, logMissing, out missing);
        }
        if (logMissing && vpks.Count > 0)
        {
            LogInfo(scanAll
                ? $"Mesh group catalogue sync started ({vpks.Count} addon VPK(s))."
                : $"Mesh group catalogue sync started ({vpks.Count} configured addon(s)).");
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notReady = 0;
        foreach (var (path, name) in vpks)
        {
            token.ThrowIfCancellationRequested();
            known.Add(path);
            var file = new FileInfo(path);
            file.Refresh();
            var size = file.Length;
            var mtime = file.LastWriteTimeUtc.Ticks;
            cache.TryGetValue(path, out var entry);
            if (entry != null && entry.Size == size && entry.Mtime == mtime
                && (entry.File.Length == 0 || File.Exists(Path.Join(autoDirectory, entry.File))))
            {
                continue;
            }

            if (!IsFileReady(path))
            {
                notReady++;
                continue;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                var models = await ListModelsAsync(cli, path, token);
                if (models.Count == 0)
                {
                    DeleteAutoFile(autoDirectory, entry);
                    cache[path] = new CacheEntry { Size = size, Mtime = mtime, Models = 0, File = "" };
                    outcome.CacheChanged = true;
                    continue;
                }

                var catalogue = await ExtractAsync(cli, path, models, token);
                if (catalogue.Count == 0)
                {
                    DeleteAutoFile(autoDirectory, entry);
                    cache[path] = new CacheEntry { Size = size, Mtime = mtime, Models = models.Count, File = "" };
                    outcome.CacheChanged = true;
                    continue;
                }

                var fileName = "auto-" + SafeName(name) + ".json";
                WriteCatalogue(Path.Join(autoDirectory, fileName), catalogue);
                if (entry != null && entry.File.Length > 0 && !string.Equals(entry.File, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteAutoFile(autoDirectory, entry);
                }
                cache[path] = new CacheEntry { Size = size, Mtime = mtime, Models = catalogue.Count, File = fileName };
                outcome.Changed = true;
                outcome.CacheChanged = true;
                outcome.UpdatedAddons++;
                outcome.UpdatedModels += catalogue.Count;
                LogInfo($"Mesh group catalogue sync: {name} ({catalogue.Count} models, {watch.Elapsed.TotalSeconds:0}s).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogError($"Mesh group catalogue sync: {name} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (scanAll)
        {
            foreach (var path in cache.Keys.Where(p => !known.Contains(p)).ToList())
            {
                DeleteAutoFile(autoDirectory, cache[path]);
                cache.Remove(path);
                outcome.Changed = true;
                outcome.CacheChanged = true;
            }
        }

        if (missing.Count > 0)
        {
            var shown = string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? ", ..." : "");
            LogInfo($"Mesh group catalogue sync: {shown} is not on the server yet, the catalogue is built after the next server start.");
        }
        else if (notReady > 0)
        {
            LogInfo("Mesh group catalogue sync: an addon is still being downloaded, it is read after the next server start.");
        }
        else if (scanAll && vpks.Count == 0)
        {
            LogInfo("Mesh group catalogue sync: no addon VPK in the configured folder(s) yet, nothing to do.");
        }
        return outcome;
    }

    public static bool IsFileReady(string dirVpkPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(dirVpkPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
            var files = Directory.GetFiles(folder, "*.vpk");
            if (files.Length == 0) return false;
            var now = DateTime.UtcNow;
            foreach (var path in files)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return false;
                if (now - info.LastWriteTimeUtc < FreshFileWindow) return false;
            }
            using var stream = new FileStream(dirVpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? FindCli(string moduleDirectory)
    {
        if (string.IsNullOrEmpty(moduleDirectory)) return null;
        foreach (var relative in CliCandidates)
        {
            var path = Path.Join(moduleDirectory, relative);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    private void CheckCliDependencies(string cli)
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.GetDirectoryName(cli);
        if (string.IsNullOrEmpty(folder)) return;
        foreach (var dependency in new[] { "libSkiaSharp.dll", "spirv-cross.dll" })
        {
            if (!File.Exists(Path.Join(folder, dependency)))
            {
                LogWarning($"Mesh group catalogue sync: {dependency} is not next to the CLI, reading the VPKs may fail.");
            }
        }
    }

    private async Task<List<string>> ListModelsAsync(string cli, string vpk, CancellationToken token)
    {
        var text = await RunCliAsync(cli, ["-i", vpk, "--vpk_dir"], ListTimeout, token);
        return MeshGroupCatalogueBuilder.ParseModelList(text);
    }

    private async Task<SortedDictionary<string, MeshGroupModel>> ExtractAsync(string cli, string vpk, List<string> models, CancellationToken token)
    {
        var catalogue = new SortedDictionary<string, MeshGroupModel>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            token.ThrowIfCancellationRequested();
            var text = await RunCliAsync(cli, ["-i", vpk, "-b", "DATA", "-f", model], DataTimeout, token);
            var (groups, defaultMask) = MeshGroupCatalogueBuilder.ParseDataBlock(text);
            if (groups.Count == 0 || defaultMask == null) continue;
            var key = model[..^".vmdl_c".Length] + ".vmdl";
            catalogue[key] = MeshGroupCatalogueBuilder.BuildModel(groups, defaultMask.Value);
        }
        return catalogue;
    }

    private static async Task<string> RunCliAsync(string cli, string[] arguments, TimeSpan timeout, CancellationToken token)
    {
        var info = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
        };
        if (!OperatingSystem.IsWindows())
        {
            var folder = Path.GetDirectoryName(cli)!;
            var existing = info.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : null;
            info.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? folder : folder + ":" + existing;
        }
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The CLI could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"The CLI did not answer within {timeout.TotalSeconds:0} seconds.");
        }

        var text = await output;
        await error;
        return text;
    }

    public static string? FindAddonVpk(string workshopId, IEnumerable<string> roots)
    {
        var id = workshopId.Trim();
        if (id.Length == 0) return null;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var folder = Path.GetFullPath(Path.Join(root, id));
                if (!Directory.Exists(folder)) continue;

                var vpk = Path.Join(folder, id + "_dir.vpk");
                if (File.Exists(vpk)) return Path.GetFullPath(vpk);

                vpk = Path.Join(folder, id + ".vpk");
                if (File.Exists(vpk)) return Path.GetFullPath(vpk);

                vpk = Directory.EnumerateFiles(folder, "*_dir.vpk", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (vpk != null) return Path.GetFullPath(vpk);

                vpk = Directory.EnumerateFiles(folder, "*.vpk", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(IsLegacyAddonVpk);
                if (vpk != null) return Path.GetFullPath(vpk);
            }
            catch
            {
            }
        }
        return null;
    }

    private static bool IsLegacyAddonVpk(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 0 && name.All(char.IsDigit);
    }

    private static string NameOfVpk(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        if (name.Length > 0) return name;
        name = Path.GetFileName(path);
        return name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase)
            ? name[..^"_dir.vpk".Length]
            : name;
    }

    private List<(string Path, string Name)> ResolveConfiguredAddons(List<string> roots, bool logMissing, out List<string> missing)
    {
        var result = new List<(string Path, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        missing = new List<string>();
        foreach (var id in _configuredWorkshopIds)
        {
            var path = FindAddonVpk(id, roots);
            if (path == null)
            {
                missing.Add(id.Trim());
                if (logMissing)
                {
                    LogWarning($"Mesh group catalogue sync: the configured workshop addon '{id.Trim()}' is not on the server yet, it is read after the next server start.");
                }
                continue;
            }
            if (seen.Add(path))
            {
                result.Add((path, NameOfVpk(path)));
            }
        }
        return result;
    }

    private List<(string Path, string Name)> FindVpks(List<string> roots)
    {
        var result = new List<(string Path, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.vpk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(path);
                    if (!name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) && !IsLegacyAddonVpk(path))
                    {
                        continue;
                    }
                    var full = Path.GetFullPath(path);
                    if (!seen.Add(full)) continue;
                    result.Add((full, NameOfVpk(full)));
                }
            }
            catch (Exception ex)
            {
                LogError($"Mesh group catalogue sync: the addon directory could not be scanned: {ex.GetType().Name}: {ex.Message}");
            }
        }
        result.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<string> ResolveRoots()
    {
        var roots = new List<string>();
        if (_configuredRoots.Count > 0)
        {
            foreach (var root in _configuredRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                AddRoot(roots, Path.IsPathRooted(root) ? root : Path.Join(_moduleDirectory, root));
            }
            return roots;
        }
        roots.AddRange(CandidateRoots(_moduleDirectory, _gameDirectory));
        return roots;
    }

    public static List<string> CandidateRoots(string moduleDirectory, string gameDirectory)
    {
        var roots = new List<string>();

        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(executableDirectory))
        {
            AddRoot(roots, Path.Join(executableDirectory, "steamapps/workshop/content/730"));
        }

        var game = gameDirectory;
        var gameRoot = Path.Join(moduleDirectory, "../../../../.."); // <install>/game
        foreach (var platform in new[] { "win64", "linuxsteamrt64" })
        {
            AddRoot(roots, Path.Join(gameRoot, "bin", platform, "steamapps/workshop/content/730"));
            AddRoot(roots, Path.Join(game, "bin", platform, "steamapps/workshop/content/730"));
            AddRoot(roots, Path.Join(game, "../bin", platform, "steamapps/workshop/content/730"));
            AddRoot(roots, Path.Join(game, "csgo/bin", platform, "steamapps/workshop/content/730"));
        }
        try
        {
            foreach (var bin in Directory.GetDirectories(Path.Join(gameRoot, "bin")))
            {
                AddRoot(roots, Path.Join(bin, "steamapps/workshop/content/730"));
            }
        }
        catch
        {
        }

        AddRoot(roots, Path.Join(moduleDirectory, "../../../../../../steamapps/workshop/content/730"));
        AddRoot(roots, Path.Join(game, "steamapps/workshop/content/730"));
        AddRoot(roots, Path.Join(game, "../steamapps/workshop/content/730"));
        AddRoot(roots, Path.Join(game, "csgo/steamapps/workshop/content/730"));

        AddRoot(roots, Path.Join(Environment.CurrentDirectory, "steamapps/workshop/content/730"));

        return roots;
    }

    private static void AddRoot(List<string> roots, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(full);
            }
        }
        catch
        {
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }
        var safe = builder.ToString().Trim();
        return safe.Length == 0 ? "addon" : safe;
    }

    private static void WriteCatalogue(string path, SortedDictionary<string, MeshGroupModel> catalogue)
    {
        var json = JsonSerializer.Serialize(catalogue, CatalogueOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static void DeleteAutoFile(string autoDirectory, CacheEntry? entry)
    {
        if (entry == null || entry.File.Length == 0) return;
        try
        {
            var path = Path.Join(autoDirectory, entry.File);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static Dictionary<string, CacheEntry> ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            var cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(path));
            if (cache == null) return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, CacheEntry>(cache, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteCache(string path, Dictionary<string, CacheEntry> cache)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch
        {
        }
    }

    private void LogInfo(string message)
    {
        try { _logger.LogInformation($"[PlayerModelChanger] {message}"); } catch { }
    }

    private void LogWarning(string message)
    {
        try { _logger.LogWarning($"[PlayerModelChanger] {message}"); } catch { }
    }

    private void LogError(string message)
    {
        try { _logger.LogError($"[PlayerModelChanger] {message}"); } catch { }
    }
}
