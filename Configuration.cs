using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ReplayDrill;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 4;

    /// <summary>キャラの移動速度（ヤルム/秒）。実際の操作の感覚に近い 7 が既定。</summary>
    public float MoveSpeed { get; set; } = 7f;

    /// <summary>再生速度に合わせて移動速度も変える。</summary>
    public bool ScaleWithPlaybackSpeed { get; set; } = true;

    /// <summary>練習中は移動キーをゲームに渡さない（ARR のカメラ操作などと競合しないように）。</summary>
    public bool SwallowMoveKeys { get; set; } = true;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;

        // v3 までの既定値 6 は、実際の操作より遅く感じた。
        if (this.Version < 4)
        {
            if (this.MoveSpeed == 6f)
            {
                this.MoveSpeed = 7f;
            }

            this.Version = 4;
            this.Save();
        }

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
