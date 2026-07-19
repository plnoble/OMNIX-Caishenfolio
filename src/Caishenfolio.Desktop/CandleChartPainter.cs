using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop;

public enum ChartDrawMode
{
    Crosshair,
    Pan,
    TrendLine,
    HorizLine,
}

/// <summary>
/// Candles + MA + volume + crosshair + wheel zoom + drag pan + trend/horiz lines.
/// </summary>
public sealed class CandleChartPainter
{
    private readonly Canvas _canvas;
    private readonly TextBlock _crosshairLabel;
    private IReadOnlyList<MarketBarDto> _allBars = Array.Empty<MarketBarDto>();
    private int _viewStart;
    private int _viewCount;
    private double[] _slotCenters = Array.Empty<double>();
    private double _padL, _padR, _padT, _priceBottom, _volTop, _volBottom, _plotW, _priceH;
    private double _priceMin, _priceMax;
    private ChartDrawMode _mode = ChartDrawMode.Crosshair;
    private Point? _dragStart;
    private int _panAnchorStart;
    private Point? _drawAnchor;
    private readonly List<ChartLine> _lines = new();
    private bool _isDrawing;

    private sealed class ChartLine
    {
        public required string Kind { get; init; } // trend | horiz
        public double X1Norm { get; set; } // 0..1 of view width index fraction, or price for horiz
        public double Y1Price { get; set; }
        public double X2Norm { get; set; }
        public double Y2Price { get; set; }
        // store as bar-index absolute for trend
        public int I1 { get; set; }
        public int I2 { get; set; }
        public double Price { get; set; } // horiz
    }

    public CandleChartPainter(Canvas canvas, TextBlock crosshairLabel)
    {
        _canvas = canvas;
        _crosshairLabel = crosshairLabel;
        _canvas.Focusable = true;
        _canvas.MouseWheel += OnWheel;
        _canvas.MouseLeftButtonDown += OnLeftDown;
        _canvas.MouseLeftButtonUp += OnLeftUp;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseRightButtonDown += (_, e) =>
        {
            // right-drag pan shortcut
            _mode = ChartDrawMode.Pan;
            _dragStart = e.GetPosition(_canvas);
            _panAnchorStart = _viewStart;
            _canvas.CaptureMouse();
            e.Handled = true;
        };
        _canvas.MouseRightButtonUp += (_, e) =>
        {
            if (_canvas.IsMouseCaptured)
            {
                _canvas.ReleaseMouseCapture();
            }

            _dragStart = null;
            e.Handled = true;
        };
        _canvas.MouseLeave += (_, _) =>
        {
            if (!_isDrawing)
            {
                _crosshairLabel.Text = _crosshairLabel.Text.Contains("画线")
                    ? _crosshairLabel.Text
                    : "";
            }
        };
    }

    public ChartDrawMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public void SetBars(IReadOnlyList<MarketBarDto> bars)
    {
        _allBars = bars ?? Array.Empty<MarketBarDto>();
        _viewStart = 0;
        _viewCount = _allBars.Count;
        _lines.Clear();
        DrawStatic();
    }

    public void Redraw() => DrawStatic();

    public void ResetView()
    {
        _viewStart = 0;
        _viewCount = _allBars.Count;
        DrawStatic();
    }

    public void ClearDrawings()
    {
        _lines.Clear();
        _drawAnchor = null;
        _isDrawing = false;
        DrawStatic();
    }

    private IReadOnlyList<MarketBarDto> VisibleBars()
    {
        if (_allBars.Count == 0)
        {
            return _allBars;
        }

        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, _allBars.Count - 1));
        _viewCount = Math.Clamp(_viewCount, 5, _allBars.Count);
        if (_viewStart + _viewCount > _allBars.Count)
        {
            _viewStart = Math.Max(0, _allBars.Count - _viewCount);
        }

        return _allBars.Skip(_viewStart).Take(_viewCount).ToList();
    }

    private void DrawStatic(Point? cursor = null, int? hoverIdx = null, Point? tempEnd = null)
    {
        _canvas.Children.Clear();
        var bars = VisibleBars();
        if (bars.Count == 0 || _canvas.ActualWidth <= 20 || _canvas.ActualHeight <= 20)
        {
            return;
        }

        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        _padL = 48;
        _padR = 10;
        _padT = 16;
        var padB = 8;
        var volH = Math.Max(40, height * 0.22);
        var gap = 6;
        _priceBottom = height - padB - volH - gap;
        _volTop = _priceBottom + gap;
        _volBottom = height - padB;
        _plotW = Math.Max(1, width - _padL - _padR);
        _priceH = Math.Max(1, _priceBottom - _padT);

        _priceMin = bars.Min(b => (double)b.Low);
        _priceMax = bars.Max(b => (double)b.High);
        if (Math.Abs(_priceMax - _priceMin) < 1e-9)
        {
            _priceMax = _priceMin + 1;
        }

        var span = _priceMax - _priceMin;
        _priceMin -= span * 0.05;
        _priceMax += span * 0.05;
        span = _priceMax - _priceMin;

        double YPrice(double price) => _padT + (_priceMax - price) / span * _priceH;

        var maxVol = Math.Max(1, bars.Max(b => (double)b.Volume));
        double YVol(double v) => _volBottom - (v / maxVol) * (_volBottom - _volTop);

        for (var i = 0; i <= 4; i++)
        {
            var y = _padT + _priceH * i / 4.0;
            _canvas.Children.Add(HLine(_padL, _padL + _plotW, y, Color.FromRgb(0x2A, 0x35, 0x42)));
            var price = _priceMax - span * i / 4.0;
            AddText(price.ToString("0.####", CultureInfo.InvariantCulture), _padL - 46, y - 6, 10, 0x9A, 0xA7, 0xB5);
        }

        var n = bars.Count;
        var slot = _plotW / n;
        var bodyW = Math.Max(2, Math.Min(12, slot * 0.62));
        _slotCenters = new double[n];

        for (var i = 0; i < n; i++)
        {
            var bar = bars[i];
            var x = _padL + slot * i + slot / 2;
            _slotCenters[i] = x;
            var up = bar.Close >= bar.Open;
            var color = up ? Color.FromRgb(0xE0, 0x4F, 0x5F) : Color.FromRgb(0x2F, 0xB3, 0x7A);
            var brush = new SolidColorBrush(color);

            _canvas.Children.Add(new Line
            {
                X1 = x, X2 = x,
                Y1 = YPrice((double)bar.High),
                Y2 = YPrice((double)bar.Low),
                Stroke = brush,
                StrokeThickness = 1.1,
            });
            var yO = YPrice((double)bar.Open);
            var yC = YPrice((double)bar.Close);
            var top = Math.Min(yO, yC);
            var bodyH = Math.Max(1.5, Math.Abs(yC - yO));
            var rect = new Rectangle
            {
                Width = bodyW,
                Height = bodyH,
                Fill = brush,
                Stroke = brush,
            };
            Canvas.SetLeft(rect, x - bodyW / 2);
            Canvas.SetTop(rect, top);
            _canvas.Children.Add(rect);

            var vh = Math.Max(1, _volBottom - YVol((double)bar.Volume));
            var vrect = new Rectangle
            {
                Width = bodyW,
                Height = vh,
                Fill = new SolidColorBrush(Color.FromArgb(160, color.R, color.G, color.B)),
            };
            Canvas.SetLeft(vrect, x - bodyW / 2);
            Canvas.SetTop(vrect, _volBottom - vh);
            _canvas.Children.Add(vrect);
        }

        DrawMa(bars, 5, Color.FromRgb(0xF0, 0xC0, 0x40), YPrice, slot);
        DrawMa(bars, 10, Color.FromRgb(0x5B, 0x9B, 0xF5), YPrice, slot);
        DrawMa(bars, 20, Color.FromRgb(0xC0, 0x7B, 0xF0), YPrice, slot);

        // user drawings
        foreach (var line in _lines)
        {
            if (line.Kind == "horiz")
            {
                var y = YPrice(line.Price);
                _canvas.Children.Add(HLine(_padL, _padL + _plotW, y, Color.FromRgb(0xFF, 0xB0, 0x40)));
                AddText(line.Price.ToString("0.####"), _padL + _plotW - 70, y - 12, 11, 0xFF, 0xB0, 0x40);
            }
            else
            {
                // map absolute indices into visible coords
                var i1 = line.I1 - _viewStart;
                var i2 = line.I2 - _viewStart;
                if (i1 < 0 && i2 < 0 || i1 >= n && i2 >= n)
                {
                    continue;
                }

                var x1 = IndexToX(Math.Clamp(i1, 0, n - 1), slot);
                var x2 = IndexToX(Math.Clamp(i2, 0, n - 1), slot);
                // extrapolate if outside - still show clipped segment using price
                _canvas.Children.Add(new Line
                {
                    X1 = x1,
                    Y1 = YPrice(line.Y1Price),
                    X2 = x2,
                    Y2 = YPrice(line.Y2Price),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x4E, 0xC3, 0xFF)),
                    StrokeThickness = 1.6,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                });
            }
        }

        // temp drawing
        if (_isDrawing && _drawAnchor is { } a && tempEnd is { } te)
        {
            if (_mode == ChartDrawMode.HorizLine)
            {
                var y = a.Y;
                _canvas.Children.Add(HLine(_padL, _padL + _plotW, y, Color.FromRgb(0xFF, 0xD0, 0x80)));
            }
            else if (_mode == ChartDrawMode.TrendLine)
            {
                _canvas.Children.Add(new Line
                {
                    X1 = a.X,
                    Y1 = a.Y,
                    X2 = te.X,
                    Y2 = te.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x8E, 0xD6, 0xFF)),
                    StrokeThickness = 1.4,
                });
            }
        }

        // crosshair
        if (cursor is { } c && hoverIdx is { } hi && hi >= 0 && hi < bars.Count && _mode == ChartDrawMode.Crosshair)
        {
            var x = _slotCenters[hi];
            _canvas.Children.Add(VLine(x, _padT, _volBottom, Color.FromArgb(180, 0xCC, 0xD4, 0xDE)));
            _canvas.Children.Add(HLine(_padL, _padL + _plotW, c.Y, Color.FromArgb(120, 0xCC, 0xD4, 0xDE)));
            var bar = bars[hi];
            var date = bar.TimestampUtc;
            if (DateTimeOffset.TryParse(bar.TimestampUtc, out var dto))
            {
                date = dto.ToString("yyyy-MM-dd HH:mm");
            }

            _crosshairLabel.Text =
                $"{date}  开{bar.Open:0.####} 高{bar.High:0.####} 低{bar.Low:0.####} 收{bar.Close:0.####} 量{bar.Volume:N0}" +
                $"  |  可见{_viewStart + 1}-{_viewStart + bars.Count}/{_allBars.Count}";
        }

        AddText("成交量", _padL, _volTop + 2, 10, 0x9A, 0xA7, 0xB5);
        AddText(
            "滚轮缩放 · 右键拖拽平移 · 画线工具见上方",
            _padL + 60,
            2,
            10,
            0x9A,
            0xA7,
            0xB5);
    }

    private double IndexToX(int i, double slot) => _padL + slot * i + slot / 2;

    private void DrawMa(
        IReadOnlyList<MarketBarDto> bars,
        int period,
        Color color,
        Func<double, double> yPrice,
        double slot)
    {
        // MA uses full series for correctness when zoomed: compute from absolute indices
        if (_allBars.Count < period)
        {
            return;
        }

        var points = new PointCollection();
        for (var abs = _viewStart; abs < _viewStart + bars.Count; abs++)
        {
            if (abs < period - 1)
            {
                continue;
            }

            double sum = 0;
            for (var j = abs - period + 1; j <= abs; j++)
            {
                sum += (double)_allBars[j].Close;
            }

            var ma = sum / period;
            var i = abs - _viewStart;
            var x = _padL + slot * i + slot / 2;
            points.Add(new Point(x, yPrice(ma)));
        }

        if (points.Count >= 2)
        {
            _canvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.3,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_allBars.Count < 10)
        {
            return;
        }

        var pos = e.GetPosition(_canvas);
        var focusFrac = Math.Clamp((pos.X - _padL) / Math.Max(1, _plotW), 0, 1);
        var focusAbs = _viewStart + (int)(focusFrac * _viewCount);

        var factor = e.Delta > 0 ? 0.8 : 1.25;
        var newCount = (int)Math.Round(_viewCount * factor);
        newCount = Math.Clamp(newCount, 10, _allBars.Count);
        var newStart = focusAbs - (int)(focusFrac * newCount);
        _viewCount = newCount;
        _viewStart = Math.Clamp(newStart, 0, Math.Max(0, _allBars.Count - _viewCount));
        DrawStatic();
        e.Handled = true;
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _canvas.Focus();
        var pos = e.GetPosition(_canvas);
        if (_mode == ChartDrawMode.Pan)
        {
            _dragStart = pos;
            _panAnchorStart = _viewStart;
            _canvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_mode is ChartDrawMode.TrendLine or ChartDrawMode.HorizLine)
        {
            if (!_isDrawing)
            {
                _isDrawing = true;
                _drawAnchor = pos;
            }
            else if (_drawAnchor is { } a)
            {
                CommitDrawing(a, pos);
                _isDrawing = false;
                _drawAnchor = null;
                DrawStatic();
            }

            e.Handled = true;
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == ChartDrawMode.Pan && _canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
            _dragStart = null;
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        var bars = VisibleBars();
        if (bars.Count == 0)
        {
            return;
        }

        if (_mode == ChartDrawMode.Pan && _dragStart is { } ds && e.LeftButton == MouseButtonState.Pressed)
        {
            var dx = pos.X - ds.X;
            var slot = _plotW / Math.Max(1, bars.Count);
            var shift = (int)Math.Round(-dx / Math.Max(1, slot));
            _viewStart = Math.Clamp(_panAnchorStart + shift, 0, Math.Max(0, _allBars.Count - _viewCount));
            DrawStatic();
            return;
        }

        // right-button pan
        if (_dragStart is { } rds && e.RightButton == MouseButtonState.Pressed)
        {
            var dx = pos.X - rds.X;
            var slot = _plotW / Math.Max(1, bars.Count);
            var shift = (int)Math.Round(-dx / Math.Max(1, slot));
            _viewStart = Math.Clamp(_panAnchorStart + shift, 0, Math.Max(0, _allBars.Count - _viewCount));
            DrawStatic();
            return;
        }

        if (_isDrawing && _drawAnchor is not null)
        {
            DrawStatic(tempEnd: pos);
            var span = _priceMax - _priceMin;
            var price = _priceMax - (pos.Y - _padT) / Math.Max(1, _priceH) * span;
            _crosshairLabel.Text = _mode == ChartDrawMode.HorizLine
                ? $"画水平线中… 价格≈{price:0.####}（再点一下确认）"
                : $"画趋势线中… 再点一下结束";
            return;
        }

        if (_mode != ChartDrawMode.Crosshair)
        {
            return;
        }

        var idx = NearestIndex(pos.X);
        DrawStatic(pos, idx);
    }

    private void CommitDrawing(Point a, Point b)
    {
        var bars = VisibleBars();
        if (bars.Count == 0)
        {
            return;
        }

        var span = _priceMax - _priceMin;
        double PriceFromY(double y) => _priceMax - (y - _padT) / Math.Max(1, _priceH) * span;

        if (_mode == ChartDrawMode.HorizLine)
        {
            _lines.Add(new ChartLine
            {
                Kind = "horiz",
                Price = PriceFromY(a.Y),
                Y1Price = PriceFromY(a.Y),
                Y2Price = PriceFromY(a.Y),
            });
            return;
        }

        var i1 = NearestIndex(a.X);
        var i2 = NearestIndex(b.X);
        _lines.Add(new ChartLine
        {
            Kind = "trend",
            I1 = _viewStart + i1,
            I2 = _viewStart + i2,
            Y1Price = PriceFromY(a.Y),
            Y2Price = PriceFromY(b.Y),
        });
    }

    private int NearestIndex(double x)
    {
        if (_slotCenters.Length == 0)
        {
            return 0;
        }

        var idx = 0;
        var best = double.MaxValue;
        for (var i = 0; i < _slotCenters.Length; i++)
        {
            var d = Math.Abs(_slotCenters[i] - x);
            if (d < best)
            {
                best = d;
                idx = i;
            }
        }

        return idx;
    }

    private static Line HLine(double x1, double x2, double y, Color c) => new()
    {
        X1 = x1, X2 = x2, Y1 = y, Y2 = y,
        Stroke = new SolidColorBrush(c),
        StrokeThickness = 1,
    };

    private static Line VLine(double x, double y1, double y2, Color c) => new()
    {
        X1 = x, X2 = x, Y1 = y1, Y2 = y2,
        Stroke = new SolidColorBrush(c),
        StrokeThickness = 1,
    };

    private void AddText(string text, double x, double y, double size, byte r, byte g, byte b)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        _canvas.Children.Add(tb);
    }
}
