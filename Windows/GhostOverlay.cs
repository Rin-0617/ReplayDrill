using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using ReplayDrill.Services;

namespace ReplayDrill.Windows;

/// <summary>
/// 分身の印（足元の円と向き）をゲーム画面に重ねて描く。AoE の予兆はリプレイ自身が描くので描かない。
/// </summary>
public sealed class GhostOverlay
{
    private const int Segments = 32;
    private static readonly TimeSpan HitFlash = TimeSpan.FromSeconds(1.2);

    private readonly Configuration configuration;
    private readonly GhostController ghost;
    private readonly HitJudge judge;
    private readonly IGameGui gameGui;

    public GhostOverlay(Configuration configuration, GhostController ghost, HitJudge judge, IGameGui gameGui)
    {
        this.configuration = configuration;
        this.ghost = ghost;
        this.judge = judge;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!this.ghost.Active || this.configuration.MoveReplayCharacter)
        {
            return;
        }

        var flashing = DateTime.UtcNow - this.judge.LastHitAt < HitFlash;
        var color = flashing ? 0xFF3030FFu : 0xFFFFC040u;
        var fill = flashing ? 0x603030FFu : 0x40FFC040u;
        var drawList = ImGui.GetBackgroundDrawList();
        var center = this.ghost.Position;
        var radius = this.configuration.GhostRadius;

        // 画面外の点があっても、見えている辺だけ描く。
        var points = new Vector2?[Segments];
        for (var i = 0; i < Segments; i++)
        {
            var angle = i * MathF.Tau / Segments;
            var world = center + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            points[i] = this.gameGui.WorldToScreen(world, out var screen) ? screen : null;
        }

        if (points.All(p => p.HasValue))
        {
            foreach (var point in points)
            {
                drawList.PathLineTo(point!.Value);
            }

            drawList.PathFillConvex(fill);
        }

        for (var i = 0; i < Segments; i++)
        {
            if (points[i] is { } from0 && points[(i + 1) % Segments] is { } to0)
            {
                drawList.AddLine(from0, to0, color, 3f);
            }
        }

        // 向き（最後に動いた方向）を矢印で示す。
        var forward = new Vector3(MathF.Sin(this.ghost.Rotation), 0f, MathF.Cos(this.ghost.Rotation));
        if (this.gameGui.WorldToScreen(center, out var from) && this.gameGui.WorldToScreen(center + (forward * radius * 2f), out var to))
        {
            drawList.AddLine(from, to, color, 3f);
        }

        if (flashing && this.gameGui.WorldToScreen(center + new Vector3(0f, 2.2f, 0f), out var label))
        {
            var text = $"被弾: {this.judge.LastHitName}";
            var size = ImGui.CalcTextSize(text);
            drawList.AddText(label - (size / 2f), 0xFF3030FFu, text);
        }
    }
}
