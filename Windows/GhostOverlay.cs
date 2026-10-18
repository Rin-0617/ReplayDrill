using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using ReplayDrill.Services;

namespace ReplayDrill.Windows;

/// <summary>
/// 被弾したとき、キャラの頭上に技の名前を出す。AoE の予兆はリプレイ自身が描くので描かない。
/// </summary>
public sealed class GhostOverlay
{
    private static readonly TimeSpan HitFlash = TimeSpan.FromSeconds(1.2);

    private readonly GhostController ghost;
    private readonly HitJudge judge;
    private readonly IGameGui gameGui;

    public GhostOverlay(GhostController ghost, HitJudge judge, IGameGui gameGui)
    {
        this.ghost = ghost;
        this.judge = judge;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!this.ghost.Active || DateTime.UtcNow - this.judge.LastHitAt >= HitFlash)
        {
            return;
        }

        if (this.gameGui.WorldToScreen(this.ghost.Position + new Vector3(0f, 2.2f, 0f), out var label))
        {
            var text = $"被弾: {this.judge.LastHitName}";
            var size = ImGui.CalcTextSize(text);
            ImGui.GetBackgroundDrawList().AddText(label - (size / 2f), 0xFF3030FFu, text);
        }
    }
}
