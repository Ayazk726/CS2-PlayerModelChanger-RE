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

    /// <summary>
    /// The place where the props wait while no inspection is running. They are
    /// invisible there and they get no collision, so nobody sees them.
    /// </summary>
    private static readonly Vector ParkingSpot = new Vector(0f, 0f, -500f);

    /// <summary>
    /// The entities of the inspection are kept between two inspections.
    /// <para>
    /// A physics prop has to be born before it can get its model, and the
    /// engine prints "prop_physics_x at (0, 0, 0) has no model name!" for
    /// every prop that is born without one. With the cache that message shows
    /// up once per slot instead of on every model change.
    /// </para>
    /// </summary>
    private static readonly Dictionary<int, CBaseEntity> cameraProps = new();
    private static readonly Dictionary<int, CPhysicsPropOverride> modelProps = new();

    /// <summary>
    /// Raised when a player's inspection camera is gone. The plugin listens to
    /// it to bring back the menu it hid while the model was shown.
    /// </summary>
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
        /// <summary>
        /// The state of the player while the model is shown. He stays where he
        /// is, he is hidden and he may not move.
        /// </summary>
        public PawnState? Pawn { get; set; }
    }

    /// <summary>
    /// Everything the inspection changes about the player. It is stored so
    /// that the player can be put back exactly as he was.
    /// </summary>
    class PawnState
    {
        public required CCSPlayerPawn Pawn { get; set; }
        public ulong MeshGroupMask { get; set; }
        public Color Render { get; set; }
        public float ShadowStrength { get; set; }
        public MoveType_t MoveType { get; set; }
        public float Maxspeed { get; set; }
        /// <summary>
        /// The weapons that were hidden for this inspection. They get their
        /// old look back when the inspection ends.
        /// </summary>
        public List<HiddenEntity> Hidden { get; set; } = new();
    }

    /// <summary>
    /// The look of an entity that was hidden for an inspection.
    /// </summary>
    class HiddenEntity
    {
        public required CBaseModelEntity Entity { get; set; }
        public Color Render { get; set; }
    }

    public static Vector CalculatePositionInFront(CCSPlayerController player, float offSetXY, float offSetZ = 0)
    {
        var pawn = player.PlayerPawn.Value;
        // Extract yaw angle from player's rotation QAngle
        float yawAngle = pawn!.EyeAngles!.Y;

        // Convert yaw angle from degrees to radians
        float yawAngleRadians = (float)(yawAngle * Math.PI / 180.0);

        // Calculate offsets in x and y directions
        float offsetX = offSetXY * (float)Math.Cos(yawAngleRadians);
        float offsetY = offSetXY * (float)Math.Sin(yawAngleRadians);

        // Calculate position in front of the player
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
                // The player stays hidden and where he is, he may not move
                // while the model is shown.
                KeepPawnHidden(cameraStatus.Pawn);

                var origin = cameraStatus.Origin!;
                float rotationAngle = cameraStatus.Times / MAX_ROTATION_TIMES * 2 * float.Pi - float.Pi; // - float.PI = from back
                float posX = origin.X + float.Cos(rotationAngle) * DISTANCE;
                float posY = origin.Y + float.Sin(rotationAngle) * DISTANCE;

                var cameraOrigin = new Vector(posX, posY, origin.Z + Z_DISTANCE);

                var cameraAngle = new QAngle(0, 360 * (cameraStatus.Times / MAX_ROTATION_TIMES), 0);
                cameraStatus.CameraProp.Teleport(cameraOrigin, cameraAngle, Vector.Zero);

                // The model is put back on its place on every tick, so that it
                // neither falls nor rolls away.
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

    /// <summary>
    /// The props of a finished inspection wait hidden at the parking spot.
    /// They are moved there every tick, so they neither fall out of the world
    /// nor gather speed while nobody looks at them.
    /// </summary>
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

            // It is already hidden, it only has to stay in place.
            prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
        }
    }

    /// <summary>
    /// True while this player has an inspection camera. The plugin uses it to
    /// tell "the model is being shown" from "nothing is happening".
    /// </summary>
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

    /// <summary>
    /// Drop the inspection camera of this player. The entities stay in the
    /// world, they are used again by the next model.
    /// <paramref name="notify"/> is false when the camera is only replaced by a
    /// new one, so that the plugin does not think the inspection ended.
    /// </summary>
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
                // The player gets his model, his weapons and his movement
                // back. He never left his place, so there is nothing to put
                // him back to.
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

    /// <summary>
    /// The inspection of this player, if one is running right now.
    /// </summary>
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

    /// <summary>
    /// The way the model is shown, taken from the configuration.
    /// </summary>
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

        // An inspection that is already running is only restarted: the player
        // keeps his place and the entities are not built again. That keeps the
        // engine from complaining about props without a model on every change.
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

        // The old inspection is over. The player gets his camera and his place
        // back, the entities stay in the world for the next model.
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
                // No preview prop: the player keeps his own camera and his place.
                RestoreCamera(player);
                return;
            }

            ShowModel(prop, path, model, player, originLocClone);

            // The player stays where he is. He is hidden and he may not move,
            // so that the model of the prop is the only thing in the picture.
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

    /// <summary>
    /// The camera of the inspection. It is built once and kept for the next
    /// model, so the engine does not complain about it again.
    /// <para>
    /// It is a point_camera and not a physics prop: a prop without a model of
    /// its own is drawn as the error model by every client, and the color of
    /// the entity does not change that. A point_camera is not a model entity
    /// at all, so there is nothing to draw.
    /// </para>
    /// </summary>
    private static CBaseEntity? GetCameraProp(int slot)
    {
        if (cameraProps.TryGetValue(slot, out var cached) && cached.IsValid)
        {
            return cached;
        }

        var camera = CreatePointCamera();
        if (camera == null)
        {
            // The point camera could not be made, the prop of the original
            // plugin is used then. It shows the error model, but it works.
            camera = CreateCameraProp();
        }
        if (camera == null) return null;

        // It waits out of the way until the inspection uses it.
        camera.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);

        cameraProps[slot] = camera;
        return camera;
    }

    /// <summary>
    /// The camera of the inspection: a point_camera. It has no model of its
    /// own, so no client draws an error model for it.
    /// </summary>
    private static CBaseEntity? CreatePointCamera()
    {
        var camera = Utilities.CreateEntityByName<CPointCamera>("point_camera");
        if (camera == null || !camera.IsValid) return null;

        camera.DispatchSpawn();
        if (!camera.IsValid) return null;

        // The camera has to be switched on, otherwise it is not used at all.
        camera.Active = true;
        Utilities.SetStateChanged(camera, "CPointCamera", "m_bActive");

        // The field of view of the camera is the field of view of the player.
        camera.FOV = 90f;
        Utilities.SetStateChanged(camera, "CPointCamera", "m_FOV");

        ClearGraphedFlag(camera);

        // A camera without a place of its own can not be moved, then the
        // prop of the original plugin is used instead.
        camera.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
        if (camera.AbsOrigin == null) return null;

        return camera;
    }

    /// <summary>
    /// The camera of the inspection as a physics prop. It has no model of its
    /// own, so the engine draws the error model in its place.
    /// </summary>
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

        // The color has to be marked as changed, without that the clients
        // keep drawing the error model.
        prop.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");

        return prop;
    }

    /// <summary>
    /// Takes the entity out of the world graph, so that the engine does not
    /// put it somewhere else.
    /// </summary>
    private static void ClearGraphedFlag(CBaseEntity entity)
    {
        var owner = entity.CBodyComponent?.SceneNode?.Owner?.Entity;
        if (owner == null) return;

        owner.Flags = (uint)(owner.Flags & ~(1 << 2));
    }

    /// <summary>
    /// The prop that shows the model while the camera walks around it.
    /// <para>
    /// It is built once and kept for the next model, only its model changes.
    /// </summary>
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
            // The prop did not survive a spawn without a model: fall back to the
            // old order, which shows the model but prints the engine assertion.
            prop = Utilities.CreateEntityByName<CPhysicsPropOverride>("prop_physics_override");
            if (prop == null || !prop.IsValid) return null;
            prop.SetModel(path);
            prop.DispatchSpawn();
        }

        // The prop stands where the player stands, it must not push him away
        // and it must not fall through the world either. It is put back on
        // its place on every tick.
        prop.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_NEVER;
        prop.Collision.SolidFlags = 12;
        prop.Collision.SolidType = SolidType_t.SOLID_VPHYSICS;

        // The prop stays in the world after the inspection, so it waits hidden
        // at the parking spot. The color has to be marked as changed, without
        // that the clients keep drawing the model.
        prop.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");
        prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);

        modelProps[slot] = prop;
        return prop;
    }

    /// <summary>
    /// Puts the model into the preview prop and shows it at the given place.
    /// </summary>
    private static void ShowModel(CPhysicsPropOverride prop, string path, Model? model, CCSPlayerController player, Vector origin)
    {
        prop.SetModel(path);

        if (model != null)
        {
            // The prop shows the model the way the player wears it: the mesh
            // groups the player picked, or the ones of the model when there is
            // no choice of the player yet.
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

        // Visible again.
        prop.Render = Color.FromArgb(255, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");

        // not sure what the fuck is this but it can resolve model have weird pose
        prop.Teleport(origin, new QAngle(0, ModelAngle(origin), 0), Vector.Zero);
    }

    /// <summary>
    /// The direction the model on the prop faces.
    /// </summary>
    private static float ModelAngle(Vector origin)
    {
        return (180 / float.Pi) * float.Atan2(origin.Y, origin.X) + 180;
    }

    /// <summary>
    /// Puts the model on its place. It stands where the player stands and it
    /// is put back on its place on every tick, so that it neither falls nor
    /// rolls away.
    /// </summary>
    private static void PlaceModel(CameraStatus cameraStatus)
    {
        var prop = cameraStatus.ModelProp;
        var origin = cameraStatus.Origin;
        if (prop == null || !prop.IsValid || origin == null) return;

        prop.Teleport(origin, new QAngle(0, ModelAngle(origin), 0), Vector.Zero);
    }

    /// <summary>
    /// Hides the preview prop and parks it, so nobody sees it while it waits
    /// for the next model.
    /// </summary>
    private static void HideModelProp(CBaseProp? prop)
    {
        if (prop == null || !prop.IsValid) return;

        prop.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");
        prop.Teleport(ParkingSpot, new QAngle(0f, 0f, 0f), Vector.Zero);
    }

    /// <summary>
    /// Hides the player and stops him from moving. He stays where he is, so
    /// that nobody sees him fall out of the map and so that the preview prop
    /// can stand on his place.
    /// <para>
    /// The mesh groups are the reliable way to hide him: cs2kz writes the
    /// color of the player on every tick (!hidelegs), so a transparent player
    /// would be back on the screen right away.
    /// </para>
    /// </summary>
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

        // Nothing of the model of the player is drawn any more.
        pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask = 0;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");

        // The color is the second safety net.
        pawn.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

        // No shadow of the player is left behind either.
        pawn.ShadowStrength = 0f;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_flShadowStrength");

        // The weapons would hang in the middle of the model that is shown.
        HideWeapons(pawn, state.Hidden);

        // The player may not move while the model is shown. cs2kz uses the
        // same way to pause a player, so the server does not take it for a
        // cheat.
        SetPawnMoveType(pawn, MoveType_t.MOVETYPE_NONE);
        if (movement != null)
        {
            movement.Maxspeed = 0f;
        }

        return state;
    }

    /// <summary>
    /// Keeps the player hidden and keeps him where he is. Only the small
    /// fields are written again, the body of the player is left alone so that
    /// the animation of the client is not disturbed by it.
    /// </summary>
    private static void KeepPawnHidden(PawnState? state)
    {
        if (state == null) return;

        var pawn = state.Pawn;
        if (pawn == null || !pawn.IsValid) return;

        // cs2kz writes the color of the player on every tick, so it has to be
        // written again here as well.
        pawn.Render = Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");

        // The player may not move while the model is shown.
        SetPawnMoveType(pawn, MoveType_t.MOVETYPE_NONE);
        var movement = pawn.MovementServices;
        if (movement != null)
        {
            movement.Maxspeed = 0f;
        }
    }

    /// <summary>
    /// Gives the player his model, his weapons and his movement back.
    /// </summary>
    private static void RestorePawn(PawnState? state)
    {
        if (state == null) return;

        var pawn = state.Pawn;
        if (pawn == null || !pawn.IsValid) return;

        // The model of the player comes back.
        pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask = state.MeshGroupMask;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        pawn.Render = state.Render;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        pawn.ShadowStrength = state.ShadowStrength;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_flShadowStrength");

        // The weapons come back too.
        RestoreWeapons(state.Hidden);

        // The player may move again.
        pawn.PreviouslySetMoveType = state.MoveType;
        pawn.MoveType = state.MoveType;
        pawn.ActualMoveType = state.MoveType;
        var movement = pawn.MovementServices;
        if (movement != null)
        {
            movement.Maxspeed = state.Maxspeed;
        }
    }

    /// <summary>
    /// Changes the move type of the player the way the engine does it, so
    /// that the server and cs2kz notice the change.
    /// </summary>
    private static void SetPawnMoveType(CCSPlayerPawn pawn, MoveType_t moveType)
    {
        pawn.PreviouslySetMoveType = pawn.MoveType;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
    }

    /// <summary>
    /// Hides the weapons of the player while the model is shown.
    /// </summary>
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

    /// <summary>
    /// Hides an entity from every client.
    /// </summary>
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

    /// <summary>
    /// Gives the hidden entities their old look back.
    /// </summary>
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

    /// <summary>
    /// Drops the entities of this slot for good.
    /// </summary>
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

    /// <summary>
    /// Drops the entities of a player that left the server.
    /// </summary>
    public static void RemovePlayerProps(int slot)
    {
        DropProps(slot);
    }

    /// <summary>
    /// Drops every entity of the plugin.
    /// </summary>
    public static void Cleanup()
    {
        // A player that is in the middle of an inspection must not stay
        // hidden or frozen when the plugin goes away.
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

    /// <summary>Give the camera back to the player.</summary>
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
