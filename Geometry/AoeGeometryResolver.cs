using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace ReplayDrill.Geometry;

/// <summary>
/// Lumina の Action シート（CastType / EffectRange / XAxisModifier / Omen）から AoE の形を作る。
/// RaidLayer の ActionGeometryResolver を判定用に移植したもの。
/// </summary>
public sealed class AoeGeometryResolver
{
    private const float DefaultRadius = 8f;
    private const float DefaultLineHalfWidth = 2f;
    private const float DefaultDonutInnerRadius = 8f;
    private const float DefaultConeHalfAngleDeg = 45f;

    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, AoeGeometry?> cache = [];

    public AoeGeometryResolver(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public AoeGeometry? Resolve(uint actionId)
    {
        if (this.cache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        AoeGeometry? geometry = null;
        try
        {
            if (this.dataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId) is { } action)
            {
                geometry = Build(actionId, action);
            }
        }
        catch
        {
            // Action シートのフィールドはパッチで変わり得る。判定できない技として扱う。
        }

        this.cache[actionId] = geometry;
        return geometry;
    }

    private static AoeGeometry Build(uint actionId, LuminaAction action)
    {
        var name = action.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Action {actionId}";
        }

        var castType = Convert.ToInt32(action.CastType);
        var effectRange = Convert.ToSingle(action.EffectRange);
        var xAxis = Convert.ToSingle(action.XAxisModifier);
        var omenPath = OmenPath(action);

        // CastType の意味（BossMod の解釈と同じ）:
        //   2/6 円  5 円+R  7 円（対象地点に設置）  3 扇+R  13 扇
        //   4 直線+R  12 直線  8 突進  10 ドーナツ  11 十字
        var baseShape = castType switch
        {
            3 or 13 => AoeShape.Cone,
            4 or 8 or 12 => AoeShape.Line,
            10 => AoeShape.Donut,
            11 => AoeShape.Cross,
            _ => AoeShape.Circle,
        };

        // 十字は Omen 名に line/rect を含むことがあるため、CastType を優先する。
        var omen = OmenGeometryTable.Resolve(omenPath, baseShape);
        var shape = castType == 11 ? AoeShape.Cross : omen.Shape;
        var groundTargeted = castType == 7 || (castType is not (5 or 6) && action.TargetArea);

        var length = effectRange > 0f ? effectRange : DefaultRadius;
        var halfWidth = xAxis > 0f ? xAxis / 2f : DefaultLineHalfWidth;
        var coneHalfAngle = omen.ConeFullAngleDeg > 0f ? omen.ConeFullAngleDeg / 2f : DefaultConeHalfAngleDeg;
        var innerRadius = shape != AoeShape.Donut ? 0f
            : omen.InnerRatio > 0f ? length * omen.InnerRatio
            : MathF.Min(DefaultDonutInnerRadius, MathF.Max(0f, length - 1f));

        return new AoeGeometry(
            ActionId: actionId,
            Name: name,
            Shape: shape,
            Length: length,
            InnerRadius: innerRadius,
            HalfWidth: halfWidth,
            ConeHalfAngleDeg: coneHalfAngle,
            GroundTargeted: groundTargeted,
            AddCasterHitbox: !groundTargeted && castType is 3 or 4 or 5,
            IsCharge: castType == 8,
            CastType: castType,
            IsPlayerAction: action.IsPlayerAction);
    }

    private static string OmenPath(LuminaAction action)
    {
        try
        {
            return action.Omen.ValueNullable is { } omen ? omen.Path.ExtractText() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
