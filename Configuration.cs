using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ReplayDrill;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 3;

    /// <summary>キャラの移動速度（ヤルム/秒）。通常の走りは 6。</summary>
    public float MoveSpeed { get; set; } = 6f;

    /// <summary>再生速度に合わせて移動速度も変える。</summary>
    public bool ScaleWithPlaybackSpeed { get; set; } = true;

    /// <summary>練習中は WASD をゲームに渡さない（ARR のカメラ操作などと競合しないように）。</summary>
    public bool SwallowMoveKeys { get; set; } = true;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        this.Normalize();
    }

    public void Normalize()
    {
        this.MoveSpeed = Math.Clamp(this.MoveSpeed, 1f, 20f);
    }

    public void Save()
    {
        this.Normalize();
        this.pluginInterface?.SavePluginConfig(this);
    }
}
