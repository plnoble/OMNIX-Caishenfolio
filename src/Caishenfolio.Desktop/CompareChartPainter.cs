using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Caishenfolio.Desktop;

/// <summary>
/// Multi-series normalized overlay chart (起点=100).
/// </summary>
public static class CompareChartPainter
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x5B, 0x9B, 0xF5),
        Color.FromRgb(0xE0, 0x4F, 0x5F),
        Color.FromRgb(0x2F, 0xB3, 0x7A),
        Color.FromRgb(0xF0, 0xC0, 0x40),
        Color.FromRgb(0xC0, 0x7B, 0xF0),
        Color.FromRgb(0x4E, 0xC3, 0xFF),
    ];

    public static void Draw(
        Canvas canvas,
        IReadOnlyList<string> dates,
        IReadOnlyDictionary<string, IReadOnlyList<double>> series,
        TextBlock? legend = null)
    {
        canvas.Children.Clear();
        if (legend is not null)
        {
            legend.Text = "";
        }

        if (dates.Count < 2 || series.Count == 0 || canvas.ActualWidth < 40 || canvas.ActualHeight < 40)
        {
            return;
        }

        var padL = 48.0;
        var padR = 12.0;
        var padT = 20.0;
        var padB = 28.0;
        var w = Math.Max(1, canvas.ActualWidth - padL - padR);
        var h = Math.Max(1, canvas.ActualHeight - padT - padB);

        var allVals = series.Values.SelectMany(v => v).ToList();
        var min = allVals.Min();
        var max = allVals.Max();
        if (Math.Abs(max - min) < 1e-9)
        {
            max = min + 1;
        }

        var span = max - min;
        min -= span * 0.05;
        max += span * 0.05;
        span = max - min;

        double X(int i) => padL + (dates.Count <= 1 ? w / 2 : w * i / (dates.Count - 1.0));
        double Y(double v) => padT + (max - v) / span * h;

        for (var g = 0; g <= 4; g++)
        {
            var y = padT + h * g / 4.0;
            canvas.Children.Add(new Line
            {
                X1 = padL,
                X2 = padL + w,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x42)),
                StrokeThickness = 1,
            });
            var val = max - span * g / 4.0;
            AddText(canvas, val.ToString("0.0", CultureInfo.InvariantCulture), 4, y - 6, 10);
        }

        if (min < 100 && max > 100)
        {
            var y100 = Y(100);
            canvas.Children.Add(new Line
            {
                X1 = padL,
                X2 = padL + w,
                Y1 = y100,
                Y2 = y100,
                Stroke = new SolidColorBrush(Color.FromRgb(0x66, 0x77, 0x88)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
        }

        var si = 0;
        var legendParts = new List<string>();
        foreach (var kv in series)
        {
            var color = Palette[si % Palette.Length];
            si++;
            var pts = new PointCollection();
            var values = kv.Value;
            var n = Math.Min(dates.Count, values.Count);
            for (var i = 0; i < n; i++)
            {
                pts.Add(new Point(X(i), Y(values[i])));
            }

            if (pts.Count >= 2)
            {
                canvas.Children.Add(new Polyline
                {
                    Points = pts,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 2.2,
                    StrokeLineJoin = PenLineJoin.Round,
                });
            }

            var end = values.Count > 0 ? values[^1] : 100;
            legendParts.Add($"{MarketLabels.FromSymbol(kv.Key)} {kv.Key} → {end:0.0}");
        }

        AddText(canvas, dates[0], padL, padT + h + 6, 10);
        AddText(canvas, dates[^1], padL + w - 72, padT + h + 6, 10);
        AddText(canvas, "归一化收盘（起点=100）· 多股叠线对比", padL + 8, 2, 11);

        if (legend is not null)
        {
            legend.Text = string.Join("   ", legendParts) + "   · 研究/模拟，非投资建议";
        }
    }

    private static void AddText(Canvas canvas, string text, double x, double y, double size)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB5)),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        canvas.Children.Add(tb);
    }
}
