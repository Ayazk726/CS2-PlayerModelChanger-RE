using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using Cs2Menus;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

public partial class PlayerModelChanger
{
    private readonly ConcurrentDictionary<int, Cs2Menu> _sideMenus = new();

    private void ConfigureMenu(Cs2Menu menu)
    {
        menu.SetCloseOnSelect(false);
        menu.SetMenuKey(MenuNavAction.Up, MenuButton.W);
        menu.SetMenuKey(MenuNavAction.Down, MenuButton.S);
        menu.SetMenuKey(MenuNavAction.Select, MenuButton.D);
        menu.SetMenuKey(MenuNavAction.Back, MenuButton.A);
    }

    private Cs2Menu? TryCreateMenu(string title, Action<Cs2Menu, int, int>? onSelect = null)
    {
        if (_menus == null || !_menus.Available)
        {
            return null;
        }

        try
        {
            var menu = _menus.CreateMenu(MenuType.Panorama, title, onSelect);
            LogMenusReady();
            return menu;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PlayerModelChanger] Could not create a cs2menus menu: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Cs2Menu? GetSelectRootMenu(CCSPlayerController player)
    {
        var menu = TryCreateMenu(Localizer["modelmenu.root"], (m, slot, item) => OnSelectRootItem(m, slot, item, player));
        if (menu == null) return null;
        ConfigureMenu(menu);

        var sideMenu = GetSelectSideMenu(player);
        if (sideMenu != null)
        {
            menu.AddSubMenu(Localizer["modelmenu.select"], sideMenu, "sides");
        }

        menu.AddItem(CurrentModelText(player), "current");

        return menu;
    }

    public Cs2Menu? GetSelectSideMenu(CCSPlayerController player)
    {
        var sides = new List<Side>();
        var tDefault = DefaultModelManager.GetPlayerDefaultModel(player, Side.T);
        var ctDefault = DefaultModelManager.GetPlayerDefaultModel(player, Side.CT);
        if (tDefault == null || !tDefault.force)
        {
            sides.Add(Side.T);
        }
        if (ctDefault == null || !ctDefault.force)
        {
            sides.Add(Side.CT);
        }
        if (sides.Count == 2)
        {
            sides.Insert(0, Side.All);
        }
        if (sides.Count == 0)
        {
            return null;
        }

        var menu = TryCreateMenu(Localizer["modelmenu.selectside"], (m, slot, item) => { });
        if (menu == null) return null;
        ConfigureMenu(menu);
        _sideMenus[player.Slot] = menu;

        foreach (var side in sides)
        {
            var modelMenu = GetSelectModelMenu(player, side);
            if (modelMenu == null) continue;

            var sideName = $"{Localizer[$"side.{side.ToName()}"]}";
            var currentModel = Service.GetPlayerModel(player, side);
            var text = currentModel != null ? $"{sideName}: {currentModel.Name}" : sideName;
            menu.AddSubMenu(text, modelMenu, $"side_{side.ToName()}");
        }

        return menu;
    }

    private void OnSelectRootItem(Cs2Menu menu, int slot, int item, CCSPlayerController player)
    {
        if (!player.IsValid) return;
        if (menu.GetItemInfo(item) != "current") return;

        var currentModel = Service.GetPlayerModel(player, Side.All);
        if (currentModel == null)
        {
            player.PrintToChat(Localizer["modelmenu.nomodel"]);
            return;
        }

        var customMenu = GetSelectCustomizationMenu(player, currentModel);
        if (customMenu == null)
        {
            player.PrintToChat(Localizer["modelmenu.nomeshgroup"]);
            return;
        }

        if (!MenuManager.Push(player, customMenu))
        {
            MenuManager.Display(player, customMenu, customMenu);
        }
    }

    private string CurrentModelText(CCSPlayerController player)
    {
        var currentModel = Service.GetPlayerModel(player, Side.All);
        return currentModel != null
            ? $"{Localizer["modelmenu.current"]}: {currentModel.Name}"
            : $"{Localizer["modelmenu.current"]}: {Localizer["model.none"]}";
    }

    public void OpenSelectRootMenu(CCSPlayerController player)
    {
        if (!MenuManager.Available)
        {
            player.PrintToChat(Localizer["modelmenu.menusunavailable"]);
            return;
        }

        var root = GetSelectRootMenu(player);
        if (root == null)
        {
            player.PrintToChat(Localizer["modelmenu.forced"]);
            return;
        }
        MenuManager.Display(player, root, root);
    }

    public void OpenSelectModelMenu(CCSPlayerController player, Side side)
    {
        if (!MenuManager.Available)
        {
            player.PrintToChat(Localizer["modelmenu.menusunavailable"]);
            return;
        }

        var defaultModel = DefaultModelManager.GetPlayerDefaultModel(player, side);
        if (defaultModel != null && defaultModel.force)
        {
            player.PrintToChat(Localizer["modelmenu.forced"]);
            return;
        }

        var menu = GetSelectModelMenu(player, side);
        if (menu == null)
        {
            player.PrintToChat(Localizer["modelmenu.forced"]);
            return;
        }
        MenuManager.Display(player, menu, menu);
    }

    public Cs2Menu? GetSelectModelMenu(CCSPlayerController player, Side side)
    {
        var currentModel = Service.GetPlayerModel(player, side);
        var sideName = $"{Localizer[$"side.{side.ToName()}"]}";
        var currentName = currentModel == null ? $"{Localizer["model.none"]}" : currentModel.Name;
        var title = Message("modelmenu.title", sideName, currentName);

        var menu = TryCreateMenu(title, (m, slot, item) => OnSelectModel(m, slot, item, player, side));
        if (menu == null) return null;
        ConfigureMenu(menu);

        int itemIndex = 0;

        menu.AddItem(Localizer["modelmenu.unset"], "");
        itemIndex++;

        if (!Config.DisableRandomModel)
        {
            menu.AddItem(Localizer["modelmenu.random"], "@random");
            itemIndex++;
        }

        if (Service.HasDefaultModel(player, side))
        {
            menu.AddItem(Localizer["modelmenu.default"], "@default");
            itemIndex++;
        }

        foreach (var model in Service.GetAllAppliableModels(player, side))
        {
            if (model.HideInMenu) continue;
            var isSelected = currentModel != null && model.Index == currentModel.Index;
            menu.AddItem(isSelected ? $"★ {model.Name}" : model.Name, model.Index);

            if (isSelected && MenuManager.SupportsBadgesAndDialogs)
            {
                menu.SetItemHighlight(itemIndex, true);
            }

            itemIndex++;
        }

        return menu;
    }

    private void OnSelectModel(Cs2Menu menu, int slot, int itemIndex, CCSPlayerController player, Side side)
    {
        if (!player.IsValid) return;

        var modelIndex = menu.GetItemInfo(itemIndex);

        if (modelIndex == "@default"
            && MenuManager.Confirm(slot,
                Localizer["modelmenu.confirm.title"],
                Localizer["modelmenu.confirm.body"],
                Localizer["modelmenu.confirm.cancel"],
                Localizer["modelmenu.confirm.ok"],
                (confirmedSlot, confirmed) =>
                {
                    if (!confirmed) return;
                    if (!player.IsValid || !menu.IsValid) return;
                    ApplyModelSelection(player, menu, confirmedSlot, itemIndex, modelIndex, side);
                }))
        {
            return;
        }

        ApplyModelSelection(player, menu, slot, itemIndex, modelIndex, side);
    }

    private void ApplyModelSelection(CCSPlayerController player, Cs2Menu menu, int slot, int itemIndex, string modelIndex, Side side)
    {
        if (!player.IsValid) return;

        if (Service.SetPlayerModelWithCheck(player, modelIndex, side, silent: true))
        {
            var newModel = Service.GetPlayerModel(player, side);
            if (menu.IsValid)
            {
                var sideName = $"{Localizer[$"side.{side.ToName()}"]}";
                var currentName = newModel == null ? $"{Localizer["model.none"]}" : newModel.Name;
                menu.SetTitle(Message("modelmenu.title", sideName, currentName));

                RefreshModelMenuMarkers(menu, player, side);
            }

            var rootMenu = MenuManager.GetRootMenu(slot);
            if (rootMenu != null && !ReferenceEquals(rootMenu, menu) && rootMenu.IsValid)
            {
                UpdateCurrentModelItemText(rootMenu, player);
            }

            RefreshSideMenuTexts(player);

            HideMenuWhileInspecting(player);

            MenuManager.Notify(player, slot, Message("modelmenu.applied",
                newModel == null ? Localizer["model.none"] : newModel.Name));
        }
    }

    private void RefreshModelMenuMarkers(Cs2Menu menu, CCSPlayerController player, Side side)
    {
        var currentModel = Service.GetPlayerModel(player, side);
        for (int i = 0; i < menu.ItemCount; i++)
        {
            var info = menu.GetItemInfo(i);
            if (string.IsNullOrEmpty(info) || info.StartsWith("@")) continue;
            var m = Service.GetModel(info);
            if (m == null) continue;
            var isCurrent = currentModel != null && m.Index == currentModel.Index;
            var desired = isCurrent ? $"★ {m.Name}" : m.Name;
            if (menu.GetItemText(i) != desired)
            {
                menu.SetItemText(i, desired);
            }
            if (MenuManager.SupportsBadgesAndDialogs)
            {
                menu.SetItemHighlight(i, isCurrent);
            }
        }
    }

    private void UpdateCurrentModelItemText(Cs2Menu rootMenu, CCSPlayerController player)
    {
        var text = CurrentModelText(player);
        for (int i = 0; i < rootMenu.ItemCount; i++)
        {
            if (rootMenu.GetItemInfo(i) != "current") continue;
            if (rootMenu.GetItemText(i) != text)
            {
                rootMenu.SetItemText(i, text);
            }
            break;
        }
    }

    private void RefreshSideMenuTexts(CCSPlayerController player)
    {
        if (!_sideMenus.TryGetValue(player.Slot, out var sideMenu) || !sideMenu.IsValid)
        {
            return;
        }

        for (int i = 0; i < sideMenu.ItemCount; i++)
        {
            var info = sideMenu.GetItemInfo(i);
            if (string.IsNullOrEmpty(info) || !info.StartsWith("side_")) continue;

            var side = info.Substring("side_".Length).ToSide();
            if (side == null) continue;

            var sideName = $"{Localizer[$"side.{side.Value.ToName()}"]}";
            var currentModel = Service.GetPlayerModel(player, side.Value);
            var text = currentModel != null ? $"{sideName}: {currentModel.Name}" : sideName;
            if (sideMenu.GetItemText(i) != text)
            {
                sideMenu.SetItemText(i, text);
            }
        }
    }

    public Cs2Menu? GetSelectCustomizationMenu(CCSPlayerController player, Model? currentModel = null)
    {
        currentModel ??= Service.GetPlayerModel(player, Side.All);
        if (currentModel == null) return null;
        if (currentModel.Meshgroups.Count == 0 && currentModel.Skins.Count == 0) return null;

        var meshgroupPreference = Service.GetMeshgroupPreference(player, currentModel);
        var title = currentModel.Name;

        var menu = TryCreateMenu(title, (m, slot, item) => OnSelectMeshgroupGroup(m, slot, item, player, currentModel!));
        if (menu == null) return null;
        ConfigureMenu(menu);

        var skinMenu = GetSelectSkinMenu(player, currentModel);
        if (skinMenu != null)
        {
            menu.AddSubMenu(Localizer["modelmenu.skin"], skinMenu, "skin");
        }

        foreach (var meshgroup in currentModel.Meshgroups)
        {
            Dictionary<string, dynamic> data = JsonSerializer.Deserialize<Dictionary<string, dynamic>>(meshgroup.Value);
            bool radio = meshgroup.Key.Contains("@radio");
            bool opradio = meshgroup.Key.Contains("@opradio");
            bool combination = meshgroup.Key.Contains("@combination");
            var key = meshgroup.Key.Replace("@radio", "").Replace("@combination", "").Replace("@opradio", "");

            var subMenu = TryCreateMenu(key, (m, slot, item) => OnSelectMeshgroupItem(m, slot, item, player, currentModel!, radio, opradio, combination));
            if (subMenu == null) continue;
            ConfigureMenu(subMenu);

            foreach (var kv in data)
            {
                if (!combination)
                {
                    var value = JsonSerializer.Deserialize<int>(kv.Value);
                    var isSelected = meshgroupPreference.Contains(value);
                    var type = radio ? "r" : opradio ? "or" : "m";
                    var info = $"{value}|{type}";
                    subMenu.AddItem(isSelected ? $"★ {kv.Key}" : kv.Key, info);
                }
                else
                {
                    List<int> values = JsonSerializer.Deserialize<List<int>>(kv.Value) ?? new List<int>();
                    var allSelected = values.Count > 0 && values.All(v => meshgroupPreference.Contains(v));
                    var type = (radio || opradio) ? "cr" : "cm";
                    var info = $"{string.Join(",", values)}|{type}";
                    subMenu.AddItem(allSelected ? $"★ {kv.Key}" : kv.Key, info);
                }
            }

            menu.AddSubMenu(key, subMenu, meshgroup.Key);
        }

        return menu;
    }

    public Cs2Menu? GetSelectSkinMenu(CCSPlayerController player, Model? currentModel = null)
    {
        currentModel ??= Service.GetPlayerModel(player, Side.All);
        if (currentModel == null || currentModel.Skins.Count == 0) return null;

        var skinPreference = Service.GetSkinPreference(player, currentModel);
        var title = currentModel.Name;

        var menu = TryCreateMenu(title, (m, slot, item) => OnSelectSkin(m, slot, item, player, currentModel!));
        if (menu == null) return null;
        ConfigureMenu(menu);

        foreach (var skin in currentModel.Skins)
        {
            var isSelected = skin.Value == skinPreference;
            menu.AddItem(isSelected ? $"★ {skin.Key}" : skin.Key, skin.Value.ToString());
        }

        return menu;
    }

    public void OpenSelectCustomizationMenu(CCSPlayerController player)
    {
        if (!MenuManager.Available)
        {
            player.PrintToChat(Localizer["modelmenu.menusunavailable"]);
            return;
        }

        var menu = GetSelectCustomizationMenu(player);
        if (menu == null)
        {
            player.PrintToChat(Localizer["modelmenu.nomeshgroup"]);
            return;
        }
        MenuManager.Display(player, menu, menu);
    }

    public void OpenSelectSkinMenu(CCSPlayerController player)
    {
        if (!MenuManager.Available)
        {
            player.PrintToChat(Localizer["modelmenu.menusunavailable"]);
            return;
        }

        var menu = GetSelectSkinMenu(player);
        if (menu == null)
        {
            player.PrintToChat(Localizer["modelmenu.noskin"]);
            return;
        }
        MenuManager.Display(player, menu, menu);
    }

    private void OnSelectMeshgroupGroup(Cs2Menu menu, int slot, int item, CCSPlayerController player, Model currentModel)
    {
    }

    private void OnSelectMeshgroupItem(Cs2Menu menu, int slot, int item, CCSPlayerController player,
        Model currentModel, bool radio, bool opradio, bool combination)
    {
        if (!player.IsValid) return;

        if (!ApplyMeshgroupToggle(menu, item, player, currentModel, radio, opradio, combination))
        {
            RefreshMeshgroupMenuMarkers(menu, player, currentModel, radio, opradio, combination);
            return;
        }

        Service.MeshgroupUpdate(player);
        HideMenuWhileInspecting(player);

        RefreshMeshgroupMenuMarkers(menu, player, currentModel, radio, opradio, combination);
    }

    private bool ApplyMeshgroupToggle(Cs2Menu menu, int item, CCSPlayerController player,
        Model currentModel, bool radio, bool opradio, bool combination)
    {
        var info = menu.GetItemInfo(item);
        var parts = info.Split('|');
        if (parts.Length != 2) return false;
        var type = parts[1];
        var wasSelected = menu.GetItemText(item).StartsWith("★ ");

        if (type == "r") // radio (forced selected)
        {
            if (!int.TryParse(parts[0], out var value)) return false;
            if (wasSelected) return false;
            Service.AddMeshgroupPreference(player, currentModel, value, false);
            for (int i = 0; i < menu.ItemCount; i++)
            {
                if (i == item) continue;
                var sibInfo = menu.GetItemInfo(i);
                var sibParts = sibInfo.Split('|');
                if (sibParts.Length != 2 || sibParts[1] != "r") continue;
                if (!int.TryParse(sibParts[0], out var sibValue)) continue;
                Service.RemoveMeshgroupPreference(player, currentModel, sibValue, false);
            }
            return true;
        }
        if (type == "or") // optional radio
        {
            if (!int.TryParse(parts[0], out var value)) return false;
            if (wasSelected) return false;
            Service.AddMeshgroupPreference(player, currentModel, value, false);
            for (int i = 0; i < menu.ItemCount; i++)
            {
                if (i == item) continue;
                var sibInfo = menu.GetItemInfo(i);
                var sibParts = sibInfo.Split('|');
                if (sibParts.Length != 2 || sibParts[1] != "or") continue;
                if (!int.TryParse(sibParts[0], out var sibValue)) continue;
                Service.RemoveMeshgroupPreference(player, currentModel, sibValue, false);
            }
            return true;
        }
        if (type == "m") // multi-select
        {
            if (!int.TryParse(parts[0], out var value)) return false;
            if (wasSelected) return false;
            Service.AddMeshgroupPreference(player, currentModel, value, false);
            return true;
        }
        if (type == "cr") // combination radio
        {
            var values = parts[0].Split(',').Select(int.Parse).ToList();
            if (wasSelected) return false;
            Service.SetMeshgroupPreference(player, currentModel, values, false);
            return true;
        }
        if (type == "cm") // combination multi
        {
            var values = parts[0].Split(',').Select(int.Parse).ToList();
            if (wasSelected) return false;
            foreach (var v in values) Service.AddMeshgroupPreference(player, currentModel, v, false);
            return true;
        }
        return false;
    }

    private void RefreshMeshgroupMenuMarkers(Cs2Menu menu, CCSPlayerController player,
        Model currentModel, bool radio, bool opradio, bool combination)
    {
        var meshgroupPreference = Service.GetMeshgroupPreference(player, currentModel);
        for (int i = 0; i < menu.ItemCount; i++)
        {
            var info = menu.GetItemInfo(i);
            var parts = info.Split('|');
            if (parts.Length != 2) continue;
            var type = parts[1];

            bool isSelected;
            if (type == "r" || type == "or" || type == "m")
            {
                if (!int.TryParse(parts[0], out var v)) continue;
                isSelected = meshgroupPreference.Contains(v);
            }
            else // cr / cm
            {
                var vs = parts[0].Split(',').Select(int.Parse).ToList();
                isSelected = vs.All(v => meshgroupPreference.Contains(v));
            }

            var currentText = menu.GetItemText(i);
            var original = currentText.StartsWith("★ ") ? currentText.Substring(2) : currentText;
            var desired = isSelected ? $"★ {original}" : original;
            if (currentText != desired) menu.SetItemText(i, desired);
        }
    }

    private void OnSelectSkin(Cs2Menu menu, int slot, int item, CCSPlayerController player, Model currentModel)
    {
        if (!player.IsValid) return;
        if (!int.TryParse(menu.GetItemInfo(item), out var skin)) return;

        Service.SetSkinPreference(player, currentModel, skin);

        for (int i = 0; i < menu.ItemCount; i++)
        {
            var currentText = menu.GetItemText(i);
            var original = currentText.StartsWith("★ ") ? currentText.Substring(2) : currentText;
            var desired = (i == item ? "★ " : "") + original;
            if (currentText != desired) menu.SetItemText(i, desired);
        }

    }
}
