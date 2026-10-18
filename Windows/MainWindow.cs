using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ReplayDrill.Services;

namespace ReplayDrill.Windows;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly GhostController ghost;
    private readonly HitJudge judge;
    private ulong anchorId;

    public MainWindow(Configuration configuration, GhostController ghost, HitJudge judge)
        : base($"ReplayDrill {Plugin.VersionText}###ReplayDrillMain")
    {
        this.configuration = configuration;
        this.ghost = ghost;
        this.judge = judge;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 260),
            MaximumSize = new Vector2(1000, 1200),
        };
        this.TitleBarButtons.Add(SupportLink.CreateTitleBarButton());
    }

    public override void Draw()
    {
        if (!this.ghost.IsReplay)
        {
            ImGui.TextWrapped("A Realm Recorded でリプレイを再生すると、練習を始められます。");
            ImGui.Separator();
            this.DrawSettings();
            return;
        }

        this.DrawPractice();
        ImGui.Separator();
        this.DrawResults();
        ImGui.Separator();
        this.DrawSettings();
    }

    private void DrawPractice()
    {
        var candidates = this.ghost.Candidates.ToList();
        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("プレイヤーキャラが見つかりません。");
            return;
        }

        if (candidates.All(c => c.GameObjectId != this.anchorId))
        {
            this.anchorId = candidates[0].GameObjectId;
        }

        var anchor = candidates.First(c => c.GameObjectId == this.anchorId);
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("分身の出発点", anchor.Name.TextValue))
        {
            foreach (var candidate in candidates)
            {
                if (ImGui.Selectable(candidate.Name.TextValue, candidate.GameObjectId == this.anchorId))
                {
                    this.anchorId = candidate.GameObjectId;
                }
            }

            ImGui.EndCombo();
        }

        if (!this.ghost.Active)
        {
            if (ImGui.Button("練習を始める"))
            {
                this.judge.Reset();
                this.ghost.Start(anchor);
            }

            ImGui.SameLine();
            ImGui.TextDisabled("始めると、選んだ人の足元に分身が出ます。");
            return;
        }

        if (ImGui.Button("やめる"))
        {
            this.ghost.Stop();
        }

        ImGui.SameLine();
        if (ImGui.Button("出発点に戻す"))
        {
            this.ghost.Start(anchor);
        }

        ImGui.TextDisabled($"WASDで移動（カメラの向きが前）  再生速度 x{this.ghost.PlaybackSpeed:0.##}{(this.ghost.IsPaused ? "（一時停止中）" : string.Empty)}");
    }

    private void DrawResults()
    {
        var total = this.judge.Hits + this.judge.Dodges;
        var rate = total == 0 ? 0 : this.judge.Dodges * 100 / total;
        ImGui.TextUnformatted($"回避 {this.judge.Dodges}  被弾 {this.judge.Hits}  回避率 {rate}%");
        ImGui.SameLine();
        if (ImGui.SmallButton("記録を消す"))
        {
            this.judge.Reset();
        }

        if (!ImGui.BeginTable("results", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV, new Vector2(0, 220)))
        {
            return;
        }

        ImGui.TableSetupColumn("時刻", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("結果", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("技");
        ImGui.TableSetupColumn("形", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var result in this.judge.Results)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.At.ToString("HH:mm:ss"));
            ImGui.TableNextColumn();
            switch (result.Outcome)
            {
                case JudgeOutcome.Hit:
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "被弾");
                    break;
                case JudgeOutcome.Dodged:
                    ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), "回避");
                    break;
                default:
                    ImGui.TextDisabled("対象者");
                    break;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(result.ActionName);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Action {result.ActionId} / {result.Caster}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ShapeName(result.Shape));
        }

        ImGui.EndTable();
    }

    private void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("設定"))
        {
            return;
        }

        var changed = false;
        var speed = this.configuration.MoveSpeed;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("移動速度（ヤルム/秒）", ref speed, 1f, 20f, "%.1f"))
        {
            this.configuration.MoveSpeed = speed;
            changed = true;
        }

        var scale = this.configuration.ScaleWithPlaybackSpeed;
        if (ImGui.Checkbox("再生速度に合わせて速くする", ref scale))
        {
            this.configuration.ScaleWithPlaybackSpeed = scale;
            changed = true;
        }

        var swallow = this.configuration.SwallowMoveKeys;
        if (ImGui.Checkbox("練習中はWASDをゲームに渡さない", ref swallow))
        {
            this.configuration.SwallowMoveKeys = swallow;
            changed = true;
        }

        var radius = this.configuration.GhostRadius;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("印の大きさ", ref radius, 0.2f, 2f, "%.1f"))
        {
            this.configuration.GhostRadius = radius;
            changed = true;
        }

        if (changed)
        {
            this.configuration.Save();
        }

        ImGui.TextDisabled($"判定フック: {this.judge.HookStatus}");
    }

    private static string ShapeName(Geometry.AoeShape shape) => shape switch
    {
        Geometry.AoeShape.Circle => "円",
        Geometry.AoeShape.Donut => "ドーナツ",
        Geometry.AoeShape.Line => "直線",
        Geometry.AoeShape.Cone => "扇",
        Geometry.AoeShape.Cross => "十字",
        _ => "?",
    };
}
