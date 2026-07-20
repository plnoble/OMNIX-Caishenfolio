using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Caishenfolio.Desktop;

public static class EquityChartPainter
{
    public static void Draw(
        Canvas canvas,
        IReadOnlyList<(string Time, double Equity, double Close)> points)
    {
        canvas.Children.Clear();
        if (points.Count < 2 || canvas.ActualWidth < 40 || canvas.ActualHeight < 40)
        {
            return;
        }

        var padL = 52.0;
        var padR = 12.0;
        var padT = 18.0;
        var padB = 28.0;
        var w = Math.Max(1, canvas.ActualWidth - padL - padR);
        var h = Math.Max(1, canvas.ActualHeight - padT - padB);

        var minE = points.Min(p => p.Equity);
        var maxE = points.Max(p => p.Equity);
        if (Math.Abs(maxE - minE) < 1e-9)
        {
            maxE = minE + 0.01;
        }

        var span = maxE - minE;
        minE -= span * 0.08;
        maxE += span * 0.08;
        span = maxE - minE;

        double X(int i) => padL + w * i / (points.Count - 1.0);
        double Y(double v) => padT + (maxE - v) / span * h;

        for (var g = 0; g <= 4; g++)
        {
            var y = padT + h * g / 4.0;
            canvas.Children.Add(new Line
            {
                X1 = padL, X2 = padL + w, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x42)),
                StrokeThickness = 1,
            });
            var val = maxE - span * g / 4.0;
            Add(canvas, val.ToString("0.000", CultureInfo.InvariantCulture), 2, y - 6, 10);
        }

        // baseline 1.0
        if (minE < 1 && maxE > 1)
        {
            var y1 = Y(1);
            canvas.Children.Add(new Line
            {
                X1 = padL, X2 = padL + w, Y1 = y1, Y2 = y1,
                Stroke = new SolidColorBrush(Color.FromRgb(0x66, 0x77, 0x88)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
        }

        var equityPts = new PointCollection();
        var pricePts = new PointCollection();
        var minC = points.Min(p => p.Close);
        var maxC = points.Max(p => p.Close);
        if (Math.Abs(maxC - minC) < 1e-9)
        {
            maxC = minC + 1;
        }

        var spanC = maxC - minC;
        minC -= spanC * 0.05;
        maxC += spanC * 0.05;
        spanC = maxC - minC;
        double Yc(double c) => padT + (maxC - c) / spanC * h;

        for (var i = 0; i < points.Count; i++)
        {
            equityPts.Add(new Point(X(i), Y(points[i].Equity)));
            pricePts.Add(new Point(X(i), Yc(points[i].Close)));
        }

        canvas.Children.Add(new Polyline
        {
            Points = pricePts,
            Stroke = new SolidColorBrush(Color.FromArgb(100, 0x9A, 0xA7, 0xB5)),
            StrokeThickness = 1.2,
        });
        canvas.Children.Add(new Polyline
        {
            Points = equityPts,
            Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xF5)),
            StrokeThickness = 2.2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        Add(canvas, points[0].Time.Length >= 10 ? points[0].Time[..10] : points[0].Time, padL, padT + h + 6, 10);
        var lastT = points[^1].Time;
        Add(canvas, lastT.Length >= 10 ? lastT[..10] : lastT, padL + w - 72, padT + h + 6, 10);
        Add(canvas, "蓝线=策略权益(起点1.0)  灰线=收盘价(独立刻度)", padL + 8, 2, 11);
    }

    private static void Add(Canvas canvas, string text, double x, double y, double size)
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
