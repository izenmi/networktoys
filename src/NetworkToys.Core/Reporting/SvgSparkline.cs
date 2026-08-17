using System.Globalization;
using System.Text;

namespace NetworkToys.Core.Reporting;

/// <summary>
/// レポートに埋め込む折れ線を SVG で描く。
///
/// 外部のグラフライブラリは使わない。レポートは 1 ファイル完結で、
/// 客先のオフライン環境でも開ける必要があるため。
/// 純粋な文字列生成なのでテストできる。
/// </summary>
public static class SvgSparkline
{
    /// <param name="values">描く値。空なら空文字を返す。</param>
    /// <param name="width">描画幅（px）。</param>
    /// <param name="height">描画高（px）。</param>
    /// <param name="stroke">線の色。</param>
    /// <param name="fill">塗りの色。空なら塗らない。</param>
    public static string Render(
        IReadOnlyList<double> values,
        int width = 240,
        int height = 40,
        string stroke = "#7FB8D9",
        string fill = "#D6ECF7")
    {
        if (values is null || values.Count == 0)
            return string.Empty;

        double max = 1;
        foreach (double value in values)
        {
            if (value > max) max = value;
        }

        // 数 ms しか出ない相手で拡大しすぎると、ただの揺らぎが山脈に見える
        max = Math.Max(max, 10);

        var line = new StringBuilder();
        var area = new StringBuilder();

        double step = values.Count > 1 ? (double)width / (values.Count - 1) : width;

        for (int i = 0; i < values.Count; i++)
        {
            double x = Math.Round(i * step, 2);
            double y = Math.Round(height - Math.Clamp(values[i] / max, 0, 1) * height, 2);

            if (i > 0) line.Append(' ');
            line.Append(x.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(y.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(fill))
        {
            area.Append("0,").Append(height.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(line)
                .Append(' ').Append(width.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(height.ToString(CultureInfo.InvariantCulture));
        }

        var svg = new StringBuilder();
        svg.Append("<svg class=\"spark\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
           .Append("\" width=\"").Append(width).Append("\" height=\"").Append(height)
           .Append("\" preserveAspectRatio=\"none\" xmlns=\"http://www.w3.org/2000/svg\">");

        if (area.Length > 0)
            svg.Append("<polygon points=\"").Append(area).Append("\" fill=\"").Append(fill).Append("\"/>");

        svg.Append("<polyline points=\"").Append(line)
           .Append("\" fill=\"none\" stroke=\"").Append(stroke)
           .Append("\" stroke-width=\"1.4\" stroke-linejoin=\"round\"/>");

        svg.Append("</svg>");
        return svg.ToString();
    }
}
