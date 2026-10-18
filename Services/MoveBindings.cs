using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ReplayDrill.Services;

public enum MoveAction
{
    Forward,
    Back,
    Left,
    Right,
}

/// <summary>
/// ゲームのキー設定（キーボード）から、前後左右の移動に割り当てたキーを読む。
/// ゲーム側の入力判定は使わない。練習中は移動キーをゲームに渡さないので、そちらは常に「押していない」になる。
/// </summary>
public static unsafe class MoveBindings
{
    private static readonly Dictionary<MoveAction, InputId[]> Inputs = new()
    {
        [MoveAction.Forward] = [InputId.MOVE_FORE],
        [MoveAction.Back] = [InputId.MOVE_BACK],

        // 「旋回」と「横移動」のどちらに割り当てていても、カメラ基準の横移動として扱う。
        [MoveAction.Left] = [InputId.MOVE_LEFT, InputId.MOVE_STRIFE_L],
        [MoveAction.Right] = [InputId.MOVE_RIGHT, InputId.MOVE_STRIFE_R],
    };

    /// <summary>キー設定を読めないときの割り当て。</summary>
    private static readonly Dictionary<MoveAction, VirtualKey> Fallback = new()
    {
        [MoveAction.Forward] = VirtualKey.W,
        [MoveAction.Back] = VirtualKey.S,
        [MoveAction.Left] = VirtualKey.A,
        [MoveAction.Right] = VirtualKey.D,
    };

    /// <summary>その移動に割り当てたキーのどれかが（修飾キーも含めて）押されているか。</summary>
    public static bool IsDown(MoveAction action)
    {
        var settings = Settings(action);
        if (settings.Count == 0)
        {
            return IsKeyDown((int)Fallback[action]);
        }

        var modifiers = CurrentModifiers();
        return settings.Any(s => IsKeyDown((int)s.Key) && s.KeyModifier == modifiers);
    }

    /// <summary>移動に割り当てたキー（マウスのボタンを除く）。ゲームに渡さないために使う。</summary>
    public static IEnumerable<VirtualKey> Keys()
    {
        foreach (var action in Enum.GetValues<MoveAction>())
        {
            var settings = Settings(action);
            if (settings.Count == 0)
            {
                yield return Fallback[action];
                continue;
            }

            foreach (var setting in settings)
            {
                if ((int)setting.Key > (int)SeVirtualKey.XBUTTON2)
                {
                    yield return (VirtualKey)setting.Key;
                }
            }
        }
    }

    /// <summary>状態表示用。例「前:W 後:S 左:A/Q 右:D/E」。</summary>
    public static string Describe() =>
        string.Join(' ', Enum.GetValues<MoveAction>().Select(action =>
        {
            var settings = Settings(action);
            var keys = settings.Count == 0 ? Fallback[action].ToString() : string.Join('/', settings.Select(s => s.Key.ToString()));
            return $"{Label(action)}:{keys}";
        }));

    private static List<KeySetting> Settings(MoveAction action)
    {
        var result = new List<KeySetting>();
        var input = UIInputData.Instance();
        if (input is null || input->Keybinds is null)
        {
            return result;
        }

        foreach (var id in Inputs[action])
        {
            // キー設定の配列は InputId の番号で並んでいる。
            if ((int)id >= input->NumKeybinds)
            {
                continue;
            }

            foreach (var setting in input->Keybinds[(int)id].KeySettings)
            {
                if (setting.Key != SeVirtualKey.NO_KEY)
                {
                    result.Add(setting);
                }
            }
        }

        return result;
    }

    private static KeyModifierFlag CurrentModifiers()
    {
        var flags = KeyModifierFlag.None;
        if (IsKeyDown((int)VirtualKey.SHIFT))
        {
            flags |= KeyModifierFlag.Shift;
        }

        if (IsKeyDown((int)VirtualKey.CONTROL))
        {
            flags |= KeyModifierFlag.Ctrl;
        }

        if (IsKeyDown((int)VirtualKey.MENU))
        {
            flags |= KeyModifierFlag.Alt;
        }

        return flags;
    }

    private static string Label(MoveAction action) => action switch
    {
        MoveAction.Forward => "前",
        MoveAction.Back => "後",
        MoveAction.Left => "左",
        _ => "右",
    };

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
