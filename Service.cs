using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Localization;

namespace PlayerModelChanger;

struct MapDefaultModel
{
    public string? T { get; set; }
    public string? CT { get; set; }
}

public class ModelService
{

    private ModelConfig _Config;
    public IStorage _Storage;

    private DefaultModelManager _DefaultModelManager;

    private IStringLocalizer _Localizer;

    private ModelCacheManager _CacheManager;

    private Dictionary<ulong, MapDefaultModel> _MapDefaultModels = new Dictionary<ulong, MapDefaultModel>();

    private Dictionary<ulong, long> _ModelChangeCooldown = new Dictionary<ulong, long>();

    private Dictionary<string, Model> _pickedModels = new Dictionary<string, Model>();

    public ModelService(ModelConfig Config, IStorage storage, IStringLocalizer localizer, DefaultModelManager defaultModelManager)
    {
        this._Config = Config;
        this._Storage = storage;
        this._Localizer = localizer;

        this._DefaultModelManager = defaultModelManager;
        _CacheManager = new ModelCacheManager(storage);
        _CacheManager.ResyncCache();
    }

    public static void InitializeModel(string key, Model model)
    {
        model.Index = key;
        if (model.Name == "")
        {
            model.Name = model.Index;
        }
    }
    public void ReloadConfig(string ModuleDirectory, ModelConfig config)
    {
        this._Config = config;
        _DefaultModelManager.ReloadConfig(ModuleDirectory, this);
    }
    public void ResyncCache()
    {
        _CacheManager.ResyncCache();
    }
    public void SetAllTModels(string tmodel, bool permissionBypass)
    {
        _CacheManager.SetAllTModels(tmodel, permissionBypass);
        _Storage.SetAllTModel(tmodel, permissionBypass);
    }
    public void SetAllCTModels(string ctmodel, bool permissionBypass)
    {
        _CacheManager.SetAllCTModels(ctmodel, permissionBypass);
        _Storage.SetAllCTModel(ctmodel, permissionBypass);
    }

    public void SetAllModels(string tmodel, string ctmodel, bool permissionBypass)
    {
        _CacheManager.SetAllModels(tmodel, ctmodel, permissionBypass);
        _Storage.SetAllTModel(tmodel, permissionBypass);
        _Storage.SetAllCTModel(ctmodel, permissionBypass);
    }
    public void SetPlayerModel(ulong steamid, string? modelIndex, Side side, bool permissionBypass, bool silent = false)
    {
        modelIndex = modelIndex != null ? modelIndex : "";
        _CacheManager.SetPlayerModel(steamid, modelIndex, side, true);
        RollPickedModel(steamid, side, modelIndex);
        Utils.ExecuteSide(side,
            () =>
            {
                _Storage.SetPlayerTModel(steamid, modelIndex, permissionBypass);
                _Storage.SetPlayerCTModel(steamid, modelIndex, permissionBypass);
            },
            () =>
            {
                _Storage.SetPlayerTModel(steamid, modelIndex, permissionBypass);
            },
            () =>
            {
                _Storage.SetPlayerCTModel(steamid, modelIndex, permissionBypass);
            }
        );
        if (!_Config.DisableInstantChange)
        {
            var player = Utilities.GetPlayerFromSteamId(steamid);
            if (player == null) { return; }
            if (Utils.CanPlayerSetModelInstantly(player, side))
            {
                var model = GetPlayerModel(player, side);
                Utils.InstantUpdatePlayer(player, model, _Config.Inspection.Enable, silent);
            }
        }
    }

    public void SetPlayerAllModel(ulong steamid, string? tModel, string? ctModel, bool permissionBypass, bool inspection = true, bool silent = false)
    {
        tModel = tModel != null ? tModel : "";
        ctModel = ctModel != null ? ctModel : "";
        _CacheManager.SetPlayerModel(steamid, tModel, Side.T, permissionBypass);
        _CacheManager.SetPlayerModel(steamid, ctModel, Side.CT, permissionBypass);
        _Storage.SetPlayerTModel(steamid, tModel, permissionBypass);
        _Storage.SetPlayerCTModel(steamid, ctModel, permissionBypass);
        RollPickedModel(steamid, Side.T, tModel);
        RollPickedModel(steamid, Side.CT, ctModel);
        if (!_Config.DisableInstantChange)
        {
            var player = Utilities.GetPlayerFromSteamId(steamid);
            if (player == null) { return; }
            if (Utils.CanPlayerSetModelInstantly(player, Side.All))
            {
                var side = player.Team == CsTeam.Terrorist ? Side.T : Side.CT;
                var model = GetPlayerModel(player, side);
                Utils.InstantUpdatePlayer(player, model, inspection && _Config.Inspection.Enable && model?.Index != "@random", silent);
            }
        }
    }

    public void SetPlayerModel(ulong steamid, string? modelIndex, CsTeam team, bool permissionBypass, bool silent = false)
    {
        var side = team == CsTeam.Terrorist ? Side.T : Side.CT;
        SetPlayerModel(steamid, modelIndex != null ? modelIndex : "", side, permissionBypass, silent);
    }

    public int GetModelCount() => _Config.Models.Count;
    public List<ulong> GetAllPlayers() => _CacheManager.GetAllPlayers();
    public List<Model> GetAllModels() => _Config.Models.Values.ToList();
    public bool ExistModel(string modelIndex) => _Config.Models.ContainsKey(modelIndex);
    public Model? FindModel(string modelName) => _Config.Models.Values.FirstOrDefault(model => model?.Name == modelName, null);
    public Model? GetModel(string modelIndex) => _Config.Models.TryGetValue(modelIndex, out var value) ? value : null;

    public bool CanPlayerApplyModel(CCSPlayerController player, Side side, Model model)
    {
        return Utils.PlayerHasPermission(player, model.Permissions, model.PermissionsOr) && // permission
            (model.Side == Side.All || model.Side == side); // side
    }

    public List<Model> GetAllAppliableModels(CCSPlayerController player, Side side)
    {
        return _Config.Models.Values.Where(model => CanPlayerApplyModel(player, side, model)).ToList();
    }

    public Tuple<bool, bool> CheckAndReplaceModel(CCSPlayerController player)
    {
        var steamid = player.AuthorizedSteamID!.SteamId64!;
        var modelCache = _CacheManager.GetPlayerModelCache(player);
        var defaultTModel = _DefaultModelManager.GetPlayerDefaultModel(player, Side.T);
        var defaultCTModel = _DefaultModelManager.GetPlayerDefaultModel(player, Side.CT);
        if (modelCache == null) // player first time join
        {
            SetPlayerAllModel(steamid, "@default", "@default", false, false);
            return new Tuple<bool, bool>(false, false);
        }
        var tValid = CheckModel(player, Side.T, modelCache, defaultTModel);
        var ctValid = CheckModel(player, Side.CT, modelCache, defaultCTModel);
        if (!tValid && !ctValid)
        {
            SetPlayerAllModel(steamid, "@default", "@default", false);
            return new Tuple<bool, bool>(true, true);
        }
        else if (!tValid)
        {
            SetPlayerModel(steamid, "@default", Side.T, false);
            return new Tuple<bool, bool>(true, false);
        }
        else if (!ctValid)
        {
            SetPlayerModel(steamid, "@default", Side.CT, false);
            return new Tuple<bool, bool>(false, true);
        }
        return new Tuple<bool, bool>(false, false);
    }

    public bool CheckModel(CCSPlayerController player, Side side, ModelCache? modelCache, DefaultModel? defaultModel)
    {
        if (modelCache == null)
        {
            return false;
        }
        var modelIndex = side == Side.T ? modelCache!.t_model : modelCache!.ct_model;
        if ((side == Side.T && modelCache!.t_permission_bypass) || (side == Side.CT && modelCache!.ct_permission_bypass))
        {
            return true;
        }
        if (defaultModel != null && defaultModel.force && modelIndex != "@default")
        {
            return false;
        }
        if (modelIndex == "@random" && _Config.DisableRandomModel)
        {
            return false;
        }
        if (modelIndex == "" || modelIndex == "@random" || modelIndex == "@default")
        {
            return true;
        }

        var model = GetModel(modelIndex!);
        if (model == null)
        {
            return false;
        }
        return CanPlayerApplyModel(player, side, model);
    }

    private bool IsPlayerInCooldown(CCSPlayerController player)
    {
        if (_ModelChangeCooldown.ContainsKey(player.SteamID))
        {
            var lastTime = _ModelChangeCooldown[player.SteamID];
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastTime < (_Config.ModelChangeCooldownSecond * 1000))
            {
                player.PrintToChat(_Localizer["command.model.cooldown"]);
                return true;
            }
        }
        return false;
    }

    public bool SetPlayerModelWithCheck(CCSPlayerController player, string modelIndex, Side side, bool silent = false)
    {
        if (IsPlayerInCooldown(player))
        {
            return false;
        }

        var isSpecial = modelIndex == "" || modelIndex == "@random";

        if (modelIndex == "@default")
        {
            Utils.ExecuteSide(side,
                () => SetPlayerAllModel(player!.AuthorizedSteamID!.SteamId64, "@default", "@default", false, silent: silent),
                () => SetPlayerModel(player!.AuthorizedSteamID!.SteamId64, "@default", side, false, silent),
                () => SetPlayerModel(player!.AuthorizedSteamID!.SteamId64, "@default", side, false, silent)
            );
            if (_Config.DisableInstantChange || !Utils.CanPlayerSetModelInstantly(player, side))
            {
                player.PrintToChat(_Localizer["command.model.success"]);
            }
            return true;
        }

        var model = GetModel(modelIndex);
        if (!isSpecial)
        {
            if (model == null)
            {
                player.PrintToChat(Utils.Message(_Localizer, "command.model.notfound", modelIndex));
                return false;
            }

            if (!Utils.PlayerHasPermission(player, model.Permissions, model.PermissionsOr))
            {
                player.PrintToChat(Utils.Message(_Localizer, "model.nopermission", modelIndex));
                return false;
            }

            if (!GetAllAppliableModels(player, side).Contains(model))
            {
                player.PrintToChat(Utils.Message(_Localizer, "model.wrongteam", modelIndex));
                return false;
            }

        }

        var steamid = player!.AuthorizedSteamID!.SteamId64;
        SetPlayerModel(steamid, modelIndex, side, false, silent);
        _ModelChangeCooldown[steamid] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_Config.DisableInstantChange || !Utils.CanPlayerSetModelInstantly(player, side))
        {
            player.PrintToChat(_Localizer["command.model.success"]);
        }
        return true;
    }

    public Model? GetRandomModel(CCSPlayerController player)
    {
        return GetPickedModel(player, Side.All, "@random", GetAllAppliableModels(player, Side.All));
    }

    private Model? GetPickedModel(CCSPlayerController player, Side side, string entry, List<Model> models)
    {
        if (models.Count == 0)
        {
            return null;
        }

        var steamid = player.AuthorizedSteamID?.SteamId64;
        if (steamid == null)
        {
            return models[Random.Shared.Next(models.Count)];
        }

        var key = PickedKey(steamid.Value, side, entry);
        if (!_pickedModels.TryGetValue(key, out var model))
        {
            model = models[Random.Shared.Next(models.Count)];
            _pickedModels[key] = model;
        }
        return model;
    }

    private static string PickedKey(ulong steamid, Side side, string entry) => $"{steamid}_{side}_{entry}";

    public void RollPickedModel(ulong steamid, Side side, string entry)
    {
        if (entry != "@random" && entry != "@default")
        {
            return;
        }

        if (entry == "@random")
        {
            _pickedModels.Remove(PickedKey(steamid, Side.All, entry));
            return;
        }

        if (side == Side.All)
        {
            _pickedModels.Remove(PickedKey(steamid, Side.T, entry));
            _pickedModels.Remove(PickedKey(steamid, Side.CT, entry));
            return;
        }
        _pickedModels.Remove(PickedKey(steamid, side, entry));
    }

    public void RollPickedModels(CCSPlayerController player)
    {
        var steamid = player.AuthorizedSteamID?.SteamId64;
        if (steamid == null)
        {
            return;
        }

        var side = player.Team == CsTeam.Terrorist ? Side.T : Side.CT;
        RollPickedModel(steamid.Value, side, "@random");
        RollPickedModel(steamid.Value, side, "@default");
    }

    public bool HasDefaultModel(CCSPlayerController player, Side side)
    {
        if (side == Side.All)
        {
            return _DefaultModelManager.GetPlayerDefaultModel(player, Side.T) != null
                || _DefaultModelManager.GetPlayerDefaultModel(player, Side.CT) != null;
        }
        return _DefaultModelManager.GetPlayerDefaultModel(player, side) != null;
    }

    public Model? GetPlayerModel(CCSPlayerController player, Side side)
    {
        if (side == Side.All)
        {
            var tModel = GetPlayerModel(player, Side.T);
            var ctModel = GetPlayerModel(player, Side.CT);
            if (tModel?.Index == ctModel?.Index)
            {
                return tModel;
            }
            return player.Team == CsTeam.Terrorist ? tModel : ctModel;
        }
        var modelCache = _CacheManager.GetPlayerModelCache(player);
        var modelIndex = side == Side.T ? modelCache?.t_model : modelCache?.ct_model;
        if (modelIndex == null || modelIndex == "")
        {
            return null;
        }

        if (modelIndex == "@default")
        {
            var defaultModel = _DefaultModelManager.GetPlayerDefaultModel(player, side);
            if (defaultModel == null || defaultModel.index.Count == 0)
            {
                return null;
            }
            var models = defaultModel.index
                .Select(index => index == "" ? null : GetModel(index))
                .Where(model => model != null)
                .Select(model => model!)
                .ToList();
            if (models.Count == 0)
            {
                return null;
            }
            return GetPickedModel(player, side, "@default", models);
        }
        if (modelIndex == "@random")
        {
            return GetRandomModel(player);
        }
        return GetModel(modelIndex);
    }

    public string GetPlayerModelName(CCSPlayerController player)
    {
        var modelCache = _CacheManager.GetPlayerModelCache(player);
        string? modelIndex = modelCache?.t_model;
        if (string.IsNullOrEmpty(modelIndex))
        {
            modelIndex = modelCache?.ct_model;
        }
        if (modelIndex == null || modelIndex == "")
        {
            return _Localizer["model.none"];
        }
        if (modelIndex == "@random")
        {
            return _Localizer["modelmenu.random"];
        }
        if (modelIndex == "@default")
        {
            return _Localizer["modelmenu.default"];
        }
        else
        {
            var model = GetModel(modelIndex);
            if (model == null)
            {
                return _Localizer["model.none"];
            }
            return model.Name;
        }
    }

    public List<int> GetMeshgroupPreference(CCSPlayerController player, Model model)
    {
        return _CacheManager.GetMeshgroupPreference(player.AuthorizedSteamID!.SteamId64, model.Index);
    }

    public void AddMeshgroupPreference(CCSPlayerController player, Model model, int meshgroup, bool update = true)

    {

        _CacheManager.AddMeshgroupPreference(player.AuthorizedSteamID!.SteamId64, model.Index, meshgroup);
        _Storage.AddMeshgroupPreference(player.AuthorizedSteamID!.SteamId64, model.Index, meshgroup);
        if (update) MeshgroupUpdate(player);
    }

    public void SetMeshgroupPreference(CCSPlayerController player, Model model, List<int> meshgroups, bool update = true)
    {
        var old = new List<int>(GetMeshgroupPreference(player, model));
        old.Sort();
        foreach (var fixedGroup in model.FixedMeshgroups.Keys)
        {
            old.Remove(fixedGroup);
        }
        if (old.Count > 0)
        {
            old.RemoveAt(old.Count - 1); // preserve the 'autodefault' meshgroup
        }
        foreach (var meshgroup in old)
        {
            RemoveMeshgroupPreference(player, model, meshgroup, false);
        }

        foreach (var meshgroup in meshgroups)
        {
            AddMeshgroupPreference(player, model, meshgroup, false);
        }
        if (update) MeshgroupUpdate(player);
    }

    public void RemoveMeshgroupPreference(CCSPlayerController player, Model model, int meshgroup, bool update = true)
    {
        _CacheManager.RemoveMeshgroupPreference(player.AuthorizedSteamID!.SteamId64, model.Index, meshgroup);
        _Storage.RemoveMeshgroupPreference(player.AuthorizedSteamID!.SteamId64, model.Index, meshgroup);
        if (update) MeshgroupUpdate(player);
    }

    public bool HasMeshgroupPreference(CCSPlayerController player, Model model, int meshgroup)
    {
        return GetMeshgroupPreference(player, model).Contains(meshgroup);
    }

    public bool InitMeshgroupPreference(CCSPlayerController player, Model model, ulong meshgroupmask)
    {
        if (model.Meshgroups.Count == 0)
        {
            return true;
        }
        if (GetMeshgroupPreference(player, model).Count > 0)
        {
            return false;
        }
        var meshgroups = new List<int>();
        for (int i = 0; i < 64; i++)
        {
            if ((meshgroupmask & (1UL << i)) != 0)
            {
                meshgroups.Add(i);
            }
        }
        var steamid = player.AuthorizedSteamID!.SteamId64;
        foreach (var meshgroup in meshgroups)
        {
            _CacheManager.AddMeshgroupPreference(steamid, model.Index, meshgroup);
            _Storage.AddMeshgroupPreference(steamid, model.Index, meshgroup);
        }
        return true;
    }

    public void MeshgroupUpdate(CCSPlayerController player)
    {
        if (Utils.CanPlayerSetModelInstantly(player, Side.All))
        {
            var model = GetPlayerModel(player, Side.All);
            Utils.InstantUpdatePlayer(player, model, _Config.Inspection.Enable);
        }
    }

    public bool MapDefaultModelInitialized(CCSPlayerController player)
    {
        if (!_MapDefaultModels.ContainsKey(player.AuthorizedSteamID!.SteamId64))
        {
            return false;
        }
        MapDefaultModel model = _MapDefaultModels[player.AuthorizedSteamID!.SteamId64];
        if (player.Team == CsTeam.None || player.Team == CsTeam.Spectator)
        {
            return true;
        }
        return player.Team == CsTeam.Terrorist ? model.T != null : model.CT != null;
    }
    public void SetMapDefaultModel(CCSPlayerController player, string model)
    {
        var m = _MapDefaultModels.GetValueOrDefault(player.AuthorizedSteamID!.SteamId64, new MapDefaultModel());
        if (player.Team == CsTeam.CounterTerrorist)
        {
            m.CT = model;
        }
        else if (player.Team == CsTeam.Terrorist)
        {
            m.T = model;
        }
        _MapDefaultModels[player.AuthorizedSteamID!.SteamId64] = m;
    }

    public int GetSkinPreference(CCSPlayerController player, Model model)
    {
        return _CacheManager.GetSkinPreference(player.AuthorizedSteamID!.SteamId64, model.Index);
    }

    public void SetSkinPreference(CCSPlayerController player, Model model, int skin, bool update = true)
    {
        _CacheManager.UpdateSkinPerference(player.AuthorizedSteamID!.SteamId64, model.Index, skin);
        _Storage.UpdateSkinPerference(player.AuthorizedSteamID!.SteamId64, model.Index, skin);
        if (update)
        {
            var pawn = player.PlayerPawn.Value!;
            pawn.AcceptInput("Skin", pawn, pawn, skin.ToString());
        }
    }

    public string? GetMapDefaultModel(CCSPlayerController player)
    {
        if (!_MapDefaultModels.ContainsKey(player.AuthorizedSteamID!.SteamId64))
        {
            return null;
        }
        if (player.Team == CsTeam.CounterTerrorist)
        {
            return _MapDefaultModels[player.AuthorizedSteamID!.SteamId64].CT;
        }
        else if (player.Team == CsTeam.Terrorist)
        {
            return _MapDefaultModels[player.AuthorizedSteamID!.SteamId64].T;
        }
        return null;
    }
    public void ClearMapDefaultModel()
    {
        _MapDefaultModels.Clear();
    }
}
