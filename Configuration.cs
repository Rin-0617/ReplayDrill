using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

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

    /// <summary>
    /// 録画から学習した、技の効果として届かないノックバック（技の ID → 内容）。
    /// Dalamud の設定読み込みは既定でコレクションに追記するので、置き換えを指定する。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, LearnedKnockback> LearnedKnockbacks { get; set; } = [];

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
        this.LearnedKnockbacks ??= [];
        this.MoveSpeed = Math.Clamp(this.MoveSpeed, 1f, 20f);
    }

    public void Save()
    {
        this.Normalize();
        this.pluginInterface?.SavePluginConfig(this);
    }
}

/// <summary>録画から学習したノックバック。発生源（詠唱者）から外向きに、この距離だけ飛ばす。</summary>
[Serializable]
public sealed class LearnedKnockback
{
    public string Name { get; set; } = string.Empty;

    public float Distance { get; set; }
}
