using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ReplayDrill.Services;

public enum MarchDirection
{
    Forward,
    Back,
    Left,
    Right,
}

/// <summary>
/// 動かしているキャラに、ノックバック・引き寄せ・強制移動をかける。
/// ノックバックと引き寄せは、技の発動データの効果（<see cref="HitJudge"/> から渡される）と
/// Knockback / Attract シートから。強制移動は、録画のキャラに付いた命令デバフから。
/// </summary>
public sealed partial class ForcedMovement : IDisposable
{
    // ActionEffect の効果の種類。値（Value）がそれぞれ Knockback / Attract シートの行番号。
    // 7.x ではノックバックが 31（つぎつぎトラップのログで Knockback 240 行目を確認）。
    // 引き寄せは同じく1つずれた 32・33 とみなしている。
    public const byte EffectKnockback = 31;
    public const byte EffectAttract1 = 32;
    public const byte EffectAttract2 = 33;

    private const float DefaultSpeed = 20f;

    private readonly GhostController ghost;
    private readonly Configuration configuration;
    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, MarchDirection> marchOrders = [];
    private readonly HashSet<uint> marchStatuses = [];
    private MarchDirection? lastOrder;

    public ForcedMovement(GhostController ghost, Configuration configuration, IDataManager dataManager, IObjectTable objectTable, IFramework framework, IPluginLog log)
    {
        this.ghost = ghost;
        this.configuration = configuration;
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.LoadMarchStatuses();
        this.framework.Update += this.OnUpdate;
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnUpdate;
    }

    /// <summary>Knockback シートの行に従って、キャラを飛ばす。</summary>
    public void ApplyKnockback(ushort row, Vector3 source, float sourceRotation)
    {
        if (this.dataManager.GetExcelSheet<Knockback>().GetRowOrDefault(row) is not { } data || data.Distance == 0)
        {
            return;
        }

        var away = Flat(this.ghost.Position - source);
        var direction = data.Direction switch
        {
            1 => FromAngle(sourceRotation + (data.DirectionArg * MathF.PI / 180f)),
            3 => FromAngle(sourceRotation),
            4 => FromAngle(sourceRotation - (MathF.PI / 2f)),
            5 => FromAngle(sourceRotation + (MathF.PI / 2f)),

            // 0: 発生源から遠ざかる向き。6（対象者を中心に外へ）や 2（ランダム）なども、これで代用する。
            _ => away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : FromAngle(sourceRotation),
        };

        this.ghost.Displace(direction, data.Distance, data.Speed > 0 ? data.Speed : DefaultSpeed);
        this.log.Debug($"[ReplayDrill] Knockback row {row}: {data.Distance}y dir={data.Direction}");
    }

    /// <summary>録画から学習したノックバックがあれば、発生源から外向きにかける。</summary>
    public bool TryApplyLearned(uint actionId, Vector3 source)
    {
        if (!this.configuration.LearnedKnockbacks.TryGetValue(actionId, out var learned) || learned.Distance <= 0f)
        {
            return false;
        }

        var away = Flat(this.ghost.Position - source);
        if (away.LengthSquared() < 1e-6f)
        {
            return false;
        }

        this.ghost.Displace(Vector2.Normalize(away), learned.Distance, DefaultSpeed);
        this.log.Debug($"[ReplayDrill] Learned knockback {actionId} {learned.Name}: {learned.Distance}y");
        return true;
    }

    /// <summary>Attract シートの行に従って、キャラを発生源へ引き寄せる。</summary>
    public void ApplyAttract(ushort row, Vector3 source, float sourceHitbox)
    {
        if (this.dataManager.GetExcelSheet<Attract>().GetRowOrDefault(row) is not { } data)
        {
            return;
        }

        var toward = Flat(source - this.ghost.Position);
        var distance = toward.Length();
        if (distance < 1e-3f)
        {
            return;
        }

        float remain = data.MinRemainingDistance;
        if (data.UseDistanceBetweenHitboxes)
        {
            remain += sourceHitbox + 0.5f;
        }

        var move = Math.Clamp(distance - remain, 0f, data.MaxDistance > 0 ? data.MaxDistance : distance);
        this.ghost.Displace(toward / distance, move, data.Speed > 0 ? data.Speed : DefaultSpeed);
        this.log.Debug($"[ReplayDrill] Attract row {row}: {move:0.0}y");
    }

    /// <summary>
    /// 録画のキャラに付いた命令デバフ（「効果が切れた際に、前方向に移動してしまう」など）を覚えておき、
    /// 「強制移動」の状態になったら、その時のキャラの向きを基準に歩かせる。
    /// </summary>
    private void OnUpdate(IFramework framework)
    {
        if (!this.ghost.Active)
        {
            this.lastOrder = null;
            return;
        }

        if (this.objectTable.FirstOrDefault(o => o.GameObjectId == this.ghost.AnchorId) is not IBattleChara anchor)
        {
            return;
        }

        var marching = false;
        foreach (var status in anchor.StatusList)
        {
            if (this.marchOrders.TryGetValue(status.StatusId, out var order))
            {
                this.lastOrder = order;
            }

            marching |= this.marchStatuses.Contains(status.StatusId);
        }

        if (marching && !this.ghost.IsMarching)
        {
            this.ghost.StartMarch(this.lastOrder ?? MarchDirection.Forward);
            this.lastOrder = null;
        }
        else if (!marching && this.ghost.IsMarching)
        {
            this.ghost.StopMarch();
        }
    }

    /// <summary>
    /// 命令デバフはパッチごとに別の番号があるので、日本語の説明文から方向を読み取る。
    /// クライアントの言語に関係なく、日本語のシートを読む。
    /// </summary>
    private void LoadMarchStatuses()
    {
        try
        {
            foreach (var status in this.dataManager.GetExcelSheet<Status>(ClientLanguage.Japanese))
            {
                var name = status.Name.ExtractText();
                if (name == "強制移動")
                {
                    this.marchStatuses.Add(status.RowId);
                    continue;
                }

                var match = OrderRegex().Match(status.Description.ExtractText());
                if (match.Success)
                {
                    this.marchOrders[status.RowId] = match.Groups[1].Value switch
                    {
                        "前" => MarchDirection.Forward,
                        "後" => MarchDirection.Back,
                        "左" => MarchDirection.Left,
                        _ => MarchDirection.Right,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to load forced march statuses.");
        }

        this.log.Debug($"[ReplayDrill] Forced march: {this.marchOrders.Count} orders, {this.marchStatuses.Count} march statuses.");
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private static Vector2 FromAngle(float angle) => new(MathF.Sin(angle), MathF.Cos(angle));

    [GeneratedRegex("(?:切れた際に?|終了時)、?(前|後|左|右)方向に移動してしまう")]
    private static partial Regex OrderRegex();
}
