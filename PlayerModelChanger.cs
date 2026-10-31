using CounterStrikeSharp.API.Core;

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Drawing;
using CounterStrikeSharp.API.Modules.Config;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Memory;
using System.Runtime.InteropServices;
using Cs2Menus;
namespace PlayerModelChanger;

public partial class PlayerModelChanger : BasePlugin, IPluginConfig<ModelConfig>
{
    public override string ModuleName => "PlayerModelChanger-RE";
    public override string ModuleVersion => "1.8.6";

    public override string ModuleAuthor => "samyyc";
    public required ModelConfig Config { get; set; }
    public required ModelService Service { get; set; }
    public required DefaultModelManager DefaultModelManager { get; set; }

    public ModelMenuManager MenuManager { get; set; } = null!;

    private Cs2MenusBridge? _menus;

    private const int MenusStartupGraceTicks = 600;
    private int _menusStartupTicks = -1;

    private bool _menusReadyLogged = false;

    private readonly ConcurrentDictionary<int, int> _hiddenMenus = new();

    private const int HiddenMenuGraceTicks = 120;

    private static PlayerModelChanger? _Instance { get; set; }

    private MeshGroupCatalogueSync? _catalogueSync;

    private int _catalogueRefreshPending;

    private readonly ConcurrentDictionary<string, string> _botModelIndex = new();

    private const string NoBotModel = "@no-bot-model";

    private bool _freezeBotModelsUntilMapStart;

    public bool Enable = true;

    public override void Load(bool hotReload)
    {
        _Instance = this;
        IStorage? Storage = null;
        switch (Config.StorageType)
        {
            case "sqlite":
                Storage = new SqliteStorage(ModuleDirectory);
                break;
            case "mysql":
                Storage = new MySQLStorage(Config.MySQLIP, Config.MySQLPort, Config.MySQLUser, Config.MySQLPassword, Config.MySQLDatabase, Config.MySQLTable);
                break;
        }
        ;
        if (Storage == null)
        {
            throw new Exception("[PlayerModelChanger] Failed to initialize storage. Please check your config");
        }
        DefaultModelManager = new DefaultModelManager();
        this.Service = new ModelService(Config, Storage, Localizer, DefaultModelManager);
        DefaultModelManager.ReloadConfig(ModuleDirectory, Service);

        var bootstrap = Cs2MenusBootstrap.Load(new[] { CounterStrikeSharp.API.Server.GameDirectory, ModuleDirectory });
        MenuManager = new ModelMenuManager();

        if (bootstrap.Loaded)
        {
            _menus = new Cs2MenusBridge();
            MenuManager.Initialize(_menus);
            Logger.LogInformation($"[PlayerModelChanger] mm-cs2menus bridge loaded ({Path.GetFileName(bootstrap.Path)}).");

            if (_menus.Available)
            {
                LogMenusReady();
            }
            else
            {
                Logger.LogInformation("[PlayerModelChanger] cs2menus is still starting up, the menu availability is checked on demand.");
                _menusStartupTicks = MenusStartupGraceTicks;
            }
        }
        else
        {
            Logger.LogWarning($"[PlayerModelChanger] mm-cs2menus not found ({bootstrap.Error}). Install the Metamod plugin to enable the menus.");
        }

        if (!Config.DisablePrecache)
        {
            RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheResource);
        }
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        Inspection.InspectionEnded += OnInspectionEnded;
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            Service.ClearMapDefaultModel();
            _botModelIndex.Clear();
        });

        _freezeBotModelsUntilMapStart = hotReload;
        if (hotReload)
        {
            CacheCurrentBotModels();
            Logger.LogInformation("[PlayerModelChanger] Bots keep their current model until the next map change.");
        }

        Utils.InitializeLangPrefix();

        RegisterEventHandler<EventPlayerActivate>((@event, info) =>
        {
            if (@event.Userid != null)
            {
                MenuManager.RemovePlayer(@event.Userid.Slot);
                _hiddenMenus.TryRemove(@event.Userid.Slot, out _);
                _sideMenus.TryRemove(@event.Userid.Slot, out _);
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            if (@event.Userid != null)
            {
                MenuManager.RemovePlayer(@event.Userid.Slot);
                _hiddenMenus.TryRemove(@event.Userid.Slot, out _);
                _sideMenus.TryRemove(@event.Userid.Slot, out _);
                Inspection.RemovePlayerProps(@event.Userid.Slot);
            }
            return HookResult.Continue;
        });

        if (hotReload)
        {
            MenuManager.ReloadPlayer();
        }

        StartMeshGroupCatalogueSync();
        Logger.LogInformation($"Loaded {Service.GetModelCount()} model(s) successfully.");
    }

    private void StartMeshGroupCatalogueSync()
    {
        _catalogueSync?.Dispose();
        _catalogueSync = null;
        if (Config.DisableAutoMeshgroupCatalogue || Config.DisableMeshgroupDetection)
        {
            return;
        }

        _catalogueSync = new MeshGroupCatalogueSync(
            ModuleDirectory,
            CounterStrikeSharp.API.Server.GameDirectory,
            Logger,
            Config.MeshgroupCatalogueWorkshopIds,
            Config.MeshgroupCatalogueVpkDirs,
            () => Interlocked.Exchange(ref _catalogueRefreshPending, 1));
        _catalogueSync.Start();
    }

    private void RefreshMeshGroupsFromCatalogue()
    {
        try
        {
            MeshGroupCatalog.Load(ModuleDirectory, Logger);
            if (Config.DisableMeshgroupDetection)
            {
                return;
            }
            var meshGroupConfig = MeshGroupConfig.Load(ModuleDirectory, Logger);
            var modelsWithMeshgroups = 0;
            foreach (var model in Config.Models.Values)
            {
                if (MeshGroupCatalog.ApplyTo(model, meshGroupConfig))
                {
                    modelsWithMeshgroups++;
                }
            }
            MeshGroupConfig.Write(ModuleDirectory, Config.Models.Values, meshGroupConfig, Logger);
            Logger.LogInformation($"[PlayerModelChanger] The mesh groups of {modelsWithMeshgroups} model(s) were added from the new catalogue.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[PlayerModelChanger] The mesh groups could not be refreshed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void PrecacheResource(ResourceManifest manifest)
    {
        foreach (var model in Service.GetAllModels())
        {
            Logger.LogInformation($"Precaching {model.Path}");
            manifest.AddResource(model.Path);
        }
    }

    public override void Unload(bool hotReload)
    {
        _Instance = null;
        _catalogueSync?.Dispose();
        _catalogueSync = null;
        Interlocked.Exchange(ref _catalogueRefreshPending, 0);
        RemoveListener<Listeners.OnServerPrecacheResources>(PrecacheResource);
        RemoveListener<Listeners.OnTick>(OnTick);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        Inspection.InspectionEnded -= OnInspectionEnded;
        _hiddenMenus.Clear();
        _sideMenus.Clear();

        Inspection.Cleanup();

        try { MenuManager?.Dispose(); } catch { }
        try { _menus?.Dispose(); } catch { }
        _menus = null;
        _menusStartupTicks = -1;

        Logger.LogInformation("Unloaded successfully.");
    }

    public static PlayerModelChanger getInstance()
    {
        return _Instance!;
    }

    public string Message(string key, params object[] args) => Utils.Message(Localizer, key, args);

    public void ReloadConfig()
    {
        var config = typeof(ConfigManager)
                   .GetMethod("Load")!
                   .MakeGenericMethod(typeof(ModelConfig))
                   .Invoke(null, new object[] { Path.GetFileName(ModuleDirectory) }) as IBasePluginConfig;
        OnConfigParsed((config as ModelConfig)!);
        Unload(true);
        Load(true);
        Service.ReloadConfig(ModuleDirectory, Config);
    }
    public void OnConfigParsed(ModelConfig config)
    {
        var availableStorageType = new[] { "sqlite", "mysql" };
        if (!availableStorageType.Contains(config.StorageType))
        {
            throw new Exception($"[PlayerModelChanger] Unknown storage type: {Config.StorageType}, available types: {string.Join(",", availableStorageType)}");
        }

        if (config.StorageType == "mysql")
        {
            if (config.MySQLIP == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MySQL_IP");
            }
            if (config.MySQLPort == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MYSQL_Port");
            }
            if (config.MySQLUser == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MYSQL_User");
            }
            if (config.MySQLPassword == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MYSQL_Password");
            }
            if (config.MySQLDatabase == "")
            {
                throw new Exception("[PlayerModelChanger] You must fill in the MySQL_Database");
            }
        }

        if (config.ModelForBots == null)
        {
            config.ModelForBots = new BotsConfig();
        }

        MeshGroupCatalog.Load(ModuleDirectory, Logger);
        var meshGroupConfig = MeshGroupConfig.Load(ModuleDirectory, Logger);
        var modelsWithMeshgroups = 0;

        for (int i = 0; i < config.Models.Count; i++)
        {
            var entry = config.Models.ElementAt(i);
            ModelService.InitializeModel(entry.Key, entry.Value);
            if (!config.DisableMeshgroupDetection && MeshGroupCatalog.ApplyTo(entry.Value, meshGroupConfig))
            {
                modelsWithMeshgroups++;
            }
            if (config.Models.Where(m => m.Value.Name == entry.Value.Name).Count() > 1)
            {
                throw new Exception($"[PlayerModelChanger] Found duplicated model name: {entry.Value.Name}");
            }
        }

        MeshGroupConfig.Write(ModuleDirectory, config.Models.Values, meshGroupConfig, Logger);

        if (modelsWithMeshgroups > 0)
        {
            Logger.LogInformation($"[PlayerModelChanger] {modelsWithMeshgroups} of {config.Models.Count} models have mesh groups the player can change.");
        }

        var sideLimited = config.Models.Values.Count(model => model.Side != Side.All);
        if (sideLimited > 0)
        {
            Logger.LogWarning($"[PlayerModelChanger] {sideLimited} models are limited to one side and are not offered: the player wears one model for both sides.");
        }

        Config = config;
    }

    public void OnTick()
    {
        if (Interlocked.Exchange(ref _catalogueRefreshPending, 0) == 1)
        {
            RefreshMeshGroupsFromCatalogue();
        }

        Inspection.UpdateCamera();
        CheckMenusStartup();
        CheckHiddenMenus();
    }

    private void OnMapStart(string mapName)
    {
        _freezeBotModelsUntilMapStart = false;
    }

    private void CacheCurrentBotModels()
    {
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            var pawn = bot.Pawn.Value;
            if (pawn == null || !pawn.IsValid) continue;
            var modelName = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
            if (string.IsNullOrEmpty(modelName)) continue;

            var model = Service.GetAllModels().FirstOrDefault(candidate =>
                string.Equals(candidate.Path, modelName, StringComparison.OrdinalIgnoreCase));
            if (model == null) continue;

            _botModelIndex[BotKey(bot)] = model.Index;
        }
    }

    private void HideMenuWhileInspecting(CCSPlayerController player)
    {
        if (!MenuManager.Available) return;
        MenuManager.SuspendMenu(player);
        _hiddenMenus[player.Slot] = 0;
    }

    private void ShowMenuAgain(CCSPlayerController player)
    {
        if (!_hiddenMenus.TryRemove(player.Slot, out _)) return;
        MenuManager.ResumeMenu(player);
    }

    private void OnInspectionEnded(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        ShowMenuAgain(player);
    }

    private void CheckHiddenMenus()
    {
        if (_hiddenMenus.IsEmpty) return;

        foreach (var slot in _hiddenMenus.Keys)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid)
            {
                _hiddenMenus.TryRemove(slot, out _);
                continue;
            }

            if (Inspection.HasCamera(player))
            {
                _hiddenMenus[slot] = 0;
                continue;
            }

            int ticks = _hiddenMenus.TryGetValue(slot, out var waited) ? waited : 0;
            if (ticks >= HiddenMenuGraceTicks)
            {
                ShowMenuAgain(player);
            }
            else
            {
                _hiddenMenus[slot] = ticks + 1;
            }
        }
    }

    private void CheckMenusStartup()
    {
        if (_menusStartupTicks < 0)
        {
            return;
        }
        if (_menusStartupTicks > 0)
        {
            _menusStartupTicks--;
            return;
        }
        _menusStartupTicks = -1;

        if (_menus == null)
        {
            return;
        }

        if (_menus.Available)
        {
            LogMenusReady();
        }
        else
        {
            Logger.LogWarning("[PlayerModelChanger] mm-cs2menus is still not available, the model menus will report unavailable.");
        }
    }

    private void LogMenusReady()
    {
        if (_menusReadyLogged) return;
        _menusReadyLogged = true;
        Logger.LogInformation("[PlayerModelChanger] mm-cs2menus is ready, the model menus are enabled.");
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawnEvent(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!Enable)
        {
            return HookResult.Continue;
        }

        if (@event == null)
        {
            return HookResult.Continue;
        }

        CCSPlayerController? player = @event.Userid;

        if (player == null
            || !player.IsValid)
        {
            return HookResult.Continue;
        }
        try
        {
            CsTeam team = (CsTeam)player.TeamNum;

            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
            {
                return HookResult.Continue;
            }

            if (player.IsBot)
            {
                var botKey = BotKey(player);

                if (_freezeBotModelsUntilMapStart)
                {
                    if (_botModelIndex.TryGetValue(botKey, out var frozenIndex))
                    {
                        var frozenModel = Service.GetModel(frozenIndex);
                        if (frozenModel != null)
                        {
                            SetBotModelNextServerFrame(player, frozenModel);
                        }
                    }
                    return HookResult.Continue;
                }

                if (_botModelIndex.TryGetValue(botKey, out var chosenIndex))
                {
                    if (chosenIndex == NoBotModel)
                    {
                        return HookResult.Continue;
                    }
                    var chosenModel = Service.GetModel(chosenIndex);
                    if (chosenModel != null)
                    {
                        SetBotModelNextServerFrame(player, chosenModel);
                        return HookResult.Continue;
                    }
                    _botModelIndex.TryRemove(botKey, out _);
                }

                var modelindex = PickBotModelIndex(player, team);
                if (modelindex == null)
                {
                    _botModelIndex[botKey] = NoBotModel;
                    return HookResult.Continue;
                }

                var botmodel = Service.GetModel(modelindex);
                if (botmodel == null)
                {
                    _botModelIndex.TryRemove(botKey, out _);
                    Server.NextFrame(() =>
                    {
                        if (player.IsValid && player.Pawn.Value != null)
                        {
                            var originalRender = player.Pawn.Value.Render;
                            player.Pawn.Value.Render = Color.FromArgb(255, originalRender.R, originalRender.G, originalRender.B);
                        }
                    });
                    return HookResult.Continue;
                }

                _botModelIndex[botKey] = modelindex;
                SetBotModelNextServerFrame(player, botmodel);
                return HookResult.Continue;
            }

            if (player.AuthorizedSteamID == null)
            {
                return HookResult.Continue;
            }

            if (
            player.PlayerPawn == null
            || !player.PlayerPawn.IsValid
            || player.PlayerPawn.Value == null
            || !player.PlayerPawn.Value.IsValid
            )
            {
                return HookResult.Continue;
            }

            if (Config.AutoResyncCache)
            {
                Service.ResyncCache();
            }

            if (!Config.DisableAutoCheck)
            {
                var result = Service.CheckAndReplaceModel(player);

                if (result.Item1 || result.Item2)
                {
                    player.PrintToChat(Localizer["model.invalidreseted"]);
                }
            }

            Server.NextFrame(() =>
            {
                if (!Service.MapDefaultModelInitialized(player))
                {
                    Service.SetMapDefaultModel(player, player.PlayerPawn.Value.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.ModelName);
                }
                Server.NextFrame(() =>
                {
                    Service.RollPickedModels(player);
                    var model = Service.GetPlayerModel(player, Side.All);
                    if (model != null)
                    {
                        SetModelNextServerFrame(player, model, model.Disableleg);
                    }
                    else
                    {
                        var originalRender = player.PlayerPawn.Value.Render;
                        player.PlayerPawn.Value.Render = Color.FromArgb(Config.DisableDefaultModelLeg ? 254 : 255, originalRender.R, originalRender.G, originalRender.B);
                        Utilities.SetStateChanged(player.PlayerPawn.Value, "CBaseModelEntity", "m_clrRender");
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Logger.LogInformation("Could not set player model: {0}", ex);
        }

        return HookResult.Continue;
    }

    private static string BotKey(CCSPlayerController player)
        => (player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1)).ToString();

    private string? PickBotModelIndex(CCSPlayerController player, CsTeam team)
    {
        var entries = team == CsTeam.Terrorist ? Config.ModelForBots.T : Config.ModelForBots.CT;
        if (entries.Count == 0) return null;

        var valid = entries.Where(entry => entry == "@random" || Service.GetModel(entry) != null).ToList();
        if (valid.Count == 0) return null;

        var modelindex = valid[Random.Shared.Next(valid.Count)];
        if (modelindex == "@random")
        {
            var models = Service.GetAllModels();
            if (models.Count == 0) return null;
            modelindex = models[Random.Shared.Next(models.Count)].Index;
        }

        return modelindex;
    }

    private void SetBotModelNextServerFrame(CCSPlayerController player, Model model)
    {
        Server.NextFrame(() =>
        {
            if (!player.IsValid || player.Pawn.Value == null || !player.Pawn.Value.IsValid)
            {
                return;
            }

            var pawn = player.Pawn.Value;
            var originalRender = pawn.Render;

            pawn.SetModel(model.Path);
            pawn.Render = Color.FromArgb(model.Disableleg ? 254 : 255, originalRender.R, originalRender.G, originalRender.B);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

            if (model.FixedSkin >= 0)
            {
                pawn.AcceptInput("Skin", pawn, pawn, model.FixedSkin.ToString());
            }

            var defaultMask = model.DefaultMeshgroupMask;
            if (defaultMask.HasValue && defaultMask.Value != 0)
            {
                var sceneNode = pawn.CBodyComponent?.SceneNode;
                if (sceneNode != null)
                {
                    var skeleton = sceneNode.GetSkeletonInstance();
                    if (skeleton.ModelState.MeshGroupMask != defaultMask.Value)
                    {
                        skeleton.ModelState.MeshGroupMask = defaultMask.Value;
                        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
                    }
                }
            }
        });
    }

    public Task SetModelNextServerFrame(CCSPlayerController player, Model? model, bool disableleg)
    {
        return Server.NextFrameAsync(() =>
        {
            var pawn = player.Pawn.Value!;
            var originalRender = pawn.Render;
            if (model == null)
            {
                var defaultModel = Service.GetMapDefaultModel(player);
                if (defaultModel != null)
                {
                    pawn.SetModel(defaultModel);

                }
                pawn.Render = Color.FromArgb(Config.DisableDefaultModelLeg ? 254 : 255, originalRender.R, originalRender.G, originalRender.B);
                Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

                return;
            }
            pawn.SetModel(model.Path);
            pawn.Render = Color.FromArgb(disableleg ? 254 : 255, originalRender.R, originalRender.G, originalRender.B);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

            if (model.FixedSkin != -1)
            {
                pawn.AcceptInput("Skin", pawn, pawn, model.FixedSkin.ToString());
            }
            else
            {
                pawn.AcceptInput("Skin", pawn, pawn, Service.GetSkinPreference(player, model).ToString());
            }

            if (model.Meshgroups.Count > 0)
            {
                var sceneNode = pawn.CBodyComponent?.SceneNode;
                if (sceneNode != null)
                {
                    var skeleton = sceneNode.GetSkeletonInstance();
                    ulong currentMask = skeleton.ModelState.MeshGroupMask;

                    ulong defaultMask = model.DefaultMeshgroupMask ?? currentMask;
                    Service.InitMeshgroupPreference(player, model, defaultMask);

                    ulong meshgroupmask = Utils.CalculateMeshgroupmask(Service.GetMeshgroupPreference(player, model).ToArray(), model.FixedMeshgroups);
                    if (meshgroupmask != 0 && meshgroupmask != currentMask)
                    {
                        skeleton.ModelState.MeshGroupMask = meshgroupmask;
                        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
                    }
                }
            }

        });
    }
}
