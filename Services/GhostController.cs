using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace ReplayDrill.Services;

/// <summary>
/// リプレイ再生中に WASD で動かす「分身」。リプレイの登場人物は動かせないので、
/// 位置だけを持つ仮想の点として扱い、印は <see cref="Windows.GhostOverlay"/> が描く。
/// </summary>
public sealed unsafe class GhostController : IDisposable
{
    private static readonly VirtualKey[] MoveKeys = [VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D];

    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IKeyState keyState;
    private readonly IObjectTable objectTable;
    private readonly Hook<ClientGameObject.Delegates.SetPosition>? setPositionHook;
    private readonly Hook<ClientGameObject.Delegates.SetRotation>? setRotationHook;

    /// <summary>位置を差し替えている登場人物のアドレス。0 なら差し替えない。</summary>
    private nint pinnedAddress;

    public GhostController(Configuration configuration, IFramework framework, ICondition condition, IKeyState keyState, IObjectTable objectTable, IGameInteropProvider interop, IPluginLog log)
    {
        this.configuration = configuration;
        this.framework = framework;
        this.condition = condition;
        this.keyState = keyState;
        this.objectTable = objectTable;

        try
        {
            this.setPositionHook = interop.HookFromAddress<ClientGameObject.Delegates.SetPosition>(ClientGameObject.Addresses.SetPosition.Value, this.SetPositionDetour);
            this.setRotationHook = interop.HookFromAddress<ClientGameObject.Delegates.SetRotation>(ClientGameObject.Addresses.SetRotation.Value, this.SetRotationDetour);
            this.setPositionHook.Enable();
            this.setRotationHook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "SetPosition/SetRotation hook failed.");
        }

        this.framework.Update += this.OnUpdate;
    }

    /// <summary>練習中か（分身を出しているか）。</summary>
    public bool Active { get; private set; }

    public Vector3 Position { get; private set; }

    /// <summary>分身の向き（最後に動いた方向、ゲームの Rotation と同じ表し方）。</summary>
    public float Rotation { get; private set; }

    /// <summary>分身の出発点にした登場人物。</summary>
    public ulong AnchorId { get; private set; }

    /// <summary>動かない原因を調べるための、直近フレームの状態。</summary>
    public string Diagnostics { get; private set; } = string.Empty;

    public bool IsReplay => this.condition[ConditionFlag.DutyRecorderPlayback];

    public bool IsPaused
    {
        get
        {
            var replay = ContentsReplayManager.Instance();
            return replay is not null && replay->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.Paused);
        }
    }

    public float PlaybackSpeed
    {
        get
        {
            var replay = ContentsReplayManager.Instance();
            return replay is null || replay->PlaybackSpeed <= 0f ? 1f : replay->PlaybackSpeed;
        }
    }

    /// <summary>分身を置く起点にできる登場人物（プレイヤーキャラ）。</summary>
    public IEnumerable<IBattleChara> Candidates => this.objectTable.PlayerObjects.OfType<IBattleChara>();

    /// <summary>指定した登場人物の足元に分身を置いて、練習を始める。</summary>
    public void Start(IGameObject anchor)
    {
        this.AnchorId = anchor.GameObjectId;
        this.Position = anchor.Position;
        this.Rotation = anchor.Rotation;
        this.Active = true;
    }

    public void Stop()
    {
        this.Active = false;
        this.pinnedAddress = 0;
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnUpdate;
        this.pinnedAddress = 0;
        this.setPositionHook?.Dispose();
        this.setRotationHook?.Dispose();
    }

    private void OnUpdate(IFramework framework)
    {
        if (!this.Active)
        {
            return;
        }

        if (!this.IsReplay)
        {
            this.Stop();
            return;
        }

        this.Move(framework);

        if (this.configuration.MoveReplayCharacter)
        {
            this.PinAnchor();
        }
        else
        {
            this.pinnedAddress = 0;
        }
    }

    private void Move(IFramework framework)
    {
        // チャットや ImGui の入力欄に文字を打っている間は動かさない。
        var typing = ImGui.GetIO().WantTextInput;
        var cameraOk = TryGetCameraForward(out var forward);
        this.Diagnostics = $"W:{this.keyState[VirtualKey.W]} A:{this.keyState[VirtualKey.A]} S:{this.keyState[VirtualKey.S]} D:{this.keyState[VirtualKey.D]} "
            + $"入力中:{typing} 一時停止:{this.IsPaused} カメラ:{cameraOk} 位置:{this.Position.X:0.0},{this.Position.Y:0.0},{this.Position.Z:0.0}";
        if (typing)
        {
            return;
        }

        var input = Vector2.Zero;
        if (this.keyState[VirtualKey.W])
        {
            input.Y += 1f;
        }

        if (this.keyState[VirtualKey.S])
        {
            input.Y -= 1f;
        }

        if (this.keyState[VirtualKey.D])
        {
            input.X += 1f;
        }

        if (this.keyState[VirtualKey.A])
        {
            input.X -= 1f;
        }

        if (this.configuration.SwallowMoveKeys)
        {
            foreach (var key in MoveKeys)
            {
                this.keyState[key] = false;
            }
        }

        if (input == Vector2.Zero || this.IsPaused || !cameraOk)
        {
            return;
        }

        // カメラの向きを基準に、前後左右へ動かす。
        var right = new Vector2(-forward.Y, forward.X);
        var direction = Vector2.Normalize((forward * input.Y) + (right * input.X));
        var speed = this.configuration.MoveSpeed * (this.configuration.ScaleWithPlaybackSpeed ? this.PlaybackSpeed : 1f);
        var step = direction * speed * (float)framework.UpdateDelta.TotalSeconds;

        var next = this.Position + new Vector3(step.X, 0f, step.Y);
        next.Y = GroundHeight(next) ?? this.Position.Y;
        this.Position = next;
        this.Rotation = MathF.Atan2(direction.X, direction.Y);
    }

    /// <summary>
    /// 出発点にした登場人物を、分身の位置へ置き直す（実験的）。リプレイの再生は
    /// オフラインなので、書き換えても他人には影響しない。リプレイ側がこの人物の位置を
    /// 書き込んでも、<see cref="SetPositionDetour"/> が分身の位置に差し替える。
    /// </summary>
    private void PinAnchor()
    {
        var anchor = this.objectTable.FirstOrDefault(o => o.GameObjectId == this.AnchorId);
        if (anchor is null)
        {
            this.pinnedAddress = 0;
            return;
        }

        this.pinnedAddress = anchor.Address;
        var native = (ClientGameObject*)anchor.Address;
        native->SetPosition(this.Position.X, this.Position.Y, this.Position.Z);
        native->SetRotation(this.Rotation);
    }

    private void SetPositionDetour(ClientGameObject* thisPtr, float x, float y, float z)
    {
        if (this.pinnedAddress != 0 && (nint)thisPtr == this.pinnedAddress)
        {
            (x, y, z) = (this.Position.X, this.Position.Y, this.Position.Z);
        }

        this.setPositionHook!.Original(thisPtr, x, y, z);
    }

    private void SetRotationDetour(ClientGameObject* thisPtr, float value)
    {
        if (this.pinnedAddress != 0 && (nint)thisPtr == this.pinnedAddress)
        {
            value = this.Rotation;
        }

        this.setRotationHook!.Original(thisPtr, value);
    }

    /// <summary>カメラの視線を水平に投影した向き（X, Z）。</summary>
    private static bool TryGetCameraForward(out Vector2 forward)
    {
        forward = Vector2.Zero;
        var manager = GameCameraManager.Instance();
        if (manager is null)
        {
            return false;
        }

        var camera = manager->GetActiveCamera();
        if (camera is null)
        {
            return false;
        }

        var scene = &camera->CameraBase.SceneCamera;
        var look = new Vector3(scene->LookAtVector.X, scene->LookAtVector.Y, scene->LookAtVector.Z);
        var eye = new Vector3(scene->Object.Position.X, scene->Object.Position.Y, scene->Object.Position.Z);
        var flat = new Vector2(look.X - eye.X, look.Z - eye.Z);
        if (flat.LengthSquared() < 1e-6f)
        {
            return false;
        }

        forward = Vector2.Normalize(flat);
        return true;
    }

    /// <summary>真上から下へレイを飛ばして床の高さを取る。段差や坂でも分身が埋まらないように。</summary>
    private static float? GroundHeight(Vector3 position)
    {
        var origin = position + new Vector3(0f, 3f, 0f);
        return BGCollisionModule.RaycastMaterialFilter(origin, -Vector3.UnitY, out var hit, 10f) ? hit.Point.Y : null;
    }
}
