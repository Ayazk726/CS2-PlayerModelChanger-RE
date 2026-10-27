using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CounterStrikeSharp.API.Core;
using Cs2Menus;
using Microsoft.Extensions.Logging;

namespace PlayerModelChanger;

/// <summary>
/// Per-plugin bridge to mm-cs2menus. Owns one <see cref="Cs2MenusBridge"/> and tracks
/// every menu we create so we can cancel and dispose them cleanly. Each menu's
/// <see cref="Cs2Menu.Ended"/> event removes it from <see cref="_active"/>; we
/// <see cref="Cs2Menu.Dispose"/> there to release the GCHandle/cs2menus callback
/// tokens (otherwise hot-reload of this assembly would crash the server).
/// </summary>
public class ModelMenuManager : IDisposable
{
    /// <summary>
    /// How long a model menu stays on screen, in seconds.
    /// </summary>
    /// <remarks>
    /// 0 means the menu has no runtime limit: it never closes on its own, so it
    /// never interrupts a player who is still choosing a model. The player closes
    /// it, or it goes away with the map change.
    /// </remarks>
    public const float MenuDuration = 0f;

    private Cs2MenusBridge? _bridge;

    // Currently displayed menu per slot (the one cs2menus is rendering right now).
    private readonly ConcurrentDictionary<int, Cs2Menu> _active = new();

    // Top-level menu per slot, remembered so child menus can refresh its
    // items (e.g., update the current model text) after the user picks a model.
    private readonly ConcurrentDictionary<int, Cs2Menu> _rootMenus = new();

    /// <summary>True when mm-cs2menus is loaded and the bridge is reachable.</summary>
    public bool Available => _bridge != null && _bridge.Available;

    /// <summary>
    /// True when the bridge object exists. The native plugin may still be starting
    /// up, so this is not the same as <see cref="Available"/>.
    /// </summary>
    public bool BridgeLoaded => _bridge != null;

    /// <summary>The underlying bridge, for code paths that build menus directly.</summary>
    public Cs2MenusBridge? Bridge => _bridge;

    /// <summary>
    /// True when the loaded cs2menus has the ICS2Menus005 additions: item badges,
    /// the header's scope chip, tabs, chips and the dialogs.
    /// </summary>
    public bool SupportsBadgesAndDialogs => _bridge != null && _bridge.SupportsBadgesTabsAndDialogs;

    /// <summary>Wire the bridge after the native binary was loaded.</summary>
    public void Initialize(Cs2MenusBridge bridge) => _bridge = bridge;

    /// <summary>
    /// Display <paramref name="menu"/> to the player. Any previously displayed menu
    /// on the same slot is cancelled and disposed. If <paramref name="rootMenu"/> is
    /// given, it is remembered as the top-level menu for this slot.
    /// </summary>
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

    /// <summary>
    /// Show <paramref name="menu"/> on top of the menu that is on the screen.
    /// The back button of the new menu brings the player back to the one below
    /// it, the menu below stays where it was.
    /// </summary>
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
            // The menu could not go on top of the one on the screen, it is
            // dropped instead of leaving a half open menu behind.
            _active.TryRemove(slot, out _);
            try { menu.Dispose(); } catch { }
            PlayerModelChanger.getInstance()?.Logger.LogWarning($"[PlayerModelChanger] Could not push a menu on top of the current one: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Cancel and dispose whatever menu this slot currently has open.</summary>
    public void CloseMenu(CCSPlayerController player)
    {
        if (_bridge == null) return;
        CancelAndDisposeSlot(player.Slot);
    }

    /// <summary>
    /// Hide the menu on screen without ending it: the player sees the game
    /// again while the menu stays where it was. <see cref="ResumeMenu"/> brings
    /// it back, cursor, marks and all.
    /// </summary>
    public void SuspendMenu(CCSPlayerController player)
    {
        if (_bridge == null) return;
        Cs2MenusBridge.Suspend(player.Slot);
    }

    /// <summary>Show the menu of this slot again after it was suspended.</summary>
    public void ResumeMenu(CCSPlayerController player)
    {
        if (_bridge == null) return;
        Cs2MenusBridge.Resume(player.Slot);
    }

    /// <summary>Called from player lifecycle events on disconnect.</summary>
    public void RemovePlayer(int slot)
    {
        if (_bridge == null) return;
        CancelAndDisposeSlot(slot);
    }

    /// <summary>Hot-reload safety: drop all per-slot state. The bridge will be
    /// re-initialised and existing menus will be GC'd via the old bridge's Dispose.</summary>
    public void ReloadPlayer()
    {
        _active.Clear();
        _rootMenus.Clear();
    }

    /// <summary>
    /// Re-display the active menu for the slot. cs2menus re-renders a menu by
    /// itself after every pick, so this is only needed when the menu on screen
    /// is not the one we track.
    /// </summary>
    public void RerenderPlayer(int slot)
    {
        if (_bridge == null) return;

        // The menu on the screen is re-rendered by cs2menus. Displaying it again
        // would end the current display and dispose the menu.
        if (_bridge.GetActiveMenu(slot) != null) return;

        if (_active.TryGetValue(slot, out var menu) && menu.IsValid)
        {
            menu.Display(slot, MenuDuration);
        }
    }

    /// <summary>
    /// Tell the player what happened: a toast in the menu's own window when it is
    /// a panorama one, in chat otherwise.
    /// </summary>
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

    /// <summary>
    /// Ask the player to confirm an action. Only a panorama window can show the
    /// dialog, so this returns false when there is none and the caller applies
    /// the change right away.
    /// </summary>
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
        // Only clear if this menu is still the active one (Display may have already
        // swapped it for another one before this Ended arrived).
        if (_active.TryGetValue(slot, out var current) && ReferenceEquals(current, menu))
        {
            _active.TryRemove(slot, out _);

            // A menu that was pushed on top of the root menu is gone now, the
            // root menu is on the screen again.
            if (_rootMenus.TryGetValue(slot, out var below) && below.IsValid)
            {
                _active[slot] = below;
            }
        }

        if (_rootMenus.TryGetValue(slot, out var root) && ReferenceEquals(root, menu))
        {
            _rootMenus.TryRemove(slot, out _);
            // The root menu owns its submenus, releasing its tree releases them all.
            DisposeTree(menu);
            return;
        }

        // Dispose is idempotent; safe even if Display already disposed it.
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

    /// <summary>
    /// Dispose a menu and every submenu below it. The bridge would free them all
    /// when it is disposed, but that only happens when this plugin unloads.
    /// </summary>
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
