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

    // mm-cs2menus bridge. Loaded in Load(), disposed in Unload(). null when
    // cs2menus isn't installed — menu features gracefully degrade in that case.
    private Cs2MenusBridge? _menus;

    // Ticks left before we report whether the native cs2menus plugin came up.
    // -1 means "nothing to report".
    private const int MenusStartupGraceTicks = 600;
    private int _menusStartupTicks = -1;

    // True once the bridge was reported as ready, so that is logged only once.
    private bool _menusReadyLogged = false;

    // Slots whose menu is hidden while the player looks at the model they just
    // picked. The value counts the ticks that went by without an inspection
    // camera, see CheckHiddenMenus().
    private readonly ConcurrentDictionary<int, int> _hiddenMenus = new();

    // Ticks without an inspection camera after which the hidden menu comes back
    // on its own, for the model switches that are not followed by an inspection.
    private const int HiddenMenuGraceTicks = 120;

    private static PlayerModelChanger? _Instance { get; set; }

    // Background sync that keeps the catalogue directory up to date with the
    // workshop addons of the server. Cancelled in Unload().
    private MeshGroupCatalogueSync? _catalogueSync;

    // Set by the sync when it wrote catalogue files, handled on the game thread
    // in OnTick so that the models and the config file are only touched there.
    private int _catalogueRefreshPending;

    // Bots keep the model they got for the whole map. Assigning a new random
    // model (or skin) on every respawn makes clients stutter and can crash them,
    // see the "bot respawn" crash reports of the original plugin.
    private readonly ConcurrentDictionary<string, string> _botModelIndex = new();

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

        // mm-cs2menus: locate the native binary and hand it to the CSS bridge.
        //
        // The bridge's own LoadDefault() resolves the binary from
        // Server.GameDirectory and assumes that folder is the csgo/ one, but
        // CounterStrikeSharp reports the parent game/ directory, so that lookup
        // misses the file. Cs2MenusBootstrap resolves it over several roots.
        //
        // The native plugin finishes its own Load() a couple of seconds after
        // the managed plugins do, so cs2m_available() can still be 0 right here.
        // Availability is checked again when a menu is opened, so don't turn
        // the menus off because of it.
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
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawnEvent);
        RegisterListener<Listeners.OnTick>(OnTick);
        Inspection.InspectionEnded += OnInspectionEnded;
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            Service.ClearMapDefaultModel();
            _botModelIndex.Clear();
        });

        Utils.InitializeLangPrefix();

        RegisterEventHandler<EventPlayerActivate>((@event, info) =>
        {
            if (@event.Userid != null)
            {
                MenuManager.RemovePlayer(@event.Userid.Slot);
                _hiddenMenus.TryRemove(@event.Userid.Slot, out _);
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            if (@event.Userid != null)
            {
                MenuManager.RemovePlayer(@event.Userid.Slot);
                _hiddenMenus.TryRemove(@event.Userid.Slot, out _);
                // The props of the inspection are only used by their owner.
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

    /// <summary>
    /// Starts the background sync of the catalogue directory with the workshop
    /// addons. Without the CLI next to the plugin it stays off.
    /// </summary>
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
            // Read on the game thread: the value comes from the engine and the
            // sync runs on a background thread.
            CounterStrikeSharp.API.Server.GameDirectory,
            Logger,
            Config.MeshgroupCatalogueWorkshopIds,
            Config.MeshgroupCatalogueVpkDirs,
            () => Interlocked.Exchange(ref _catalogueRefreshPending, 1));
        _catalogueSync.Start();
    }

    /// <summary>
    /// The background sync wrote catalogue files. Runs on the game thread: the
    /// catalogue is read again and the models that had no mesh groups yet get
    /// them, so a model of a new addon needs no plugin reload.
    /// </summary>
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
        DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawnEvent);
        Inspection.InspectionEnded -= OnInspectionEnded;
        _hiddenMenus.Clear();

        // The entities of the inspection have to go, the plugin is unloaded.
        Inspection.Cleanup();

        // MANDATORY on hot-reload: cs2menus holds native function pointers into
        // this assembly for every menu we created. Dispose the bridge (which
        // destroys every menu it owns and frees those callback tokens) before
        // the assembly unloads, otherwise the server crashes.
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

    /// <summary>
    /// A message of the language file. A file that does not fit the arguments
    /// of the message does not break the command, see Utils.Message().
    /// </summary>
    public string Message(string key, params object[] args) => Utils.Message(Localizer, key, args);

    public void ReloadConfig()
    {
        var config = typeof(ConfigManager)
                   .GetMethod("Load")!
                   .MakeGenericMethod(typeof(ModelConfig))
                   .Invoke(null, new object[] { Path.GetFileName(ModuleDirectory) }) as IBasePluginConfig;
        OnConfigParsed((config as ModelConfig)!);
        Unload(true);
        Load(false);
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

        // The mesh groups of the models are read from the catalogue directory
        // of the server owner. Which of them the player may change is in the
        // mesh group config of the server owner.
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

        // The file with the mesh groups of the models of the configuration, so
        // that the server owner can choose which of them the player may change.
        MeshGroupConfig.Write(ModuleDirectory, config.Models.Values, meshGroupConfig, Logger);

        if (modelsWithMeshgroups > 0)
        {
            Logger.LogInformation($"[PlayerModelChanger] {modelsWithMeshgroups} of {config.Models.Count} models have mesh groups the player can change.");
        }

        // The player wears one model for both sides, so a model that is limited
        // to one side cannot be worn and is not offered.
        var sideLimited = config.Models.Values.Count(model => model.Side != Side.All);
        if (sideLimited > 0)
        {
            Logger.LogWarning($"[PlayerModelChanger] {sideLimited} models are limited to one side and are not offered: the player wears one model for both sides.");
        }

        Config = config;
    }

    public void OnTick()
    {
        // The background catalogue sync only writes files; the catalogue and
        // the models are refreshed here, on the game thread.
        if (Interlocked.Exchange(ref _catalogueRefreshPending, 0) == 1)
        {
            RefreshMeshGroupsFromCatalogue();
        }

        // mm-cs2menus owns its own input polling — no per-tick button scanning
        // here. We only keep ticking the third-person inspection camera.
        Inspection.UpdateCamera();
        CheckMenusStartup();
        CheckHiddenMenus();
    }

    // ===== Hidden menus =====

    /// <summary>
    /// Hide the player's menu while the third-person inspection shows the model
    /// they just picked. The menu is not ended, only hidden, so it comes back
    /// with the same cursor, the same marks and the same selection.
    /// </summary>
    private void HideMenuWhileInspecting(CCSPlayerController player)
    {
        if (!MenuManager.Available) return;
        MenuManager.SuspendMenu(player);
        _hiddenMenus[player.Slot] = 0;
    }

    /// <summary>
    /// The inspection is over, the player can go on with the menu.
    /// </summary>
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

    /// <summary>
    /// Not every model switch is followed by an inspection: a dead player, a
    /// random model or the inspection being turned off all leave the menu
    /// hidden with nothing to show it again. So the menu comes back on its own
    /// when no camera shows up for a while.
    /// </summary>
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
                // The model is being shown, the menu waits for the end of it.
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

    /// <summary>
    /// The native cs2menus plugin finishes loading a moment after the managed
    /// plugins do, so report the real state once it had the time to come up.
    /// The menus work either way: their availability is checked when a player
    /// opens one, not when this plugin loads.
    /// </summary>
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

    /// <summary>
    /// Report once that the bridge is fully usable. The native plugin finishes
    /// loading a moment after the managed plugins do, so this is the first
    /// moment we know that the menus really work.
    /// </summary>
    private void LogMenusReady()
    {
        if (_menusReadyLogged) return;
        _menusReadyLogged = true;
        Logger.LogInformation("[PlayerModelChanger] mm-cs2menus is ready, the model menus are enabled.");
    }

    // from https://github.com/Challengermode/cm-cs2-defaultskins/
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
                var modelindex = PickBotModelIndex(player, team);
                if (modelindex == null)
                {
                    return HookResult.Continue;
                }
                var botmodel = Service.GetModel(modelindex);
                if (botmodel == null)
                {
                    // The configured entry is invalid; forget the choice so the
                    // next spawn can pick a valid one.
                    _botModelIndex.TryRemove(BotKey(player, team), out _);
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

                // The player wears one model for both sides, so a model that is
                // not allowed any more is reset for both of them.
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
                    // A new life gets a new random model.
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

    private static string BotKey(CCSPlayerController player, CsTeam team)
        => $"{(player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1))}_{(int)team}";

    /// <summary>
    /// The model a bot keeps for this map. A random entry is rolled once, so a
    /// respawn does not switch the bot to another model: changing the model of a
    /// bot on every respawn makes clients stutter and crash.
    /// </summary>
    private string? PickBotModelIndex(CCSPlayerController player, CsTeam team)
    {
        var entries = team == CsTeam.Terrorist ? Config.ModelForBots.T : Config.ModelForBots.CT;
        if (entries.Count == 0) return null;

        var key = BotKey(player, team);
        if (_botModelIndex.TryGetValue(key, out var chosen)) return chosen;

        var valid = entries.Where(entry => entry == "@random" || Service.GetModel(entry) != null).ToList();
        if (valid.Count == 0) return null;

        var modelindex = valid[Random.Shared.Next(valid.Count)];
        if (modelindex == "@random")
        {
            var models = Service.GetAllModels();
            if (models.Count == 0) return null;
            modelindex = models[Random.Shared.Next(models.Count)].Index;
        }

        _botModelIndex[key] = modelindex;
        return modelindex;
    }

    /// <summary>
    /// Applies a model to a bot without the per-player skin and meshgroup storage
    /// of the players. The skin only changes when the model fixes it, the
    /// meshgroups are the default ones of the model.
    /// </summary>
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

            // A fixed skin is static and safe. A per-bot skin choice is not; it
            // caused clients to stutter and crash when the bot respawned.
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

            // The mesh groups the player picked, or the ones of the model when
            // the player has not picked anything yet. The mesh groups that
            // belong to the model itself (the view it is drawn in, the face of
            // the character) are not in the menu and keep the value the model
            // was made with.
            if (model.Meshgroups.Count > 0)
            {
                var sceneNode = pawn.CBodyComponent?.SceneNode;
                if (sceneNode != null)
                {
                    var skeleton = sceneNode.GetSkeletonInstance();
                    ulong currentMask = skeleton.ModelState.MeshGroupMask;

                    // The default of the model is known from the catalogue. The
                    // mask of the pawn is only used for the models that are
                    // not in it.
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
