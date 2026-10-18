using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ReplayDrill;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;

    /// <summary>分身の移動速度（ヤルム/秒）。通常の走りは 6。</summary>
    public float MoveSpeed { get; set; } = 6f;

    /// <summary>再生速度に合わせて分身の速度も変える。</summary>
    public bool ScaleWithPlaybackSpeed { get; set; } = true;

    /// <summary>練習中は WASD をゲームに渡さない（ARR のカメラ操作などと競合しないように）。</summary>
    public bool SwallowMoveKeys { get; set; } = true;

    /// <summary>分身の印の半径（ヤルム）。見た目だけで、判定は中心の1点で行う。</summary>
    public float GhostRadius { get; set; } = 0.5f;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        this.Normalize();
    }

    public void Normalize()
    {
        this.MoveSpeed = Math.Clamp(this.MoveSpeed, 1f, 20f);
        this.GhostRadius = Math.Clamp(this.GhostRadius, 0.2f, 2f);
    }

    public void Save()
    {
        this.Normalize();
        this.pluginInterface?.SavePluginConfig(this);
    }
}
