using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ReplayDrill.Services;

/// <summary>
/// 録画の中で実際に起きたことを観察する。
/// <list type="bullet">
/// <item>
/// 技の効果としては届かず、サーバーからの位置の移動で再現されるノックバック（真空波など）を学習する。
/// 敵の技が当たった約3秒後（再生時間）に、当たった人のほとんどが発生源から外向きにほぼ同じ距離だけ
/// 動いていたら、その技を「外向きに○ヤルム吹き飛ばす」と覚えて設定に保存する。
/// </item>
/// <item>調査用に、ステータスの付き外れと、大きく動いた人をログに出す（Debug レベル）。</item>
/// </list>
/// </summary>
public sealed class MechanicObserver : IDisposable
{
    /// <summary>技が当たってから位置を比べるまでの再生時間（秒）。ノックバックが着地するまで待つ。</summary>
    private const float MoveCheckSeconds = 3f;

    private const float MoveThreshold = 3f;
    private const int MinKnockedPlayers = 3;
    private const float MaxAngleFromAway = 20f;
    /// <summary>中央値からこの範囲にいる人を「同じだけ飛ばされた」とみなす。</summary>
    private const float ClusterTolerance = 1.5f;

    private readonly GhostController ghost;
    private readonly ForcedMovement forced;
    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, HashSet<uint>> statuses = [];
    private readonly List<Snapshot> snapshots = [];
    private readonly List<Candidate> candidates = [];

    /// <summary>練習を始めてからの再生時間（秒）。</summary>
    private float clock;

    public MechanicObserver(GhostController ghost, ForcedMovement forced, Configuration configuration, IObjectTable objectTable, IFramework framework, IPluginLog log)
    {
        this.ghost = ghost;
        this.forced = forced;
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.framework.Update += this.OnUpdate;
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnUpdate;
    }

    /// <summary>敵の技がプレイヤーに当たったときに呼ぶ。当たった人（動かしているキャラ以外）の位置と向きを覚えておく。</summary>
    public void OnEnemyEffect(uint actionId, string actionName, Vector3 casterPosition, IReadOnlyCollection<ulong> targets)
    {
        var players = this.objectTable.PlayerObjects
            .Where(p => p.EntityId != this.ghost.AnchorEntityId && targets.Contains((ulong)p.EntityId))
            .OfType<IBattleChara>()
            .Select(p => new Observed(p.EntityId, p.Name.TextValue, p.Position, p.Rotation, [.. p.StatusList.Select(s => s.StatusId)]))
            .ToList();
        if (players.Count > 0)
        {
            this.snapshots.Add(new Snapshot(actionId, actionName, casterPosition, players) { Start = this.clock });
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!this.ghost.Active)
        {
            this.statuses.Clear();
            this.snapshots.Clear();
            this.candidates.Clear();
            return;
        }

        // 再生時間で数える（倍速や一時停止でも、技が当たってからの経過が録画と合うように）。
        this.clock += this.ghost.IsPaused ? 0f : (float)framework.UpdateDelta.TotalSeconds * this.ghost.PlaybackSpeed;

        this.LogStatusChanges();
        foreach (var snapshot in this.snapshots.Where(s => this.clock - s.Start >= MoveCheckSeconds).ToList())
        {
            this.snapshots.Remove(snapshot);
            this.Analyze(snapshot);
        }

        this.ResolveCandidates();
    }

    /// <summary>
    /// 前後に当たったほかの技の候補と比べて、ノックバックの原因を1つに決めてから保存する。
    /// 近い時刻に当たった技は、同じノックバックの動きを測ってしまうため。
    /// いちばん長い距離を測れた候補（ほぼ同じなら、発生源から真っすぐ外向きに動いていた候補）を採用する。
    /// </summary>
    private void ResolveCandidates()
    {
        foreach (var candidate in this.candidates.ToList())
        {
            // この候補の後3秒以内に当たった技の分析が、すべて終わるまで待つ。
            if (!this.candidates.Contains(candidate) || this.clock - candidate.Start < MoveCheckSeconds * 2f)
            {
                continue;
            }

            var group = this.candidates.Where(c => MathF.Abs(c.Start - candidate.Start) <= MoveCheckSeconds).ToList();
            var longest = group.Max(c => c.Distance);
            var winner = group
                .Where(c => c.Distance >= longest - 1f)
                .OrderBy(c => c.MeanAngle)
                .ThenBy(c => c.Start)
                .First();
            foreach (var member in group)
            {
                this.candidates.Remove(member);
                if (member == winner || member.ActionId == winner.ActionId)
                {
                    continue;
                }

                // 前に誤って覚えていた技なら、一覧から外す。
                if (this.configuration.LearnedKnockbacks.Remove(member.ActionId))
                {
                    this.configuration.Save();
                    this.log.Information($"[ReplayDrill] Forgot knockback: {member.Name} ({member.ActionId}); the movement is explained by {winner.Name}.");
                }
                else
                {
                    this.log.Debug($"[ReplayDrill] Not learned {member.ActionId} {member.Name}: the movement is explained by {winner.Name}.");
                }
            }

            this.Save(winner);
        }
    }

    private void Save(Candidate candidate)
    {
        if (this.configuration.LearnedKnockbacks.TryGetValue(candidate.ActionId, out var known) && MathF.Abs(known.Distance - candidate.Distance) < 0.5f)
        {
            return;
        }

        this.configuration.LearnedKnockbacks[candidate.ActionId] = new LearnedKnockback { Name = candidate.Name, Distance = candidate.Distance };
        this.configuration.Save();
        this.log.Information($"[ReplayDrill] Learned knockback: {candidate.Name} ({candidate.ActionId}) {candidate.Distance}y from {candidate.Count} players.");
    }

    private void Analyze(Snapshot snapshot)
    {
        var moves = new List<(Observed Player, float Distance, float VsAway)>();
        foreach (var player in snapshot.Players)
        {
            if (this.objectTable.PlayerObjects.FirstOrDefault(p => p.EntityId == player.Id) is not { } now)
            {
                continue;
            }

            var moved = new Vector2(now.Position.X - player.Position.X, now.Position.Z - player.Position.Z);
            var away = new Vector2(player.Position.X - snapshot.CasterPosition.X, player.Position.Z - snapshot.CasterPosition.Z);
            var moveAngle = MathF.Atan2(moved.X, moved.Y);
            var vsAway = Degrees(moveAngle - MathF.Atan2(away.X, away.Y));
            // 混沌の風などで距離が変わっていたら、元の距離に割り戻して比べる。
            var multiplier = this.forced.Multiplier(player.Statuses, player.Rotation, player.Position, snapshot.CasterPosition);
            moves.Add((player, moved.Length() / multiplier, vsAway));

            if (moved.Length() >= MoveThreshold)
            {
                this.log.Debug(
                    $"[ReplayDrill] Moved after {snapshot.ActionId} {snapshot.ActionName}: {player.Name} ({player.Id:X}) {moved.Length():0.0}y "
                    + $"vsFacing={Degrees(moveAngle - player.Rotation):0}° vsAwayFromCaster={vsAway:0}°");
            }
        }

        this.TryLearn(snapshot, moves);
    }

    /// <summary>
    /// 当たった人の多くが、発生源から外向きにほぼ同じ距離だけ動いていたら、ノックバックとして覚える。
    /// アームズレングスなどで飛ばなかった人、着地後に歩いた人は外れ値として除き、中央値の近くに集まった人の平均を使う。
    /// </summary>
    private void TryLearn(Snapshot snapshot, List<(Observed Player, float Distance, float VsAway)> moves)
    {
        var knocked = moves.Where(m => m.Distance >= MoveThreshold && MathF.Abs(m.VsAway) <= MaxAngleFromAway).ToList();
        if (knocked.Count < MinKnockedPlayers || knocked.Count < moves.Count * 0.6f)
        {
            if (knocked.Count > 0)
            {
                this.log.Debug($"[ReplayDrill] Not learned {snapshot.ActionId} {snapshot.ActionName}: only {knocked.Count}/{moves.Count} moved outward.");
            }

            return;
        }

        var distances = knocked.Select(m => m.Distance).Order().ToList();
        var median = distances[distances.Count / 2];
        var cluster = distances.Where(d => MathF.Abs(d - median) <= ClusterTolerance).ToList();
        if (cluster.Count < MinKnockedPlayers)
        {
            this.log.Debug($"[ReplayDrill] Not learned {snapshot.ActionId} {snapshot.ActionName}: base distances vary {string.Join(",", distances.Select(d => d.ToString("0.0")))}.");
            return;
        }

        var distance = MathF.Round(cluster.Average() * 2f) / 2f;
        var meanAngle = knocked.Where(m => MathF.Abs(m.Distance - median) <= ClusterTolerance).Average(m => MathF.Abs(m.VsAway));
        this.candidates.Add(new Candidate(snapshot.Start, snapshot.ActionId, snapshot.ActionName, distance, meanAngle, cluster.Count));
    }

    private void LogStatusChanges()
    {
        foreach (var player in this.objectTable.PlayerObjects.OfType<IBattleChara>())
        {
            var now = player.StatusList.Where(s => s.StatusId != 0).ToDictionary(s => s.StatusId, s => s);
            if (!this.statuses.TryGetValue(player.EntityId, out var before))
            {
                this.statuses[player.EntityId] = [.. now.Keys];
                continue;
            }

            foreach (var (id, status) in now)
            {
                if (!before.Contains(id))
                {
                    this.log.Debug($"[ReplayDrill] Status+ {player.Name.TextValue} ({player.EntityId:X}) {id} {status.GameData.ValueNullable?.Name.ExtractText()} {status.RemainingTime:0.0}s");
                }
            }

            foreach (var id in before.Where(id => !now.ContainsKey(id)))
            {
                this.log.Debug($"[ReplayDrill] Status- {player.Name.TextValue} ({player.EntityId:X}) {id}");
            }

            this.statuses[player.EntityId] = [.. now.Keys];
        }
    }

    /// <summary>-180..180 度に丸める。0° なら同じ向き、180° なら逆向き。</summary>
    private static float Degrees(float radians)
    {
        var degrees = radians * 180f / MathF.PI % 360f;
        return degrees switch
        {
            > 180f => degrees - 360f,
            < -180f => degrees + 360f,
            _ => degrees,
        };
    }

    private sealed record Candidate(float Start, uint ActionId, string Name, float Distance, float MeanAngle, int Count);

    private sealed record Observed(uint Id, string Name, Vector3 Position, float Rotation, List<uint> Statuses);

    private sealed class Snapshot(uint actionId, string actionName, Vector3 casterPosition, List<Observed> players)
    {
        public uint ActionId { get; } = actionId;

        public string ActionName { get; } = actionName;

        public Vector3 CasterPosition { get; } = casterPosition;

        public List<Observed> Players { get; } = players;

        /// <summary>技が当たった時刻（練習を始めてからの再生時間）。</summary>
        public float Start { get; init; }
    }
}
