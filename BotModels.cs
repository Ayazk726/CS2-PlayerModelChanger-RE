using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Drawing;

namespace PlayerModelChanger;

public partial class PlayerModelChanger
{
    private const int BotSpawnSettleTicks = 16;

    private sealed record BotModelChoice(CsTeam Team, string? ModelIndex);
    private sealed record PendingBotModel(CCSPlayerController Controller, uint Pawn, CsTeam Team, int EarliestTick);

    private readonly Dictionary<uint, BotModelChoice> _botModelChoices = new();
    private readonly Dictionary<uint, PendingBotModel> _pendingBotModels = new();
    private CCSGameRulesProxy? _botGameRulesProxy;
    private bool _botModelsActive;
    private bool _waitingForHalftimeRound;
    private bool _engineSwitchingTeams;
    private int _botModelsResumeTick;

    private void ResetBotModels(bool active)
    {
        _pendingBotModels.Clear();
        _botModelChoices.Clear();
        _botGameRulesProxy = null;
        _botModelsActive = active;
        _waitingForHalftimeRound = false;
        _engineSwitchingTeams = false;
        _botModelsResumeTick = 0;
    }

    [GameEventHandler]
    public HookResult OnBotModelStartHalftime(EventStartHalftime @event, GameEventInfo info)
    {
        if (Enable && _botModelsActive)
        {
            _pendingBotModels.Clear();
            _waitingForHalftimeRound = true;
            Logger.LogInformation("[PlayerModelChanger] Halftime started. Pending bot model changes were cleared.");
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBotModelRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_waitingForHalftimeRound)
        {
            _waitingForHalftimeRound = false;
            _botModelsResumeTick = Server.TickCount + BotSpawnSettleTicks;
            Logger.LogInformation("[PlayerModelChanger] Halftime round started. Bot model changes will resume after spawn settles.");
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBotModelPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (_botModelsActive && @event.Userid is { IsValid: true } player)
        {
            var controller = player.EntityHandle.Raw;
            if (@event.Disconnect)
            {
                _pendingBotModels.Remove(controller);
                _botModelChoices.Remove(controller);
            }
            else if (_pendingBotModels.TryGetValue(controller, out var pending) && (int)pending.Team != @event.Team)
            {
                _pendingBotModels.Remove(controller);
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBotModelPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (_pendingBotModels.Count != 0 && @event.Userid is { IsValid: true } player)
        {
            _pendingBotModels.Remove(player.EntityHandle.Raw);
        }

        return HookResult.Continue;
    }

    private void QueueBotModelForSpawn(CCSPlayerController player, CsTeam team)
    {
        if (!_botModelsActive || !player.PlayerPawn.IsValid ||
            (Config.ModelForBots.CT.Count == 0 && Config.ModelForBots.T.Count == 0))
        {
            return;
        }

        var controller = player.EntityHandle.Raw;
        _pendingBotModels[controller] = new PendingBotModel(
            player, player.PlayerPawn.Raw, team, Server.TickCount + BotSpawnSettleTicks);
    }

    private void ApplyPendingBotModels()
    {
        if (!_botModelsActive || !Enable ||
            (_pendingBotModels.Count == 0 && !_waitingForHalftimeRound && !_engineSwitchingTeams))
        {
            return;
        }

        if (_botGameRulesProxy is not { IsValid: true })
        {
            _botGameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        }

        var rules = _botGameRulesProxy?.GameRules;
        if (rules == null || rules.SwitchingTeamsAtRoundReset)
        {
            _engineSwitchingTeams = true;
            return;
        }

        var tick = Server.TickCount;
        if (_engineSwitchingTeams)
        {
            _engineSwitchingTeams = false;
            _botModelsResumeTick = Math.Max(_botModelsResumeTick, tick + BotSpawnSettleTicks);
        }

        if (_waitingForHalftimeRound || tick < _botModelsResumeTick)
        {
            return;
        }

        foreach (var (controller, pending) in _pendingBotModels.ToArray())
        {
            if (tick < pending.EarliestTick ||
                !_pendingBotModels.TryGetValue(controller, out var current) ||
                !ReferenceEquals(pending, current))
            {
                continue;
            }

            _pendingBotModels.Remove(controller);
            try
            {
                ApplyBotModelForSpawn(controller, pending);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[PlayerModelChanger] Could not apply a bot model for controller {Controller}.", controller);
            }
        }
    }

    private void ApplyBotModelForSpawn(uint controller, PendingBotModel pending)
    {
        var player = pending.Controller;
        if (!player.IsValid || !player.IsBot || !player.PawnIsAlive ||
            (CsTeam)player.TeamNum != pending.Team || player.PlayerPawn.Raw != pending.Pawn ||
            player.PlayerPawn.Value is not { IsValid: true } pawn || (CsTeam)pawn.TeamNum != pending.Team)
        {
            return;
        }

        _botModelChoices.TryGetValue(controller, out var choice);
        bool changedTeam = choice != null && choice.Team != pending.Team;
        if (choice == null || changedTeam)
        {
            choice = new BotModelChoice(pending.Team, PickBotModelIndex(pending.Team));
            _botModelChoices[controller] = choice;
        }

        var model = choice.ModelIndex == null ? null : Service.GetModel(choice.ModelIndex);
        if (model == null)
        {
            return;
        }

        var currentModel = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
        if (string.IsNullOrEmpty(currentModel))
        {
            return;
        }

        if (!string.Equals(currentModel, model.Path, StringComparison.OrdinalIgnoreCase))
        {
            pawn.SetModel(model.Path);
            if (changedTeam)
            {
                Logger.LogInformation("[PlayerModelChanger] Bot {Bot} changed to {Team} model {Model}.",
                    player.PlayerName, pending.Team, model.Path);
            }
        }

        var originalRender = pawn.Render;
        var renderAlpha = model.Disableleg ? 254 : 255;
        if (originalRender.A != renderAlpha)
        {
            pawn.Render = Color.FromArgb(renderAlpha, originalRender.R, originalRender.G, originalRender.B);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        }

        if (model.FixedSkin >= 0)
        {
            pawn.AcceptInput("Skin", pawn, pawn, model.FixedSkin.ToString());
        }

        if (model.DefaultMeshgroupMask is { } defaultMask && defaultMask != 0)
        {
            var skeleton = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance();
            if (skeleton != null && skeleton.ModelState.MeshGroupMask != defaultMask)
            {
                skeleton.ModelState.MeshGroupMask = defaultMask;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
            }
        }
    }

    private string? PickBotModelIndex(CsTeam team)
    {
        var entries = team == CsTeam.Terrorist ? Config.ModelForBots.T : Config.ModelForBots.CT;
        var valid = entries.Where(entry => entry == "@random" || Service.GetModel(entry) != null).ToList();
        if (valid.Count == 0)
        {
            return null;
        }

        var modelIndex = valid[Random.Shared.Next(valid.Count)];
        if (modelIndex != "@random")
        {
            return modelIndex;
        }

        var models = Service.GetAllModels();
        return models.Count == 0 ? null : models[Random.Shared.Next(models.Count)].Index;
    }
}
