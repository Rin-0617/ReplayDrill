using System.Numerics;

namespace ReplayDrill.Geometry;

public enum AoeShape
{
    Circle,
    Donut,
    Line,
    Cone,

    /// <summary>十字。Length=中心からの腕の長さ、HalfWidth=腕の半幅。</summary>
    Cross,
}

/// <summary>
/// Lumina の Action 行から導いた AoE の形。RaidLayer の ActionGeometry と同じ解釈。
/// </summary>
public sealed record AoeGeometry(
    uint ActionId,
    string Name,
    AoeShape Shape,
    float Length,
    float InnerRadius,
    float HalfWidth,
    float ConeHalfAngleDeg,
    bool GroundTargeted,
    bool AddCasterHitbox,
    bool IsCharge,
    int CastType,
    bool IsPlayerAction)
{
    /// <summary>判定の対象になる範囲攻撃か（単体対象・範囲0・ジョブのスキルは除く）。</summary>
    public bool IsJudgeable => this.CastType != 1 && this.Length > 0.01f && !this.IsPlayerAction;

    /// <summary>
    /// <paramref name="point"/> が範囲内か。高さは見ない。
    /// </summary>
    /// <param name="origin">起点（詠唱者、または設置位置）。</param>
    /// <param name="rotation">向き（ラジアン、ゲームの Rotation と同じく +Z が 0）。</param>
    /// <param name="casterHitbox">詠唱者のヒットボックス半径（+R の技だけ加算）。</param>
    /// <param name="chargeLength">突進の長さ（詠唱者→対象の距離）。</param>
    public bool Contains(Vector3 origin, float rotation, float casterHitbox, float chargeLength, Vector3 point)
    {
        var length = this.IsCharge ? chargeLength : this.Length + (this.AddCasterHitbox ? casterHitbox : 0f);
        var offset = new Vector2(point.X - origin.X, point.Z - origin.Z);

        // ゲームの向きを前方ベクトルに直す（Rotation 0 で +Z を向く）。
        var forward = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
        var right = new Vector2(forward.Y, -forward.X);
        var along = Vector2.Dot(offset, forward);
        var across = Vector2.Dot(offset, right);
        var distance = offset.Length();

        return this.Shape switch
        {
            AoeShape.Circle => distance <= length,
            AoeShape.Donut => distance <= length && distance >= this.InnerRadius,
            AoeShape.Line => along >= 0f && along <= length && MathF.Abs(across) <= this.HalfWidth,
            AoeShape.Cone => distance <= length && AngleBetween(forward, offset) <= this.ConeHalfAngleDeg * MathF.PI / 180f,
            AoeShape.Cross => (MathF.Abs(along) <= length && MathF.Abs(across) <= this.HalfWidth)
                || (MathF.Abs(across) <= length && MathF.Abs(along) <= this.HalfWidth),
            _ => false,
        };
    }

    private static float AngleBetween(Vector2 forward, Vector2 offset)
    {
        if (offset.LengthSquared() < 1e-6f)
        {
            return 0f;
        }

        var cos = Vector2.Dot(forward, Vector2.Normalize(offset));
        return MathF.Acos(Math.Clamp(cos, -1f, 1f));
    }
}
