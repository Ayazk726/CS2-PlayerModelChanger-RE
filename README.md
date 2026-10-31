# CS2-PlayerModelChanger-RE

A CounterStrikeSharp plugin that lets players customize their model. It provides a model selection menu, per-side (`T` / `CT`) selection, meshgroup family switching, permission restrictions, default models and random models.

This project is a derivative work based on [CS2-PlayerModelChanger](https://github.com/samyycX/CS2-PlayerModelChanger). It uses Panorama-style menus, can generate meshgroup configuration automatically and has an improved model preview.

中文文档：[README_CN.md](https://github.com/Ayazk726/CS2-PlayerModelChanger-RE/blob/master/README_CN.md)

> [!CAUTION]
> This plugin can cause a GSLT ban, use it at your own risk.

## Contents

- [CS2-PlayerModelChanger-RE](#cs2-playermodelchanger-re)
  - [Contents](#contents)
  - [Features](#features)
  - [Dependencies](#dependencies)
  - [Installation](#installation)
    - [Automatic meshgroup catalogue](#automatic-meshgroup-catalogue)
    - [Manual meshgroup configuration](#manual-meshgroup-configuration)
  - [Commands](#commands)
    - [Menu commands](#menu-commands)
    - [Client commands](#client-commands)
    - [Server commands](#server-commands)
    - [Admin commands](#admin-commands)
  - [Configuration](#configuration)
    - [Files](#files)
    - [PlayerModelChanger-RE.json](#playermodelchanger-rejson)
    - [How to configure a model](#how-to-configure-a-model)
    - [MeshGroups.json](#meshgroupsjson)
    - [DefaultModels.json](#defaultmodelsjson)
    - [Storage](#storage)
  - [Common issues](#common-issues)
  - [Build](#build)
  - [Adding a model](#adding-a-model)
    - [1. Locate the model path](#1-locate-the-model-path)
    - [2. Download the addon with MultiAddonManager](#2-download-the-addon-with-multiaddonmanager)
    - [3. Add the model to PlayerModelChanger-RE.json](#3-add-the-model-to-playermodelchanger-rejson)
  - [License](#license)

## Features

- Model selection menu with `T` / `CT` / all-sides selection.
- Meshgroup family selection: pick components such as `Ears` and `tail` per model, and control which components are open for customization through the configuration file.
- Per-player permissions, per-model permissions and forced models.
- Instant change after selection, third-person preview, optional hidden default-model legs.
- Optional: the plugin can build the meshgroup catalogue automatically from the workshop VPKs of the server (requires `Source2Viewer-CLI`).

## Dependencies

1. [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 1.0.374+.
2. [Metamod:Source 2.0](https://www.metamodsource.net/downloads.php?branch=dev) — the latest version is recommended.
3. [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager) — downloads the workshop model VPKs.
4. [mm-cs2menus](https://github.com/FemboyKZ/mm-cs2menus) — provides the Panorama-style menu API.
5. [mm-cs2menus-css-bridge](https://github.com/FemboyKZ/mm-cs2menus/releases/download/2.1.4/cs2menus-css-bridge-2.1.4.zip) — required component for CounterStrikeSharp plugin compatibility.
6. [Source2Viewer-CLI](https://github.com/ValveResourceFormat/ValveResourceFormat) — optional, only needed for the automatic meshgroup catalogue.

## Installation

1. Install CounterStrikeSharp, mm-cs2menus and mm-cs2menus-css-bridge.
2. Put the mm-cs2menus-css-bridge DLL here:

   ```text
   addons/counterstrikesharp/shared/CounterStrikeSharp.Cs2Menus/CounterStrikeSharp.Cs2Menus.dll
   ```
3. Put the plugin here:

   ```text
   game/csgo/addons/counterstrikesharp/plugins/
   ```
4. Start the server once. CounterStrikeSharp generates the main config at:

   ```text
   game/csgo/addons/counterstrikesharp/configs/plugins/PlayerModelChanger-RE/PlayerModelChanger-RE.json
   ```
5. Add your models to `Models` (see [Adding a model](#adding-a-model)).

### Automatic meshgroup catalogue

1. Put the CLI and its dependency files in the same directory as the plugin DLL:

   ```text
   plugins/PlayerModelChanger-RE/
   ├─ PlayerModelChanger-RE.dll
   ├─ Source2Viewer-CLI.exe
   ├─ libSkiaSharp.dll
   └─ spirv-cross.dll
   ```

   The plugin starts the CLI as a child process. On Linux put the Linux build together with its own `.so` files in the same place; the binary may be called `Source2Viewer-CLI` or `Source2Viewer-CLI.exe` (Linux does not check the extension). Remember to `chmod +x`.
2. Set `MeshgroupCatalogueWorkshopIds` (recommended) or `MeshgroupCatalogueVpkDirs` in `PlayerModelChanger-RE.json`. **The former is the workshop id of the VPK containing the model assets; the latter is a path to scan for VPKs. If neither is set, nothing is generated automatically.**
3. The plugin builds the catalogue when it loads. If MultiAddonManager is still downloading an addon, wait until it finishes and restart the server once; MAM only mounts a downloaded addon on the next start or map change. The result is cached per VPK, so later starts do not call the CLI again.

### Manual meshgroup configuration

[See the original plugin wiki](https://github.com/samyycX/CS2-PlayerModelChanger/wiki/Meshgroup-Configuration). The manual configuration takes priority over the automatic one; use it when the automatic output does not fit your needs.

## Commands

Mostly the same as the original plugin.

### Menu commands

| Command | Description |
|---|---|
| `!md` / `!models` | Open the model menu: *Select model* → side picker (`All sides` / `T side` / `CT side`) → model list. |
| `!md <all/ct/t>` / `!models <all/ct/t>` | Open the model list of one side directly; `all` opens the root menu. |
| `!mg` / `!mesh` | Customize the meshgroups of the model the player currently wears. |

### Client commands

| Command | Description |
|---|---|
| `!model` | Show the current model and a short help. |
| `!model <model name> [all/ct/t]` | Change the model; by default both sides are changed, one side can be selected. |

### Server commands

| Command | Description |
|---|---|
| `pmc_enable <true/false>` | Enable / disable the plugin. |
| `pmc_resynccache` | Reload the player cache from the storage. |

### Admin commands

Requires the `@pmc/admin` permission or the `#pmc/admin` group.

| Command | Description |
|---|---|
| `!modeladmin reload` | Reload `PlayerModelChanger-RE.json` and the meshgroup catalogue. |
| `!modeladmin [all/steamid] reset [all/ct/t]` | Reset the model of all players / one player; optionally one side only. |
| `!modeladmin [all/steamid] set [all/ct/t] [model index]` | Set the model of all players / one player. |
| `!modeladmin <steamid> check` | Check whether the stored model of the player is still allowed and reset it if not. |

## Configuration

### Files

```text
counterstrikesharp/
├─ plugins/PlayerModelChanger-RE/
│  ├─ PlayerModelChanger-RE.dll
│  ├─ Source2Viewer-CLI.exe                # optional, automatic catalogue
│  ├─ libSkiaSharp.dll                     # optional, CLI dependency (.so on Linux)
│  ├─ spirv-cross.dll                      # optional, CLI dependency (.so on Linux)
│  ├─ data.db                              # sqlite storage (StorageType = sqlite)
│  └─ lang/                                # translations
└─ configs/plugins/PlayerModelChanger-RE/
   ├─ PlayerModelChanger-RE.json           # main config (generated on first start)
   ├─ MeshGroups.json                      # generated from the models in PlayerModelChanger-RE.json after the catalogue is built
   ├─ DefaultModels.json                   # optional: default / forced models
   └─ catalogue/                           # meshgroup catalogue files
      ├─ <name>.json                       # custom catalogue (not recommended)
      └─ auto/<addon>.json                 # generated by the plugin
```

### PlayerModelChanger-RE.json

```json
{
  "Models": {},
  "StorageType": "sqlite",
  "MySQL_IP": "",
  "MySQL_Port": "",
  "MySQL_User": "",
  "MySQL_Password": "",
  "MySQL_Database": "",
  "MySQL_Table": "playermodelchanger",
  "ModelForBots": { "CT": [], "T": [] },
  "ModelChangeCooldownSecond": 0,
  "Inspection": { "Enable": true, "Mode": "rotation" },
  "BasicPermission": "",
  "DisableDefaultModelLeg": false,
  "DisableInstantChange": false,
  "DisablePrecache": false,
  "DisableRandomModel": false,
  "DisableAutoCheck": false,
  "DisableMeshgroupDetection": false,
  "DisableAutoMeshgroupCatalogue": false,
  "MeshgroupCatalogueWorkshopIds": [],
  "MeshgroupCatalogueVpkDirs": [],
  "DisablePlayerSelection": false,
  "AutoResyncCache": false,
  "ConfigVersion": 1
}
```

| Key | Default | Description |
|---|---|---|
| `Models` | `{}` | The models the player may pick, see [How to configure a model](#how-to-configure-a-model). |
| `StorageType` | `sqlite` | `sqlite` or `mysql`. |
| `MySQL_*` | — | MySQL connection settings, only used when `StorageType` is `mysql`. |
| `ModelForBots` | `{CT:[],T:[]}` | Model indices bots get per side. A bot picks one model on its first spawn of a map and keeps it for the whole map, also after a change of sides; an empty list leaves the bot with the game's model. |
| `ModelChangeCooldownSecond` | `0` | Cooldown between model changes in seconds; `0` disables the cooldown. |
| `Inspection.Enable` | `true` | Third-person preview after a selection. |
| `Inspection.Mode` | `rotation` | How the preview camera moves. |
| `BasicPermission` | `""` | Permission required to open the model menu; empty means everyone. |
| `DisableDefaultModelLeg` | `false` | Hide the default model legs (render 254). |
| `DisableInstantChange` | `false` | Only apply a new model on spawn, never instantly. |
| `DisablePrecache` | `false` | Do not precache models on map change. |
| `DisableRandomModel` | `false` | Hide the "random" entry. |
| `DisableAutoCheck` | `false` | Do not validate the stored model on spawn. |
| `DisableMeshgroupDetection` | `false` | Do not read the catalogue at all (no meshgroup menu). |
| `DisableAutoMeshgroupCatalogue` | `false` | Do not build the catalogue from VPKs. |
| `MeshgroupCatalogueWorkshopIds` | `[]` | Workshop ids whose addons are read to build the catalogue. |
| `MeshgroupCatalogueVpkDirs` | `[]` | Directories scanned for addon VPKs, or used as search roots for the workshop ids above. |
| `DisablePlayerSelection` | `false` | Disable `!model` / `!md` / `!mg` for players. |
| `AutoResyncCache` | `false` | Reload the storage cache on every spawn. |
| `ConfigVersion` | `1` | Managed by CounterStrikeSharp. |

### How to configure a model

A simple example:

```json
// This configuration was automatically generated by CounterStrikeSharp for plugin 'PlayerModelChanger', at 2026/10/27 05:03:19
{
  "Models": {    
    "Sample1": {
      "name": "Sample1",
      "path": "characters/models/Sample/Sample1.vmdl",
      "side": "ALL"
    },
    "Sample2": {
      "name": "Sample2",
      "path": "characters/models/Sample2/Sample2.vmdl",
      "side": "ALL"
    }},
  "StorageType": "sqlite",
  "MySQL_IP": "",
  "MySQL_Port": "",
  "MySQL_User": "",
  "MySQL_Password": "",
  "MySQL_Database": "",
  "MySQL_Table": "playermodelchanger",
  "ModelForBots": {
    "CT": [],
    "T": []
  },
  "ModelChangeCooldownSecond": 0,
  "Inspection": {
    "Enable": true,
    "Mode": "rotation"
  },
  "BasicPermission": "",
  "DisableDefaultModelLeg": false,
  "DisableInstantChange": false,
  "DisablePrecache": false,
  "DisableRandomModel": false,
  "DisableAutoCheck": false,
  "DisableMeshgroupDetection": false,
  "DisableAutoMeshgroupCatalogue": false,
  "MeshgroupCatalogueWorkshopIds": ["your vpk id"],
  "MeshgroupCatalogueVpkDirs": ["path to your vpk"],
  "DisablePlayerSelection": false,
  "AutoResyncCache": false,
  "ConfigVersion": 1
}
```

For more configuration options see https://github.com/samyycX/CS2-PlayerModelChanger/wiki/Models-Configuration.

> [!CAUTION]
> To let the plugin generate the meshgroup configuration automatically, fill in `MeshgroupCatalogueWorkshopIds` (recommended) or `MeshgroupCatalogueVpkDirs`. The former is the workshop id of the VPK containing the model assets; the latter is the path to scan for VPKs. If neither is set, nothing is generated automatically.

### MeshGroups.json

This file is generated and maintained by the plugin. For every model in `PlayerModelChanger-RE.json` that has a catalogue entry, it holds one switch per component family:

```json
{
  "sample1": {
    "defusekit": false,
    "dragon": true,
    "first_or_third_person": false
  },
  "sample2": {
    "Ears": true,
    "Hair": true,
    "Eyes": true,
    "Glasses": true,
    "Clothes": true,
    "Body": true,
    "first_or_third_person": false
  }
}
```

- `true`: the component family is open for customization by players (it appears in the `!mg` menu).
- `false`: the whole family keeps the model's own default value and is not shown in the menu.
- The plugin keeps the file in sync with the catalogue: new components are added with their catalogue default, components that disappeared are removed.

### DefaultModels.json

Optional file. Sets default or forced models per player / permission / group. The feature is disabled without this file.

```json
{
  "DefaultModels": {
    "all": {
      "*": { "index": ["sample1"], "force": false },
      "@css/vip": { "index": ["sample2"], "force": true },
      "76561198000000000": { "index": ["sample1"], "force": false }
    },
    "t": {
      "@css/vip": { "index": ["sample1"], "force": false }
    },
    "ct": {
      "#vip": { "index": ["sample2"], "force": false }
    }
  }
}
```

- `all` applies to both sides; `t` / `ct` override it for one side.
- Keys: `*` (everyone), `@flag` (CSS permission), `#group` (CSS group), SteamID64.
- `index` accepts one model index or a list (a random one of the list is used); `""` and `@random` are allowed.
- `force: true` means the matching players cannot change their model (that side is not offered in the selection menu).
- The default model is used when the player's stored model is invalid, or when the player picks the *Default* entry.

### Storage

Player model and meshgroup preferences are stored with `IStorage`:

- `sqlite` (default): `game/csgo/addons/counterstrikesharp/plugins/PlayerModelChanger-RE/data.db`
- `mysql`: uses the `MySQL_*` settings, the table name comes from `MySQL_Table`.

The player model is stored per side (`t_model` / `ct_model`); the menu applies one model to both sides by default, and the admin commands can set one side only.

## Common issues

- **No meshgroup menu (`!mg` says no customizable meshgroup)**: the model has no available component, or no VPK id whose components should be read is configured (`MeshgroupCatalogueWorkshopIds`).
- **The catalogue sync does nothing**: both `MeshgroupCatalogueWorkshopIds` and `MeshgroupCatalogueVpkDirs` are empty; the log shows `Mesh group catalogue sync disabled`.
- **Menus unavailable**: mm-cs2menus is not installed or not started; chat commands still work.

## Build

```bash
dotnet build
```

The output goes to `build/Release/PlayerModelChanger-RE/`. If your CS2 installation is not at `../steamcmd/...`, pass the csgo directory explicitly:

```bash
dotnet build /p:CsgoDir="C:/path/to/game/csgo/"
```

## Adding a model

### 1. Locate the model path

1. Open the workshop `.vpk` (or `pak01.vpk`) with `Source2Viewer` or `GCFScape`.
2. Find the compiled model (`.vmdl_c`) and copy its path.
3. Replace `.vmdl_c` with `.vmdl`.

The path should look like `characters/.../xxx.vmdl`.

> [!IMPORTANT]
> The config JSON must use `.vmdl`, not `.vmdl_c`.

### 2. Download the addon with MultiAddonManager

1. Add the workshop ID to `MultiAddonManager` (for example `mm_extra_addons "xxxxx"`).
2. Restart the server so the addon is downloaded and mounted.

### 3. Add the model to PlayerModelChanger-RE.json

Add the configuration in `PlayerModelChanger-RE.json` (see [How to configure a model](#how-to-configure-a-model)), then run `!modeladmin reload` or restart the plugin.

## License

This project is licensed under the **GNU General Public License v3.0**. See [LICENSE](LICENSE) for the full license text.

It is a modified version of [CS2-PlayerModelChanger](https://github.com/samyycX/CS2-PlayerModelChanger), originally by samyycX and contributors. See [NOTICE](NOTICE) for attribution, the list of modifications and the source code locations.

Source code:

- GitHub: <https://github.com/Ayazk726/CS2-PlayerModelChanger-RE>
- Gitee: <https://gitee.com/Ayazk/cs2-player-model-changer-re>

