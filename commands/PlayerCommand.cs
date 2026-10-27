using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace PlayerModelChanger;

public partial class PlayerModelChanger
{

    [ConsoleCommand("css_model", "Show your model.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void ChangeModelCommand(CCSPlayerController player, CommandInfo commandInfo)
    {

        if (commandInfo.ArgCount == 1)
        {
            commandInfo.ReplyToCommand(Message("player.currentmodel", Service.GetPlayerModelName(player)));
            commandInfo.ReplyToCommand(Localizer["command.model.hint1"]);
            commandInfo.ReplyToCommand(Localizer["command.model.hint2"]);
            return;
        }

        if (Config.DisablePlayerSelection)
        {
            return;
        }
        if (!Utils.PlayerHasBasicPermission(player))
        {
            commandInfo.ReplyToCommand(Localizer["model.nochangepermission"]);
            return;
        }

        var modelIndex = commandInfo.GetArg(1);

        if (modelIndex != "@random" && !Service.ExistModel(modelIndex))
        {
            var model = Service.FindModel(modelIndex);
            if (model == null)
            {
                commandInfo.ReplyToCommand(Message("command.model.notfound", modelIndex));
                return;
            }
            else
            {
                modelIndex = model.Index;
            }
        }

        // The optional side argument picks which side gets the model, "all" by
        // default.
        var side = Side.All;
        if (commandInfo.ArgCount > 2)
        {
            var parsedSide = commandInfo.GetArg(2).ToSide();
            if (parsedSide == null)
            {
                commandInfo.ReplyToCommand(Localizer["command.incorrectusage"]);
                return;
            }
            side = parsedSide.Value;
        }

        var defaultModel = DefaultModelManager.GetPlayerDefaultModel(player!, side);
        if (defaultModel != null && defaultModel.force)
        {
            commandInfo.ReplyToCommand(Localizer["model.nochangepermission"]);
            return;
        }

        Service.SetPlayerModelWithCheck(player, modelIndex, side);
    }

    [ConsoleCommand("css_md", "Select models.")]
    [ConsoleCommand("css_models", "Select models.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void GetAllModelsCommand(CCSPlayerController player, CommandInfo commandInfo)
    {

        if (Config.DisablePlayerSelection)
        {
            return;
        }
        if (!Utils.PlayerHasBasicPermission(player))
        {
            commandInfo.ReplyToCommand(Localizer["model.nochangepermission"]);
            return;
        }

        // "!md <side>" opens the model list of that side directly, without the
        // side picker of the root menu.
        if (commandInfo.ArgCount > 1)
        {
            var side = commandInfo.GetArg(1).ToSide();
            if (side == null)
            {
                commandInfo.ReplyToCommand(Localizer["command.incorrectusage"]);
                return;
            }
            if (side.Value != Side.All)
            {
                OpenSelectModelMenu(player, side.Value);
                return;
            }
        }

        OpenSelectRootMenu(player);
    }

    [ConsoleCommand("css_mesh", "Select models.")]
    [ConsoleCommand("css_mg", "Select models.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void MeshgroupCommand(CCSPlayerController player, CommandInfo commandInfo)
    {
        if (Config.DisablePlayerSelection)
        {
            return;
        }
        if (!Utils.PlayerHasBasicPermission(player))
        {
            commandInfo.ReplyToCommand(Localizer["model.nochangepermission"]);
            return;
        }
        OpenSelectCustomizationMenu(player);
    }

    [ConsoleCommand("css_skin", "Select skin.")]
    [ConsoleCommand("css_mat", "Select skin.")]
    [ConsoleCommand("css_materialgroup", "Select skin.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void SkinCommand(CCSPlayerController player, CommandInfo commandInfo)
    {
        if (Config.DisablePlayerSelection)
        {
            return;
        }
        if (!Utils.PlayerHasBasicPermission(player))
        {
            commandInfo.ReplyToCommand(Localizer["model.nochangepermission"]);
            return;
        }
        OpenSelectSkinMenu(player);
    }
}
