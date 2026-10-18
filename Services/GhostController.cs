using System.Numerics;
using System.Runtime.InteropServices;
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
/// リプレイ再生中にキーボード・マウス・パッドで動かす「分身」。選んだ登場人物の位置と向きを、
/// リプレイ側の書き込みを差し替えて分身のものに保つ。
/// </summary>
public sealed unsafe class GhostController : IDisposable
{
    /// <summary>スティックの遊び。これより小さい傾きは無視する。</summary>
    private const float StickDeadzone = 0.15f;

    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IKeyState keyState;
    private readonly IGamepadState gamepad;
    private readonly IObjectTable objectTable;
    private readonly Hook<ClientGameObject.Delegates.SetPosition>? setPositionHook;
    private readonly Hook<ClientGameObject.Delegates.SetRotation>? setRotationHook;

    /// <summary>位置を差し替えている登場人物のアドレス。0 なら差し替えない。</summary>
    private nint pinnedAddress;

    public GhostController(Configuration configuration, IFramework framework, ICondition condition, IKeyState keyState, IGamepadState gamepad, IObjectTable objectTable, IGameInteropProvider interop, IPluginLog log)
    {
        this.configuration = configuration;
        this.framework = framework;
        this.condition = condition;
        this.keyState = keyState;
        this.gamepad = gamepad;
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

        this.PinAnchor();
    }

    private void Move(IFramework framework)
    {
        // チャットや ImGui の入力欄に文字を打っている間と、ゲームの窓が前面にない間は動かさない。
        var typing = ImGui.GetIO().WantTextInput;
        var focused = IsGameFocused();
        var cameraOk = TryGetCameraForward(out var forward);

        // ゲームのキー設定で移動に割り当てたキーを、Windows から直接読む。ゲームのキー状態は
        // 下で消すので、そちらを読むと押しっぱなしが「押していない」に見えてしまう。
        var w = MoveBindings.IsDown(MoveAction.Forward);
        var a = MoveBindings.IsDown(MoveAction.Left);
        var s = MoveBindings.IsDown(MoveAction.Back);
        var d = MoveBindings.IsDown(MoveAction.Right);

        // FF14 と同じく、マウスの左右ボタンを同時に押している間は前進。窓の上でのクリックは除く。
        var mouseForward = IsDown(VirtualKey.LBUTTON) && IsDown(VirtualKey.RBUTTON) && !ImGui.GetIO().WantCaptureMouse;
        var stick = this.ReadLeftStick();
        this.Diagnostics = $"{MoveBindings.Describe()}  前:{w} 後:{s} 左:{a} 右:{d} マウス前進:{mouseForward} スティック:{stick.X:0.00},{stick.Y:0.00} 入力中:{typing} 前面:{focused} 一時停止:{this.IsPaused} カメラ:{cameraOk} "
            + $"位置:{this.Position.X:0.0},{this.Position.Y:0.0},{this.Position.Z:0.0}";
        if (typing || !focused)
        {
            return;
        }

        var input = new Vector2((d ? 1f : 0f) - (a ? 1f : 0f), (w || mouseForward ? 1f : 0f) - (s ? 1f : 0f)) + stick;

        if (this.configuration.SwallowMoveKeys)
        {
            foreach (var key in MoveBindings.Keys())
            {
                this.keyState[key] = false;
            }
        }

        if (input == Vector2.Zero || !cameraOk)
        {
            return;
        }

        // カメラの向きを基準に、前後左右へ動かす。
        var right = new Vector2(-forward.Y, forward.X);
        var direction = Vector2.Normalize((forward * input.Y) + (right * input.X));

        // スティックを少しだけ倒したときは、その分ゆっくり歩く。
        var strength = MathF.Min(1f, input.Length());
        // 一時停止中も、立ち位置を決められるように等速で動かす。
        var scale = this.configuration.ScaleWithPlaybackSpeed && !this.IsPaused ? this.PlaybackSpeed : 1f;
        var speed = this.configuration.MoveSpeed * scale * strength;
        var step = direction * speed * (float)framework.UpdateDelta.TotalSeconds;

        var next = this.Position + new Vector3(step.X, 0f, step.Y);
        next.Y = GroundHeight(next) ?? this.Position.Y;
        this.Position = next;
        this.Rotation = MathF.Atan2(direction.X, direction.Y);
    }

    /// <summary>
    /// 出発点にした登場人物を、分身の位置へ置き直す。リプレイの再生は
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

    /// <summary>左スティックの傾き（-1..1、上が +Y）。遊びの範囲は 0 にする。</summary>
    private Vector2 ReadLeftStick()
    {
        var stick = this.gamepad.LeftStick;

        // 生の値（-99..99）で返る環境にも合わせる。
        if (stick.Length() > 1.5f)
        {
            stick /= 99f;
        }

        return stick.Length() < StickDeadzone ? Vector2.Zero : stick;
    }

    private static bool IsDown(VirtualKey key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static bool IsGameFocused()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

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
