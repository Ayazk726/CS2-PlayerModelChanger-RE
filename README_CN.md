# CS2-PlayerModelChanger-RE

一个 CounterStrikeSharp 插件，让玩家自定义自己的模型。拥有模型选择菜单、按 `T` / `CT` 阵营选择、meshgroup 族切换、权限限制、默认模型与随机模型的功能。

本项目基于 [CS2-PlayerModelChanger](https://github.com/samyycX/CS2-PlayerModelChanger) 二次开发，使用Panorama风格菜单，可自动生成meshgroups配置，改进的模型预览机制。

> [!CAUTION]
> 此插件可能导致 GSLT 封禁，请自行承担风险。

## 目录

- [CS2-PlayerModelChanger-RE](#cs2-playermodelchanger-re)
  - [目录](#目录)
  - [功能](#功能)
  - [依赖](#依赖)
  - [安装](#安装)
    - [自动生成 meshgroup catalogue](#自动生成-meshgroup-catalogue)
    - [手动配置meshgroup](#手动配置meshgroup)
  - [指令](#指令)
    - [菜单指令](#菜单指令)
    - [客户端指令](#客户端指令)
    - [服务器指令](#服务器指令)
    - [管理员指令](#管理员指令)
  - [配置](#配置)
    - [文件结构](#文件结构)
    - [PlayerModelChanger-RE.json](#playermodelchanger-rejson)
    - [如何配置模型](#如何配置模型)
    - [MeshGroups.json](#meshgroupsjson)
    - [DefaultModels.json](#defaultmodelsjson)
    - [存储](#存储)
  - [常见问题](#常见问题)
  - [编译](#编译)
  - [如何添加模型](#如何添加模型)
    - [1. 找到模型路径](#1-找到模型路径)
    - [2. 用 MultiAddonManager 下载 addon](#2-用-multiaddonmanager-下载-addon)
    - [3. 把模型加进 PlayerModelChanger-RE.json](#3-把模型加进-playermodelchanger-rejson)

## 功能

- 模型选择菜单，支持 `T` / `CT` / 所有阵营选择。
- meshgroup 族选择：按模型选择 `Ears`、`tail`等组件控制组件开关且可修改配置文件允许哪些组件可供自定义。
- 玩家权限、模型权限、强制模型。
- 选择后立即生效、第三人称预览、可隐藏原版模型腿部。
- 可选：由插件自动从服务器创意工坊 VPK 生成 meshgroup catalogue（需要 `Source2Viewer-CLI`）。

## 依赖

1. [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 1.0.374+。
2. [Metamod:Source 2.0](https://www.metamodsource.net/downloads.php?branch=dev) 建议下载最新版
3. [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager) 用于创意工坊模型下载VPK。
4. [mm-cs2menus](https://github.com/FemboyKZ/mm-cs2menus) 用于提供Panorama风格菜单API。
5. [mm-cs2menus-css-bridge](https://github.com/FemboyKZ/mm-cs2menus/releases/download/2.1.4/cs2menus-css-bridge-2.1.4.zip) 兼容CSS插件的必要组件。
6. [Source2Viewer-CLI](https://github.com/ValveResourceFormat/ValveResourceFormat) 可选，用于自动生成 meshgroup catalogue。

## 安装

1. 安装 CounterStrikeSharp，mm-cs2menus，mm-cs2menus-css-bridge。
2. mm-cs2menus-css-bridge的dll需要放到:
   ```text
   addons/counterstrikesharp/shared/CounterStrikeSharp.Cs2Menus/CounterStrikeSharp.Cs2Menus.dll
   ```
4. 把插件放到：

   ```text
   game/csgo/addons/counterstrikesharp/plugins/
   ```

5. 启动一次服务器，CounterStrikeSharp 会生成主配置：

   ```text
   game/csgo/addons/counterstrikesharp/configs/plugins/PlayerModelChanger-RE/PlayerModelChanger-RE.json
   ```

6. 在 `Models` 里添加模型（见 [如何配置模型](#如何配置模型)）。

### 自动生成 meshgroup catalogue

1. 把 CLI 和它的依赖文件放到插件 DLL 同目录：

   ```text
   plugins/PlayerModelChanger-RE/
   ├─ PlayerModelChanger-RE.dll
   ├─ Source2Viewer-CLI.exe
   ├─ libSkiaSharp.dll
   └─ spirv-cross.dll
   ```

   插件会把 CLI 作为子进程启动。Linux 上放 Linux 版 CLI 以及它自带的 `.so` 文件，二进制可以叫 `Source2Viewer-CLI` 或 `Source2Viewer-CLI.exe`（Linux 不检查扩展名），并记得 `chmod +x`。
2. 在 `PlayerModelChanger-RE.json` 里填写 `MeshgroupCatalogueWorkshopIds`（推荐）或 `MeshgroupCatalogueVpkDirs`。**前者填写模型素材vpk的创意工坊id后者为指定扫描vpk的路径，两者都不填时不会进行自动生成操作。**
3. 插件加载时会生成 catalogue。如果 MultiAddonManager 还在下载 addon，等它下载完成后重启一次服务器，MAM 只会在下次启动或者换图时挂载已下载的 addon。结果按 VPK 缓存，之后的启动不会再调用 CLI。
### 手动配置meshgroup
[参考原版插件wiki](https://github.com/samyycX/CS2-PlayerModelChanger/wiki/Meshgroup-Configuration)，该手动配置优先级大于自动配置，推荐自动生成无法满足需求时使用。

## 指令
与原版插件基本相同

### 菜单指令

| 指令 | 说明 |
|---|---|
| `!md` / `!models` | 打开模型菜单：*模型选择* → 阵营选择（`所有阵营` / `T阵营` / `CT阵营`）→ 模型列表。 |
| `!md <all/ct/t>` / `!models <all/ct/t>` | 直接打开某个阵营的模型列表；`all` 打开根菜单。 |
| `!mg` / `!mesh` | 自定义当前佩戴模型的 meshgroup。 |

### 客户端指令

| 指令 | 说明 |
|---|---|
| `!model` | 显示当前模型和简短帮助。 |
| `!model <模型名> [all/ct/t]` | 修改模型；默认同时修改两个阵营，也可以只修改一个阵营。 |

### 服务器指令

| 指令 | 说明 |
|---|---|
| `pmc_enable <true/false>` | 开启 / 关闭插件。 |
| `pmc_resynccache` | 从存储重新同步玩家缓存。 |

### 管理员指令

需要 `@pmc/admin` 权限或 `#pmc/admin` 组。

| 指令 | 说明 |
|---|---|
| `!modeladmin reload` | 重载 `PlayerModelChanger-RE.json` 与 meshgroup catalogue。 |
| `!modeladmin [all/steamid] reset [all/ct/t]` | 重置所有玩家 / 指定玩家的模型，可只重置一个阵营。 |
| `!modeladmin [all/steamid] set [all/ct/t] [模型 index]` | 设置所有玩家 / 指定玩家的模型。 |
| `!modeladmin <steamid> check` | 检查玩家存储的模型是否仍可用，不可用则重置。 |

## 配置

### 文件结构

```text
counterstrikesharp/
├─ plugins/PlayerModelChanger-RE/
│  ├─ PlayerModelChanger-RE.dll
│  ├─ Source2Viewer-CLI.exe                # 可选，自动 catalogue 用
│  ├─ libSkiaSharp.dll                     # 可选，CLI 依赖（Linux 为 .so）
│  ├─ spirv-cross.dll                      # 可选，CLI 依赖（Linux 为 .so）
│  ├─ data.db                              # sqlite 存储（StorageType = sqlite）
│  └─ lang/                                # 翻译
└─ configs/plugins/PlayerModelChanger-RE/
   ├─ PlayerModelChanger-RE.json           # 主配置（首次启动生成）
   ├─ MeshGroups.json                      # 生成 catalogue后根据PlayerModelChanger-RE.json内配置的模型自动生成
   ├─ DefaultModels.json                   # 可选：默认 / 强制模型
   └─ catalogue/                           # meshgroup catalogue 文件
      ├─ <名字>.json                       # 自定义catalogue（不推荐）
      └─ auto/<addon>.json                 # 插件自动生成
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

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `Models` | `{}` | 玩家可选的模型，见 [如何配置模型](#如何配置模型)。 |
| `StorageType` | `sqlite` | `sqlite` 或 `mysql`。 |
| `MySQL_*` | — | MySQL 连接信息，仅在 `StorageType` 为 `mysql` 时使用。 |
| `ModelForBots` | `{CT:[],T:[]}` | bot 按阵营获得的模型 index；空列表表示不处理 bot。 |
| `ModelChangeCooldownSecond` | `0` | 两次更换模型之间的冷却秒数，`0` 为不限制。 |
| `Inspection.Enable` | `true` | 选择后第三人称预览。 |
| `Inspection.Mode` | `rotation` | 预览镜头移动方式。 |
| `BasicPermission` | `""` | 打开模型菜单所需权限，留空表示所有人。 |
| `DisableDefaultModelLeg` | `false` | 隐藏原版模型腿部（render 254）。 |
| `DisableInstantChange` | `false` | 只在复活时应用模型，不立即切换。 |
| `DisablePrecache` | `false` | 不在换图时预缓存模型。 |
| `DisableRandomModel` | `false` | 隐藏"随机"选项。 |
| `DisableAutoCheck` | `false` | 复活时不检查存储的模型是否有效。 |
| `DisableMeshgroupDetection` | `false` | 完全不读取 catalogue（没有 meshgroup 菜单）。 |
| `DisableAutoMeshgroupCatalogue` | `false` | 不从 VPK 自动生成 catalogue。 |
| `MeshgroupCatalogueWorkshopIds` | `[]` | 读取哪些创意工坊 addon 来生成 catalogue。 |
| `MeshgroupCatalogueVpkDirs` | `[]` | 扫描哪些目录里的 addon VPK，或作为查找上面 workshop id 的搜索根。 |
| `DisablePlayerSelection` | `false` | 禁止玩家使用 `!model` / `!md` / `!mg`。 |
| `AutoResyncCache` | `false` | 每次复活都从存储重新读取缓存。 |
| `ConfigVersion` | `1` | 由 CounterStrikeSharp 管理。 |

### 如何配置模型

简单配置示例

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
  "MeshgroupCatalogueWorkshopIds": ["你的vpkid"],
  "MeshgroupCatalogueVpkDirs": ["你的vpk所在路径"],
  "DisablePlayerSelection": false,
  "AutoResyncCache": false,
  "ConfigVersion": 1
}
```
更多配置可以参考https://github.com/samyycX/CS2-PlayerModelChanger/wiki/Models-Configuration
> [!CAUTION]
> 如果想让插件自动生成meshgroup配置请填写 MeshgroupCatalogueWorkshopIds（推荐）或 MeshgroupCatalogueVpkDirs。前者填写模型素材vpk的创意工坊id后者为指定扫描vpk的路径，两者都不填时不会进行自动生成操作。

### MeshGroups.json

这个文件由插件生成和维护。对 `PlayerModelChanger-RE.json` 里每个有 catalogue 的模型，都会生成对每个组件的自定义开关启用控制配置：

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

- `true`：该组件开放给玩家自定义（`!mg` 菜单里会出现）。
- `false`：整个组件保持模型自带默认值，不进菜单。
- 插件会让该文件与 catalogue 保持同步：新的组件按 catalogue 默认值补上，消失的组件被移除


### DefaultModels.json

可选文件，用于按玩家 / 权限 / 组设置默认或强制模型。没有该文件时功能关闭。

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

- `all` 对两个阵营生效，`t` / `ct` 覆盖单个阵营。
- 键：`*`（所有人）、`@flag`（CSS 权限）、`#group`（CSS 组）、SteamID64。
- `index` 可以是单个模型 index 或列表（从列表里随机）；允许 `""` 与 `@random`。
- `force: true` 表示匹配的玩家不能自行更换模型（该阵营不会出现在选择菜单里）。
- 当玩家存储的模型失效、或玩家选择 *默认* 选项时，会使用这里的默认模型。

### 存储

玩家模型和 meshgroup 偏好通过 `IStorage` 保存：

- `sqlite`（默认）：`game/csgo/addons/counterstrikesharp/plugins/PlayerModelChanger-RE/data.db`
- `mysql`：使用 `MySQL_*` 配置，表名由 `MySQL_Table` 指定。

模型按阵营分别存储（`t_model` / `ct_model`）；菜单默认把一个模型同时应用到两个阵营，管理员指令可以单独设置某一阵营。

## 常见问题

- **没有 meshgroup 菜单（`!mg` 提示没有可用的皮肤组）**：该模型没有可用的组件，或者没有配置需要读取组件的vpkid（`MeshgroupCatalogueWorkshopIds`）
- **catalogue 同步没有反应**：`MeshgroupCatalogueWorkshopIds` 与 `MeshgroupCatalogueVpkDirs` 都为空，日志会显示 `Mesh group catalogue sync disabled`。
- **菜单不可用**：未安装 mm-cs2menus 或它没有启动；聊天指令仍然可用。

## 编译

```bash
dotnet build
```

输出目录为 `build/Release/PlayerModelChanger-RE/`。如果你的 CS2 安装不在 `../steamcmd/...`，可以显式传入 csgo 目录：

```bash
dotnet build /p:CsgoDir="C:/path/to/game/csgo/"
```

## 如何添加模型

### 1. 找到模型路径

1. 用 `Source2Viewer` 或 `GCFScape` 打开创意工坊 `.vpk`（或 `pak01.vpk`）。
2. 找到编译后的模型（`.vmdl_c`），复制它的路径。
3. 把 `.vmdl_c` 替换成 `.vmdl`。

路径应该类似 `characters/.../xxx.vmdl`。

> [!IMPORTANT]
> 配置 JSON 里必须把 `.vmdl_c` 替换成 `.vmdl`。

### 2. 用 MultiAddonManager 下载 addon

1. 把创意工坊 ID 加到 `MultiAddonManager`（例如 `mm_extra_addons "xxxxx"`）。
2. 重启服务端让 addon 完成下载并挂载。

### 3. 把模型加进 PlayerModelChanger-RE.json

在 `PlayerModelChanger-RE.json` 里添加配置（见 [如何配置模型？](#如何配置模型？)），然后 `!modeladmin reload` 或重启插件。
