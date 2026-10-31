using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CounterStrikeSharp.API.Core;
using Cs2Menus;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

public class ModelMenuManager : IDisposable
{
    public const float MenuDuration = 0f;

    private Cs2MenusBridge? _bridge;

    private readonly ConcurrentDictionary<int, Cs2Menu> _active = new();

    private readonly ConcurrentDictionary<int, Cs2Menu> _rootMenus = new();

    public bool Available => _bridge != null && _bridge.Available;

    public bool BridgeLoaded => _bridge != null;

    public Cs2MenusBridge? Bridge => _bridge;

    public bool SupportsBadgesAndDialogs => _bridge != null && _bridge.SupportsBadgesTabsAndDialogs;

    public void Initialize(Cs2MenusBridge bridge) => _bridge = bridge;

    public void Display(CCSPlayerController player, Cs2Menu menu, Cs2Menu? rootMenu = null)
    {
        if (_bridge == null) return;
        int slot = player.Slot;

        CancelAndDisposeSlot(slot);

        if (rootMenu != null)
        {
            _rootMenus[slot] = rootMenu;
        }

        menu.Ended += OnMenuEnded;
        _active[slot] = menu;
        menu.Display(slot, MenuDuration);
    }

    public bool Push(CCSPlayerController player, Cs2Menu menu)
    {
        if (_bridge == null) return false;
        int slot = player.Slot;

        try
        {
            menu.Ended += OnMenuEnded;
            _active[slot] = menu;
            return menu.Push(slot, MenuDuration);
        }
        catch (Exception ex)
        {
            _active.TryRemove(slot, out _);
            try { menu.Dispose(); } catch { }
            PlayerModelChanger.getInstance()?.Logger.LogWarning($"[PlayerModelChanger] Could not push a menu on top of the current one: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void CloseMenu(CCSPlayerController player)
    {
        if (_bridge == null) return;
        CancelAndDisposeSlot(player.Slot);
    }

    public void SuspendMenu(CCSPlayerController player)
    {
        if (_bridge == null) return;
        Cs2MenusBridge.Suspend(player.Slot);
    }

    public void ResumeMenu(CCSPlayerController player)
    {
        if (_bridge == null) return;
        Cs2MenusBridge.Resume(player.Slot);
    }

    public void RemovePlayer(int slot)
    {
        if (_bridge == null) return;
        CancelAndDisposeSlot(slot);
    }

    public void ReloadPlayer()
    {
        _active.Clear();
        _rootMenus.Clear();
    }

    public void RerenderPlayer(int slot)
    {
        if (_bridge == null) return;

        if (_bridge.GetActiveMenu(slot) != null) return;

        if (_active.TryGetValue(slot, out var menu) && menu.IsValid)
        {
            menu.Display(slot, MenuDuration);
        }
    }

    public void Notify(CCSPlayerController player, int slot, string text)
    {
        if (_bridge != null
            && _bridge.SupportsBadgesTabsAndDialogs
            && Cs2MenusBridge.ShowMessage(slot, text, MenuTone.Ok))
        {
            return;
        }

        player.PrintToChat(text);
    }

    public bool Confirm(int slot, string title, string body, string cancel, string confirm, Action<int, bool> onDone)
    {
        if (_bridge == null || !_bridge.SupportsBadgesTabsAndDialogs)
        {
            return false;
        }

        return _bridge.ShowConfirm(slot, title, body, cancel, confirm, false, onDone);
    }

    public Cs2Menu? GetRootMenu(int slot)
        => _rootMenus.TryGetValue(slot, out var m) ? m : null;

    private void OnMenuEnded(Cs2Menu menu, int slot, MenuEndReason reason)
    {
        if (_active.TryGetValue(slot, out var current) && ReferenceEquals(current, menu))
        {
            _active.TryRemove(slot, out _);

            if (_rootMenus.TryGetValue(slot, out var below) && below.IsValid)
            {
                _active[slot] = below;
            }
        }

        if (_rootMenus.TryGetValue(slot, out var root) && ReferenceEquals(root, menu))
        {
            _rootMenus.TryRemove(slot, out _);
            DisposeTree(menu);
            return;
        }

        try { menu.Dispose(); } catch { }
    }

    private void CancelAndDisposeSlot(int slot)
    {
        if (_rootMenus.TryRemove(slot, out var root))
        {
            DisposeTree(root);
        }

        if (_active.TryRemove(slot, out var menu) && (root == null || !ReferenceEquals(menu, root)))
        {
            try { menu.Dispose(); } catch { }
        }
    }

    private static void DisposeTree(Cs2Menu menu)
    {
        var seen = new HashSet<Cs2Menu>();
        DisposeTree(menu, seen);
    }

    private static void DisposeTree(Cs2Menu menu, HashSet<Cs2Menu> seen)
    {
        if (menu == null || !seen.Add(menu)) return;

        try
        {
            for (int i = 0; i < menu.ItemCount; i++)
            {
                Cs2Menu? child = menu.GetItemSubmenu(i);
                if (child != null)
                {
                    DisposeTree(child, seen);
                }
            }
        }
        catch { }

        try { menu.Dispose(); } catch { }
    }

    public void Dispose()
    {
        foreach (var menu in _active.Values) { try { menu.Dispose(); } catch { } }
        foreach (var menu in _rootMenus.Values) { try { DisposeTree(menu); } catch { } }
        _active.Clear();
        _rootMenus.Clear();
    }
}
