using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ReplayDrill.Geometry;
using ActionEffectHandler = FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler;
using ClientCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ClientGameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;

namespace ReplayDrill.Services;

public enum JudgeOutcome
{
    Hit,
    Dodged,

    /// <summary>プレイヤーを中心にした円（散開・頭割りなど）。録画の本人の位置で決まるので判定しない。</summary>
    TargetCentered,

    /// <summary>生きているプレイヤー全員に当たった技。全体攻撃なので避けようがない。</summary>
    Unavoidable,
}

public sealed record JudgeResult(DateTime At, string ActionName, uint ActionId, string Caster, AoeShape Shape, JudgeOutcome Outcome);

/// <summary>
/// 敵の技が実際に発動した瞬間（ActionEffect）に、分身がその範囲内にいたかを判定する。
/// 予兆はリプレイ自身が描くので、ここでは形を計算するだけで何も描かない。
/// </summary>
public sealed unsafe class HitJudge : IDisposable
{
    private const int MaxResults = 200;

    private readonly GhostController ghost;
    private readonly ForcedMovement forced;
    private readonly MechanicObserver observer;
    private readonly AoeGeometryResolver resolver;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private readonly object gate = new();
    private readonly List<PendingEffect> pending = [];
    private readonly List<JudgeResult> results = [];

    public HitJudge(GhostController ghost, ForcedMovement forced, MechanicObserver observer, AoeGeometryResolver resolver, IObjectTable objectTable, IFramework framework, IGameInteropProvider interop, IPluginLog log)
    {
        this.ghost = ghost;
        this.forced = forced;
        this.observer = observer;
        this.resolver = resolver;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;

        try
        {
            this.hook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(ActionEffectHandler.Addresses.Receive.Value, this.ReceiveDetour);
            this.hook.Enable();
            this.HookStatus = "有効";
        }
        catch (Exception ex)
        {
            this.HookStatus = $"無効（{ex.GetBaseException().Message}）";
            log.Error(ex, "ActionEffect hook failed.");
        }

        this.framework.Update += this.OnUpdate;
    }

    public string HookStatus { get; }

    public int Hits { get; private set; }

    public int Dodges { get; private set; }

    /// <summary>最後に被弾した時刻（印を赤くする演出用）。</summary>
    public DateTime LastHitAt { get; private set; } = DateTime.MinValue;

    public string LastHitName { get; private set; } = string.Empty;

    public IReadOnlyList<JudgeResult> Results
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.results];
            }
        }
    }

    public void Reset()
    {
        lock (this.gate)
        {
            this.results.Clear();
            this.pending.Clear();
        }

        this.Hits = 0;
        this.Dodges = 0;
        this.LastHitAt = DateTime.MinValue;
        this.LastHitName = string.Empty;
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnUpdate;
        this.hook?.Dispose();
    }

    private void ReceiveDetour(
        uint casterEntityId,
        ClientCharacter* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        ClientGameObjectId* targetEntityIds)
    {
        try
        {
            if (this.ghost.Active && casterPtr != null && header != null)
            {
                this.Capture(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
            }
        }
        catch
        {
            // 判定の失敗で元の処理を壊さない。
        }

        // 元の処理は必ず呼ぶ（呼ばないと技の効果がゲームに届かない）。
        this.hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
    }

    private void Capture(
        uint casterEntityId,
        ClientCharacter* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        ClientGameObjectId* targetEntityIds)
    {
        var caster = (ClientGameObject*)casterPtr;
        var targets = new List<ulong>(header->NumTargets);
        var displacements = new List<Displacement>();
        var summary = new System.Text.StringBuilder();
        for (var i = 0; targetEntityIds != null && i < header->NumTargets; i++)
        {
            targets.Add(targetEntityIds[i].ObjectId);
            if (effects == null)
            {
                continue;
            }

            foreach (var e in effects[i].Effects)
            {
                // 効果の種類を調べるためのログ用（0=なし、3=ダメージ は省く）。
                if (e.Type is not (0 or 3))
                {
                    summary.Append($" {targetEntityIds[i].ObjectId:X}:{e.Type}/{e.Value}/{e.Param0},{e.Param1},{e.Param2},{e.Param3},{e.Param4}");
                }

                if (e.Type is ForcedMovement.EffectKnockback or ForcedMovement.EffectAttract1 or ForcedMovement.EffectAttract2)
                {
                    displacements.Add(new Displacement(targetEntityIds[i].ObjectId, e.Type, e.Value));
                }
            }
        }

        // 味方（プレイヤー）が発動した技は判定しない。ただし、デバフが切れたときの吹き飛ばしのように、
        // 動かしているキャラ本人が発動して本人が飛ばされるものは、本人の向きを基準にかける。
        var enemyAction = header->ActionType == 1 && !IsFriendly(caster);
        var selfDisplacement = casterEntityId == this.ghost.AnchorEntityId
            && displacements.Any(d => d.TargetId == this.ghost.AnchorEntityId);
        if (!enemyAction && !selfDisplacement)
        {
            if (displacements.Count > 0)
            {
                this.log.Debug($"[ReplayDrill] Skipped friendly effect {header->ActionId} type={header->ActionType} from {caster->NameString} ({casterEntityId:X}) anchor={this.ghost.AnchorEntityId:X}{summary}");
            }

            return;
        }

        var effect = new PendingEffect(
            casterEntityId,
            header->ActionId,
            header->GlobalSequence,
            caster->NameString,
            caster->Position,
            // 発動時の向き（0..65535 → -π..π）。詠唱者の今の向きより正確。
            (header->RotationInt / 65535f * MathF.Tau) - MathF.PI,
            targetPos != null ? *targetPos : caster->Position,
            caster->HitboxRadius,
            header->AnimationTargetId.ObjectId,
            targets,
            displacements)
        {
            SelfCast = !enemyAction,
        };

        lock (this.gate)
        {
            this.pending.Add(effect);
        }

        this.log.Debug($"[ReplayDrill] Effect {header->ActionId} from {caster->NameString} main={header->AnimationTargetId.ObjectId:X} targets={header->NumTargets} anchor={this.ghost.AnchorEntityId:X}{summary}");
    }

    /// <summary>フックの中ではなくフレーム処理で判定する（Lumina とオブジェクト表の参照をフック外に出す）。</summary>
    private void OnUpdate(IFramework framework)
    {
        List<PendingEffect> batch;
        lock (this.gate)
        {
            if (this.pending.Count == 0)
            {
                return;
            }

            batch = [.. this.pending];
            this.pending.Clear();
        }

        // 対象が多い技は複数回に分けて届くので、同じ発動は当たった人をまとめて1回だけ判定する。
        var merged = batch
            .GroupBy(e => (e.CasterId, e.ActionId, e.Sequence))
            .Select(g => g.First() with
            {
                Targets = [.. g.SelectMany(e => e.Targets).Distinct()],
                Displacements = [.. g.SelectMany(e => e.Displacements)],
            });
        foreach (var effect in merged)
        {
            if (effect.SelfCast)
            {
                this.ApplySelfDisplacement(effect);
                continue;
            }

            if (effect.Targets.Any(t => this.objectTable.PlayerObjects.Any(p => p.EntityId == t)))
            {
                var name = this.resolver.Resolve(effect.ActionId)?.Name ?? $"Action {effect.ActionId}";
                this.observer.OnEnemyEffect(effect.ActionId, name, effect.CasterPosition, effect.Targets);
            }

            var result = this.Judge(effect);
            if (result is not null)
            {
                this.Record(result);
            }

            this.ApplyDisplacement(effect, result);
        }
    }

    private JudgeResult? Judge(PendingEffect effect)
    {
        if (this.resolver.Resolve(effect.ActionId) is not { IsJudgeable: true } geometry)
        {
            return null;
        }

        var origin = geometry.GroundTargeted ? effect.TargetPosition : effect.CasterPosition;
        var chargeLength = Vector2.Distance(new(effect.CasterPosition.X, effect.CasterPosition.Z), new(effect.TargetPosition.X, effect.TargetPosition.Z));

        // 円の中心がプレイヤーなら、散開・頭割りのような対象者ギミック。
        if (geometry.Shape is AoeShape.Circle or AoeShape.Donut && this.IsCenteredOnPlayer(geometry, effect))
        {
            return new JudgeResult(DateTime.Now, geometry.Name, geometry.ActionId, effect.CasterName, geometry.Shape, JudgeOutcome.TargetCentered);
        }

        if (this.HitEveryLivingPlayer(effect))
        {
            return new JudgeResult(DateTime.Now, geometry.Name, geometry.ActionId, effect.CasterName, geometry.Shape, JudgeOutcome.Unavoidable);
        }

        var hit = geometry.Contains(origin, effect.Rotation, effect.CasterHitbox, chargeLength, this.ghost.Position);
        return new JudgeResult(DateTime.Now, geometry.Name, geometry.ActionId, effect.CasterName, geometry.Shape, hit ? JudgeOutcome.Hit : JudgeOutcome.Dodged);
    }

    /// <summary>
    /// 技にノックバック・引き寄せが付いていて、動かしているキャラも食らうべきなら、同じものをかける。
    /// <list type="bullet">
    /// <item>範囲で判定できる技は「被弾」か「全体」のとき。起点は詠唱者。</item>
    /// <item>対象者の円（対象者を中心に周りを吹き飛ばすなど）は、キャラが円の中にいて、対象者本人でないとき。起点は円の中心。</item>
    /// <item>判定できない技（単体対象）は、録画でそのキャラ本人が食らっていたとき。
    /// または対象者の周りの人が吹き飛ばされていて、キャラがその範囲の中にいるとき。</item>
    /// </list>
    /// </summary>
    private void ApplyDisplacement(PendingEffect effect, JudgeResult? result)
    {
        if (effect.Displacements.Count == 0)
        {
            // 技の効果として届かないノックバックは、録画から学習したものを使う。
            if (result?.Outcome is JudgeOutcome.Hit or JudgeOutcome.Unavoidable)
            {
                this.forced.TryApplyLearned(effect.ActionId, effect.CasterPosition);
            }

            return;
        }

        var own = effect.Displacements.FirstOrDefault(d => d.TargetId == this.ghost.AnchorEntityId);
        var any = own ?? effect.Displacements[0];
        var source = effect.CasterPosition;
        Displacement? apply;
        switch (result?.Outcome)
        {
            case JudgeOutcome.Hit or JudgeOutcome.Unavoidable:
                apply = any;
                break;
            case JudgeOutcome.Dodged:
                apply = null;
                break;
            case JudgeOutcome.TargetCentered:
                source = this.TargetCenter(effect);
                apply = effect.MainTargetId != this.ghost.AnchorEntityId && this.InsideTargetCircle(effect, source) ? any : null;
                break;
            default:
                apply = own;
                if (apply is null && this.InsideObservedKnockback(effect, out var center))
                {
                    source = center;
                    apply = any;
                }

                break;
        }

        if (apply is null)
        {
            return;
        }

        if (apply.Type == ForcedMovement.EffectKnockback)
        {
            this.forced.ApplyKnockback(apply.Row, source, effect.Rotation);
        }
        else
        {
            this.forced.ApplyAttract(apply.Row, source, effect.CasterHitbox);
        }
    }

    /// <summary>本人が発動して本人が飛ばされる技（デバフが切れたときの吹き飛ばしなど）。向きは今のキャラの向き。</summary>
    private void ApplySelfDisplacement(PendingEffect effect)
    {
        var own = effect.Displacements.FirstOrDefault(d => d.TargetId == this.ghost.AnchorEntityId && d.Type == ForcedMovement.EffectKnockback);
        if (own is not null)
        {
            this.forced.ApplyKnockback(own.Row, this.ghost.Position, this.ghost.Rotation);
        }
    }

    /// <summary>対象者の円の中心。設置型なら着弾点、そうでなければメインの対象の足元。</summary>
    private Vector3 TargetCenter(PendingEffect effect)
    {
        if (this.resolver.Resolve(effect.ActionId) is { GroundTargeted: false }
            && this.objectTable.PlayerObjects.FirstOrDefault(p => p.EntityId == effect.MainTargetId) is { } target)
        {
            return target.Position;
        }

        return effect.TargetPosition;
    }

    /// <summary>
    /// 範囲の大きさがデータから分からない「対象者の周りを吹き飛ばす」技。録画で吹き飛ばされた人のうち、
    /// 中心からいちばん遠い人までを範囲とみなし、キャラがその中にいるか。
    /// </summary>
    private bool InsideObservedKnockback(PendingEffect effect, out Vector3 center)
    {
        center = effect.TargetPosition;
        if (effect.MainTargetId == 0 || effect.MainTargetId == effect.CasterId || effect.MainTargetId == this.ghost.AnchorEntityId)
        {
            return false;
        }

        if (this.objectTable.PlayerObjects.FirstOrDefault(p => p.EntityId == effect.MainTargetId) is not { } main)
        {
            return false;
        }

        center = main.Position;
        var knocked = effect.Displacements
            .Where(d => d.TargetId != effect.MainTargetId && d.TargetId != this.ghost.AnchorEntityId)
            .Select(d => this.objectTable.PlayerObjects.FirstOrDefault(p => p.EntityId == d.TargetId))
            .OfType<Dalamud.Game.ClientState.Objects.Types.IGameObject>()
            .ToList();
        if (knocked.Count == 0)
        {
            return false;
        }

        var c = center;
        var radius = knocked.Max(p => Vector2.Distance(new(p.Position.X, p.Position.Z), new(c.X, c.Z))) + 1f;
        return Vector2.Distance(new(this.ghost.Position.X, this.ghost.Position.Z), new(c.X, c.Z)) <= radius;
    }

    private bool InsideTargetCircle(PendingEffect effect, Vector3 center)
    {
        if (this.resolver.Resolve(effect.ActionId) is not { } geometry)
        {
            return false;
        }

        return geometry.Contains(center, effect.Rotation, 0f, 0f, this.ghost.Position);
    }

    /// <summary>録画の中で、生きているプレイヤー全員に当たったか（全体攻撃）。</summary>
    private bool HitEveryLivingPlayer(PendingEffect effect)
    {
        var living = this.objectTable.PlayerObjects
            .OfType<Dalamud.Game.ClientState.Objects.Types.IBattleChara>()
            .Where(p => p.CurrentHp > 0)
            .Select(p => (ulong)p.EntityId)
            .ToList();
        return living.Count > 1 && living.All(effect.Targets.Contains);
    }

    private bool IsCenteredOnPlayer(AoeGeometry geometry, PendingEffect effect)
    {
        // 設置型でない円は、メインの対象（自分自身でなければ）の足元に出る。
        if (!geometry.GroundTargeted)
        {
            return effect.MainTargetId != 0
                && effect.MainTargetId != effect.CasterId
                && this.objectTable.PlayerObjects.Any(p => p.EntityId == effect.MainTargetId);
        }

        // 設置型でも、プレイヤーの足元にぴったり置かれたものは対象者ギミック。

        foreach (var player in this.objectTable.PlayerObjects)
        {
            var offset = new Vector2(player.Position.X - effect.TargetPosition.X, player.Position.Z - effect.TargetPosition.Z);
            if (offset.LengthSquared() < 0.25f)
            {
                return true;
            }
        }

        return false;
    }

    private void Record(JudgeResult result)
    {
        switch (result.Outcome)
        {
            case JudgeOutcome.Hit:
                this.Hits++;
                this.LastHitAt = DateTime.UtcNow;
                this.LastHitName = result.ActionName;
                break;
            case JudgeOutcome.Dodged:
                this.Dodges++;
                break;
        }

        lock (this.gate)
        {
            this.results.Insert(0, result);
            if (this.results.Count > MaxResults)
            {
                this.results.RemoveAt(this.results.Count - 1);
            }
        }

        this.log.Debug($"[ReplayDrill] {result.Outcome} {result.ActionName} ({result.ActionId}) from {result.Caster} shape={result.Shape}");
    }

    private static bool IsFriendly(ClientGameObject* gameObject)
    {
        var kind = gameObject->ObjectKind;
        if (kind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc)
        {
            return true;
        }

        if (gameObject->OwnerId is not (0 or 0xE0000000))
        {
            return true;
        }

        // BattleNpcSubKind: 2=ペット, 3=チョコボ, 9=フェイスなどの味方NPC
        return kind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc && gameObject->SubKind is 2 or 3 or 9;
    }

    private sealed record PendingEffect(
        uint CasterId,
        uint ActionId,
        uint Sequence,
        string CasterName,
        Vector3 CasterPosition,
        float Rotation,
        Vector3 TargetPosition,
        float CasterHitbox,
        uint MainTargetId,
        List<ulong> Targets,
        List<Displacement> Displacements)
    {
        /// <summary>動かしているキャラ本人が発動した技（判定はせず、本人へのノックバックだけかける）。</summary>
        public bool SelfCast { get; init; }
    }

    /// <summary>技の効果に含まれていたノックバック・引き寄せ（対象・種類・シートの行）。</summary>
    private sealed record Displacement(ulong TargetId, byte Type, ushort Row);
}
