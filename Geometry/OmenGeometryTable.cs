using System.Text.RegularExpressions;

namespace ReplayDrill.Geometry;

/// <summary>
/// Omen リソースパス (例 <c>vfx/omen/eff/gl_fan120_1bg</c>) から
/// 形状・扇の全角・ドーナツ内径比を推定する。
/// ゲームが実際に描くテレグラフ画像そのものなので、CastType より忠実。
/// </summary>
public static partial class OmenGeometryTable
{
    public sealed record OmenShape(AoeShape Shape, float ConeFullAngleDeg, float InnerRatio);

    // パス基底名 → 形状。代表的なものを初期投入し、必要に応じて追記する。
    private static readonly Dictionary<string, OmenShape> KnownOmens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general_1bf"] = new(AoeShape.Circle, 0f, 0f),
        ["general01f"] = new(AoeShape.Circle, 0f, 0f),
        ["general02f"] = new(AoeShape.Circle, 0f, 0f),
        ["gl_sircle_5m0f"] = new(AoeShape.Circle, 0f, 0f),
        ["gl_circle_5m"] = new(AoeShape.Circle, 0f, 0f),
        ["gl_donut5-15_o0p"] = new(AoeShape.Donut, 0f, 5f / 15f),
        ["gl_donut_5m_10m0f"] = new(AoeShape.Donut, 0f, 0.5f),
        ["gl_fan015"] = new(AoeShape.Cone, 15f, 0f),
        ["gl_fan020"] = new(AoeShape.Cone, 20f, 0f),
        ["gl_fan030"] = new(AoeShape.Cone, 30f, 0f),
        ["gl_fan045"] = new(AoeShape.Cone, 45f, 0f),
        ["gl_fan060"] = new(AoeShape.Cone, 60f, 0f),
        ["gl_fan090"] = new(AoeShape.Cone, 90f, 0f),
        ["gl_fan120"] = new(AoeShape.Cone, 120f, 0f),
        ["gl_fan135"] = new(AoeShape.Cone, 135f, 0f),
        ["gl_fan150"] = new(AoeShape.Cone, 150f, 0f),
        ["gl_fan180"] = new(AoeShape.Cone, 180f, 0f),
        ["gl_point_120_c0h"] = new(AoeShape.Cone, 120f, 0f),
        ["gl_line_1t"] = new(AoeShape.Line, 0f, 0f),
        ["gl_line05t"] = new(AoeShape.Line, 0f, 0f),
        ["gl_sec_line_5m"] = new(AoeShape.Line, 0f, 0f),
    };

    [GeneratedRegex(@"fan_?0*(\d{2,3})", RegexOptions.IgnoreCase)]
    private static partial Regex FanAngleRegex();

    [GeneratedRegex(@"donut_?0*(\d{1,3})[-_]0*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex DonutRangeRegex();

    // ドーナツ Omen の別表記 (例 gl_sircle_1030bf: 内径10/外径30)。
    [GeneratedRegex(@"sircle_(\d{2})(\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex SircleRangeRegex();

    /// <summary>
    /// Omen パスから形状を推定する。分からない場合は <paramref name="fallback"/> の形状を返す。
    /// </summary>
    public static OmenShape Resolve(string? omenPath, AoeShape fallback)
    {
        if (string.IsNullOrWhiteSpace(omenPath))
        {
            return new OmenShape(fallback, 0f, 0f);
        }

        var basename = omenPath.Trim();
        var slash = basename.LastIndexOf('/');
        if (slash >= 0 && slash < basename.Length - 1)
        {
            basename = basename[(slash + 1)..];
        }

        // 実行時に観測する VFX パスは "vfx/omen/eff/xxx.avfx"、Lumina 側は "xxx" なので揃える。
        if (basename.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
        {
            basename = basename[..^5];
        }

        if (KnownOmens.TryGetValue(basename, out var known))
        {
            return known;
        }

        var fan = FanAngleRegex().Match(basename);
        if (fan.Success && int.TryParse(fan.Groups[1].Value, out var angle) && angle is > 0 and <= 360)
        {
            return new OmenShape(AoeShape.Cone, angle, 0f);
        }

        var donut = DonutRangeRegex().Match(basename);
        if (donut.Success
            && float.TryParse(donut.Groups[1].Value, out var inner)
            && float.TryParse(donut.Groups[2].Value, out var outer)
            && outer > inner && outer > 0f)
        {
            return new OmenShape(AoeShape.Donut, 0f, inner / outer);
        }

        // sircle_XXYY は円系 Omen にも使われるので、CastType がドーナツの時だけ内径比として読む。
        var sircle = SircleRangeRegex().Match(basename);
        if (fallback == AoeShape.Donut
            && sircle.Success
            && float.TryParse(sircle.Groups[1].Value, out var a)
            && float.TryParse(sircle.Groups[2].Value, out var b)
            && a > 0f && b > 0f && a != b)
        {
            return new OmenShape(AoeShape.Donut, 0f, MathF.Min(a, b) / MathF.Max(a, b));
        }

        if (basename.Contains("donut", StringComparison.OrdinalIgnoreCase)
            || basename.Contains("ring", StringComparison.OrdinalIgnoreCase))
        {
            return new OmenShape(AoeShape.Donut, 0f, 0f);
        }

        if (basename.Contains("line", StringComparison.OrdinalIgnoreCase)
            || basename.Contains("rect", StringComparison.OrdinalIgnoreCase))
        {
            return new OmenShape(AoeShape.Line, 0f, 0f);
        }

        if (basename.Contains("fan", StringComparison.OrdinalIgnoreCase)
            || basename.Contains("sector", StringComparison.OrdinalIgnoreCase)
            || basename.Contains("cone", StringComparison.OrdinalIgnoreCase))
        {
            return new OmenShape(AoeShape.Cone, 0f, 0f);
        }

        if (basename.Contains("circle", StringComparison.OrdinalIgnoreCase)
            || basename.Contains("sircle", StringComparison.OrdinalIgnoreCase))
        {
            return new OmenShape(AoeShape.Circle, 0f, 0f);
        }

        return new OmenShape(fallback, 0f, 0f);
    }
}
