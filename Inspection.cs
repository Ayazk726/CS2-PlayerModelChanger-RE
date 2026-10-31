using System.Drawing;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace PlayerModelChanger;

public class Inspection
{

    private static List<CameraStatus> cameraStatuses = new List<CameraStatus>();
    const int MAX_ROTATION_TIMES = 300;
    const float DISTANCE = 70;
    const float Z_DISTANCE = 50;

    private static readonly Vector ParkingSpot = new Vector(0f, 0f, -500f);

    private static readonly Dictionary<int, CBaseEntity> cameraProps = new();
    private static readonly Dictionary<int, CPhysicsPropOverride> modelProps = new();

    public static event Action<CCSPlayerController>? InspectionEnded;

    enum CameraMode
    {
        THIRDPERSON, ROTATION
    }

    class CameraStatus
    {
        public CameraMode Mode { get; set; }
        public Vector? Origin { get; set; }
        public CBaseProp? ModelProp { get; set; }
        public required CCSPlayerController Player { get; set; }
        public required CBaseEntity CameraProp { get; set; }
        public int Slot { get; set; }
        public float Times { get; set; }
        public PawnState? Pawn { get; set; }
    }

    class PawnState
    {
        public required CCSPlayerPawn Pawn { get; set; }
        public ulong MeshGroupMask { get; set; }
        public Color Render { get; set; }
        public float ShadowStrength { get; set; }
        public MoveType_t MoveType { get; set; }
        public float Maxspeed { get; set; }
        public List<HiddenEntity> Hidden { get; set; } = new();
    }

    class HiddenEntity
    {
        public required CBaseModelEntity Entity { get; set; }
        public Color Render { get; set; }
    }

    public static Vector CalculatePositionInFront(CCSPlayerController player, float offSetXY, float offSetZ = 0)
    {
        var pawn = player.PlayerPawn.Value;
        float yawAngle = pawn!.EyeAngles!.Y;

        float yawAngleRadians = (float)(yawAngle * Math.PI / 180.0);

        float offsetX = offSetXY * (float)Math.Cos(yawAngleRadians);
        float offsetY = offSetXY * (float)Math.Sin(yawAngleRadians);

        var positionInFront = new Vector
        {
            X = pawn!.AbsOrigin!.X + offsetX,
            Y = pawn!.AbsOrigin!.Y + offsetY,
            Z = pawn!.AbsOrigin!.Z + offSetZ
        };

        return positionInFront;
    }

    public static void UpdateCamera()
    {
        for (int i = 0; i < cameraStatuses.Count; i++)
        {
            var cameraStatus = cameraStatuses[i];
            var player = cameraStatus.Player;

            if (player == null
                || !player.IsValid
                || player.PlayerPawn == null
                || !player.PlayerPawn.IsValid
                )
            {
                cameraStatuses.RemoveAt(i);
                DropProps(cameraStatus.Slot);
                RestorePawn(cameraStatus.Pawn);
                continue;
            }

            if ((player.Buttons & PlayerButtons.Jump) != 0)
            {
                RemoveCamera(player);
                continue;
            }

            var playerPawn = player.PlayerPawn.Value!;

            if (!cameraStatus.CameraProp.IsValid)
            {
                RemoveCamera(player);
                continue;
            }
            if (cameraStatus.Mode == CameraMode.ROTATION)
            {
                KeepPawnHidden(cameraStatus.Pawn);

                var origin = cameraStatus.Origin!;
                float rotationAngle = cameraStatus.Times / MAX_ROTATION_TIMES * 2 * float.Pi - float.Pi; // - float.PI = from back
                float posX = origin.X + float.Cos(rotationAngle) * DISTANCE;
                float posY = origin.Y + float.Sin(rotationAngle) * DISTANCE;

                var cameraOrigin = new Vector(posX, posY, origin.Z + Z_DISTANCE);

                var cameraAngle = new QAngle(0, 360 * (cameraStatus.Times / MAX_ROTATION_TIMES), 0);
                cameraStatus.CameraProp.Teleport(cameraOrigin, cameraAngle, Vector.Zero);

                PlaceModel(cameraStatus);
            }
            else if (cameraStatus.Mode == CameraMode.THIRDPERSON)
            {
                cameraStatus.CameraProp.Teleport(CalculatePositionInFront(player, -110, 90), playerPawn.V_angle, Vector.Zero);
            }
            if (cameraStatus.Times >= MAX_ROTATION_TIMES)
            {
                RemoveCamera(player);
            }
            cameraStatus.Times += 1;

        }

        ParkIdleProps();
    }

    private static void ParkIdleProps()
    {
        var busySlots = new HashSet<int>();
        foreach (var cameraStatus in cameraStatuses)
        {
            busySlots.Add(cameraStatus.Slot);
        }

        foreach (var slot in cameraProps.Keys.ToArray())
        {
            if (busySlots.Contains(slot)) continue;

            var prop = cameraProps[slot];
            if (prop == null || !prop.IsValid)
            {
                cameraProps.Remove(slot);
                continue;
            }

            prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
        }

        foreach (var slot in modelProps.Keys.ToArray())
        {
            if (busySlots.Contains(slot)) continue;

            var prop = modelProps[slot];
            if (prop == null || !prop.IsValid)
            {
                modelProps.Remove(slot);
                continue;
            }

            prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
        }
    }

    public static bool HasCamera(CCSPlayerController player)
    {
        for (int i = 0; i < cameraStatuses.Count; i++)
        {
            var cameraStatus = cameraStatuses[i];
            if (cameraStatus.Player != null
                && cameraStatus.Player.IsValid
                && cameraStatus.Player.SteamID == player.SteamID)
            {
                return true;
            }
        }
        return false;
    }

    public static void RemoveCamera(CCSPlayerController player, bool notify = true)
    {
        for (int i = 0; i < cameraStatuses.Count; i++)
        {
            var cameraStatus = cameraStatuses[i];
            var oldPlayer = cameraStatus.Player;
            if (oldPlayer == null || !oldPlayer.IsValid || oldPlayer.PlayerPawn == null || !oldPlayer.PlayerPawn.IsValid)
            {
                cameraStatuses.RemoveAt(i);
                DropProps(cameraStatus.Slot);
                RestorePawn(cameraStatus.Pawn);
                continue;
            }
            if (oldPlayer.SteamID != player.SteamID)
            {
                continue;
            }
            var oldPlayerPawn = oldPlayer.PlayerPawn.Value!;
            oldPlayerPawn.CameraServices!.ViewEntity.Raw = uint.MaxValue;
            Utilities.SetStateChanged(oldPlayerPawn, "CBasePlayerPawn", "m_pCameraServices");
            cameraStatuses.RemoveAt(i);
            if (cameraStatus.Mode == CameraMode.ROTATION)
            {
                RestorePawn(cameraStatus.Pawn);
                HideModelProp(cameraStatus.ModelProp);
            }
            if (notify)
            {
                InspectionEnded?.Invoke(player);
            }
            break;
        }
    }

    private static CameraStatus? FindStatus(CCSPlayerController player)
    {
        for (int i = 0; i < cameraStatuses.Count; i++)
        {
            var cameraStatus = cameraStatuses[i];
            if (cameraStatus.Player != null
                && cameraStatus.Player.IsValid
                && cameraStatus.Player.SteamID == player.SteamID)
            {
                return cameraStatus;
            }
        }
        return null;
    }

    private static CameraMode GetCameraMode()
    {
        var mode = PlayerModelChanger.getInstance().Config.Inspection.Mode;
        return mode == "thirdperson" ? CameraMode.THIRDPERSON : CameraMode.ROTATION;
    }

    public static void InspectModelForPlayer(CCSPlayerController player, string path, Model? model = null)
    {
        var playerPawn = player.PlayerPawn.Value!;
        var slot = player.Slot;
        var mode = GetCameraMode();

        var running = FindStatus(player);
        if (running != null && running.Mode == mode && running.Origin != null)
        {
            running.Times = 0;

            if (mode == CameraMode.ROTATION)
            {
                var preview = GetModelProp(slot, path);
                if (preview != null && preview.IsValid)
                {
                    ShowModel(preview, path, model, player, running.Origin);
                    running.ModelProp = preview;
                    return;
                }
            }
            else
            {
                return;
            }
        }

        RemoveCamera(player, notify: false);

        var _cameraProp = GetCameraProp(slot);

        if (_cameraProp == null || !_cameraProp.IsValid) return;

        playerPawn.CameraServices!.ViewEntity.Raw = _cameraProp.EntityHandle.Raw;
        Utilities.SetStateChanged(playerPawn, "CBasePlayerPawn", "m_pCameraServices");

        CameraStatus? cameraStatus = null;

        if (mode == CameraMode.THIRDPERSON)
        {
            cameraStatus = new CameraStatus
            {
                Mode = CameraMode.THIRDPERSON,
                Origin = playerPawn.AbsOrigin,
                Player = player,
                CameraProp = _cameraProp,
                Slot = slot,
                Times = 0
            };

        }
        else if (mode == CameraMode.ROTATION)
        {
            var originLoc = playerPawn.AbsOrigin!;
            var originLocClone = new Vector(originLoc.X, originLoc.Y, originLoc.Z);

            var prop = GetModelProp(slot, path);
            if (prop == null)
            {
                RestoreCamera(player);
                return;
            }

            ShowModel(prop, path, model, player, originLocClone);

            var pawnState = HidePawn(playerPawn);

            cameraStatus = new CameraStatus
            {
                Mode = CameraMode.ROTATION,
                ModelProp = prop,
                Origin = originLocClone,
                Player = player,
                CameraProp = _cameraProp,
                Slot = slot,
                Times = 0,
                Pawn = pawnState
            };
        }

        if (cameraStatus == null)
        {
            RestoreCamera(player);
            return;
        }

        cameraStatuses.Add(cameraStatus);
    }

    private static CBaseEntity? GetCameraProp(int slot)
    {
        if (cameraProps.TryGetValue(slot, out var cached) && cached.IsValid)
        {
            return cached;
        }

        var camera = CreatePointCamera();
        if (camera == null)
        {
            camera = CreateCameraProp();
        }
        if (camera == null) return null;

        camera.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);

        cameraProps[slot] = camera;
        return camera;
    }

    private static CBaseEntity? CreatePointCamera()
    {
        var camera = Utilities.CreateEntityByName<CPointCamera>("point_camera");
        if (camera == null || !camera.IsValid) return null;

        camera.DispatchSpawn();
        if (!camera.IsValid) return null;

        camera.Active = true;
        Utilities.SetStateChanged(camera, "CPointCamera", "m_bActive");

        camera.FOV = 90f;
        Utilities.SetStateChanged(camera, "CPointCamera", "m_FOV");

        ClearGraphedFlag(camera);

        camera.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
        if (camera.AbsOrigin == null) return null;

        return camera;
    }

    private static CBaseEntity? CreateCameraProp()
    {
        var prop = Utilities.CreateEntityByName<CPhysicsPropMultiplayer>("prop_physics_multiplayer");

        if (prop == null || !prop.IsValid) return null;

        prop.DispatchSpawn();
        if (!prop.IsValid) return null;

        ClearGraphedFlag(prop);

        prop.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_NEVER;
        prop.Collision.SolidFlags = 12;
        prop.Collision.SolidType = SolidType_t.SOLID_VPHYSICS;

        prop.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");

        return prop;
    }

    private static void ClearGraphedFlag(CBaseEntity entity)
    {
        var owner = entity.CBodyComponent?.SceneNode?.Owner?.Entity;
        if (owner == null) return;

        owner.Flags = (uint)(owner.Flags & ~(1 << 2));
    }

    private static CPhysicsPropOverride? GetModelProp(int slot, string path)
    {
        if (modelProps.TryGetValue(slot, out var cached) && cached.IsValid)
        {
            return cached;
        }

        var prop = Utilities.CreateEntityByName<CPhysicsPropOverride>("prop_physics_override");
        if (prop == null || !prop.IsValid) return null;

        prop.DispatchSpawn();
        if (!prop.IsValid)
        {
            prop = Utilities.CreateEntityByName<CPhysicsPropOverride>("prop_physics_override");
            if (prop == null || !prop.IsValid) return null;
            prop.SetModel(path);
            prop.DispatchSpawn();
        }

        prop.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_NEVER;
        prop.Collision.SolidFlags = 12;
        prop.Collision.SolidType = SolidType_t.SOLID_VPHYSICS;

        prop.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");
        prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);

        modelProps[slot] = prop;
        return prop;
    }

    private static void ShowModel(CPhysicsPropOverride prop, string path, Model? model, CCSPlayerController player, Vector origin)
    {
        prop.SetModel(path);

        if (model != null)
        {
            var preference = PlayerModelChanger.getInstance().Service.GetMeshgroupPreference(player, model);
            ulong meshgroupmask = preference.Count > 0
                ? Utils.CalculateMeshgroupmask(preference.ToArray(), model.FixedMeshgroups)
                : (model.DefaultMeshgroupMask ?? 0);
            var sceneNode = prop.CBodyComponent?.SceneNode;
            if (meshgroupmask != 0 && sceneNode != null)
            {
                sceneNode.GetSkeletonInstance().ModelState.MeshGroupMask = meshgroupmask;
                Utilities.SetStateChanged(prop, "CBaseEntity", "m_CBodyComponent");
            }
        }

        prop.Render = Color.FromArgb(255, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");

        prop.Teleport(origin, new QAngle(0, ModelAngle(origin), 0), Vector.Zero);
    }

    private static float ModelAngle(Vector origin)
    {
        return (180 / float.Pi) * float.Atan2(origin.Y, origin.X) + 180;
    }

    private static void PlaceModel(CameraStatus cameraStatus)
    {
        var prop = cameraStatus.ModelProp;
        var origin = cameraStatus.Origin;
        if (prop == null || !prop.IsValid || origin == null) return;

        prop.Teleport(origin, new QAngle(0, ModelAngle(origin), 0), Vector.Zero);
    }

    private static void HideModelProp(CBaseProp? prop)
    {
        if (prop == null || !prop.IsValid) return;

        prop.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");
        prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
    }

    private static PawnState? HidePawn(CCSPlayerPawn pawn)
    {
        if (pawn == null || !pawn.IsValid) return null;

        var movement = pawn.MovementServices;
        var state = new PawnState
        {
            Pawn = pawn,
            MeshGroupMask = pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask,
            Render = pawn.Render,
            ShadowStrength = pawn.ShadowStrength,
            MoveType = pawn.MoveType,
            Maxspeed = movement != null ? movement.Maxspeed : 0f
        };

        pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask = 0;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");

        pawn.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

        pawn.ShadowStrength = 0f;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_flShadowStrength");

        HideWeapons(pawn, state.Hidden);

        SetPawnMoveType(pawn, MoveType_t.MOVETYPE_NONE);
        if (movement != null)
        {
            movement.Maxspeed = 0f;
        }

        return state;
    }

    private static void KeepPawnHidden(PawnState? state)
    {
        if (state == null) return;

        var pawn = state.Pawn;
        if (pawn == null || !pawn.IsValid) return;

        pawn.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

        SetPawnMoveType(pawn, MoveType_t.MOVETYPE_NONE);
        var movement = pawn.MovementServices;
        if (movement != null)
        {
            movement.Maxspeed = 0f;
        }
    }

    private static void RestorePawn(PawnState? state)
    {
        if (state == null) return;

        var pawn = state.Pawn;
        if (pawn == null || !pawn.IsValid) return;

        pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask = state.MeshGroupMask;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        pawn.Render = state.Render;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        pawn.ShadowStrength = state.ShadowStrength;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_flShadowStrength");

        RestoreWeapons(state.Hidden);

        pawn.PreviouslySetMoveType = state.MoveType;
        pawn.MoveType = state.MoveType;
        pawn.ActualMoveType = state.MoveType;
        var movement = pawn.MovementServices;
        if (movement != null)
        {
            movement.Maxspeed = state.Maxspeed;
        }
    }

    private static void SetPawnMoveType(CCSPlayerPawn pawn, MoveType_t moveType)
    {
        pawn.PreviouslySetMoveType = pawn.MoveType;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
    }

    private static void HideWeapons(CCSPlayerPawn pawn, List<HiddenEntity> hidden)
    {
        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null) return;

        var weapons = weaponServices.MyWeapons;
        if (weapons == null) return;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon != null && weapon.IsValid)
            {
                HideEntity(weapon, hidden);
            }
        }
    }

    private static void HideEntity(CBaseModelEntity entity, List<HiddenEntity> hidden)
    {
        if (entity == null || !entity.IsValid) return;
        if (hidden.Exists(h => h.Entity == entity)) return;

        hidden.Add(new HiddenEntity
        {
            Entity = entity,
            Render = entity.Render
        });

        entity.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_clrRender");
    }

    private static void RestoreWeapons(List<HiddenEntity> hidden)
    {
        foreach (var hiddenEntity in hidden)
        {
            if (hiddenEntity.Entity == null || !hiddenEntity.Entity.IsValid) continue;

            hiddenEntity.Entity.Render = hiddenEntity.Render;
            Utilities.SetStateChanged(hiddenEntity.Entity, "CBaseModelEntity", "m_clrRender");
        }

        hidden.Clear();
    }

    private static void DropProps(int slot)
    {
        if (cameraProps.TryGetValue(slot, out var cameraProp))
        {
            if (cameraProp != null && cameraProp.IsValid)
            {
                cameraProp.Remove();
            }
            cameraProps.Remove(slot);
        }

        if (modelProps.TryGetValue(slot, out var modelProp))
        {
            if (modelProp != null && modelProp.IsValid)
            {
                modelProp.Remove();
            }
            modelProps.Remove(slot);
        }
    }

    public static void RemovePlayerProps(int slot)
    {
        DropProps(slot);
    }

    public static void Cleanup()
    {
        foreach (var cameraStatus in cameraStatuses)
        {
            RestorePawn(cameraStatus.Pawn);
            RestoreCamera(cameraStatus.Player);
        }

        foreach (var slot in cameraProps.Keys.ToArray())
        {
            DropProps(slot);
        }

        foreach (var slot in modelProps.Keys.ToArray())
        {
            DropProps(slot);
        }

        cameraStatuses.Clear();
    }

    private static void RestoreCamera(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn != null && pawn.IsValid && pawn.CameraServices != null)
        {
            pawn.CameraServices.ViewEntity.Raw = uint.MaxValue;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pCameraServices");
        }
    }
}
