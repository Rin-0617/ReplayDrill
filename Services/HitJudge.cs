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
    private readonly AoeGeometryResolver resolver;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private readonly object gate = new();
    private readonly List<PendingEffect> pending = [];
    private readonly List<JudgeResult> results = [];

    public HitJudge(GhostController ghost, AoeGeometryResolver resolver, IObjectTable objectTable, IFramework framework, IGameInteropProvider interop, IPluginLog log)
    {
        this.ghost = ghost;
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
            if (this.ghost.Active && casterPtr != null && header != null && header->ActionType == 1 && !IsFriendly((ClientGameObject*)casterPtr))
            {
                var caster = (ClientGameObject*)casterPtr;
                var targets = new List<ulong>(header->NumTargets);
                for (var i = 0; targetEntityIds != null && i < header->NumTargets; i++)
                {
                    targets.Add(targetEntityIds[i].ObjectId);
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
                    targets);

                lock (this.gate)
                {
                    this.pending.Add(effect);
                }
            }
        }
        catch
        {
            // 判定の失敗で元の処理を壊さない。
        }

        this.hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
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
            .Select(g => g.First() with { Targets = [.. g.SelectMany(e => e.Targets).Distinct()] });
        foreach (var effect in merged)
        {
            if (this.Judge(effect) is { } result)
            {
                this.Record(result);
            }
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
        List<ulong> Targets);
}
