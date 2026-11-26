using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Holm.Platform;
using ZXing;
using ZXing.Common;

namespace Holm.App;

/// <summary>
/// Snipping-tool style fullscreen overlay that lets the user drag a rectangle over a QR code on the screen
/// and decodes it with ZXing.Net. Works identically on Windows and Linux via <see cref="ScreenImage"/>.
/// </summary>
sealed class QrSnipWindow : Window
{
    readonly ScreenImage _shot;
    readonly Canvas _overlay = new();
    readonly Rectangle _dimTop = DimRect(), _dimBottom = DimRect(), _dimLeft = DimRect(), _dimRight = DimRect();
    readonly Border _selectionBox;
    readonly Border _hint;
    Point _start;
    bool _dragging, _done, _activated;

    public event Action<string?>? Done;

    public QrSnipWindow(ScreenImage shot, PixelRect bounds, double scaling)
    {
        _shot = shot;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = bounds.Position;
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
        WindowState = WindowState.FullScreen;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        FontFamily = Fonts.Text;
        Fonts.ApplyRendering(this);

        var full = new WriteableBitmap(new PixelSize(shot.Width, shot.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = full.Lock())
        {
            int rowBytes = shot.Width * 4;
            for (int y = 0; y < shot.Height; y++)
                Marshal.Copy(shot.Bgra, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        }

        var screen = new Image
        {
            Source = full, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(screen, BitmapInterpolationMode.None);

        _selectionBox = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Drag a rectangle around the QR code · Esc or right-click cancels",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeight.Medium,
            },
        };

        _overlay.Children.Add(_dimTop);
        _overlay.Children.Add(_dimBottom);
        _overlay.Children.Add(_dimLeft);
        _overlay.Children.Add(_dimRight);
        _overlay.Children.Add(_selectionBox);

        var layers = new Grid();
        layers.Children.Add(screen);
        layers.Children.Add(_overlay);
        layers.Children.Add(_hint);
        Content = layers;

        SizeChanged += (_, _) => UpdateDim(default, showSelection: false);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(null); } };
        Activated += (_, _) => _activated = true;
        Deactivated += (_, _) => { if (_activated) Finish(null); };
        Opened += (_, _) => { Activate(); Focus(); UpdateDim(default, showSelection: false); };
    }

    static Rectangle DimRect() => new() { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0x00, 0x00, 0x00)), IsHitTestVisible = false };

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed) { Finish(null); return; }
        if (!pt.Properties.IsLeftButtonPressed) return;
        _start = pt.Position;
        _dragging = true;
        UpdateDim(new Rect(_start, _start), showSelection: true);
    }

    void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        UpdateDim(MakeRect(_start, e.GetPosition(this)), showSelection: true);
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var rect = MakeRect(_start, e.GetPosition(this));
        if (rect.Width < 12 || rect.Height < 12)
        {
            UpdateDim(default, showSelection: false);
            return;
        }
        Finish(DecodeRegion(rect));
    }

    static Rect MakeRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    void UpdateDim(Rect sel, bool showSelection)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        if (!showSelection)
        {
            SetRect(_dimTop, 0, 0, w, h);
            SetRect(_dimBottom, 0, 0, 0, 0);
            SetRect(_dimLeft, 0, 0, 0, 0);
            SetRect(_dimRight, 0, 0, 0, 0);
            _selectionBox.IsVisible = false;
            return;
        }
        double x1 = Math.Clamp(sel.X, 0, w);
        double y1 = Math.Clamp(sel.Y, 0, h);
        double x2 = Math.Clamp(sel.Right, 0, w);
        double y2 = Math.Clamp(sel.Bottom, 0, h);
        SetRect(_dimTop, 0, 0, w, y1);
        SetRect(_dimBottom, 0, y2, w, Math.Max(0, h - y2));
        SetRect(_dimLeft, 0, y1, x1, Math.Max(0, y2 - y1));
        SetRect(_dimRight, x2, y1, Math.Max(0, w - x2), Math.Max(0, y2 - y1));
        Canvas.SetLeft(_selectionBox, x1);
        Canvas.SetTop(_selectionBox, y1);
        _selectionBox.Width = Math.Max(2, x2 - x1);
        _selectionBox.Height = Math.Max(2, y2 - y1);
        _selectionBox.IsVisible = true;
    }

    static void SetRect(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }

    string? DecodeRegion(Rect dipRect)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        double sx = _shot.Width / Bounds.Width;
        double sy = _shot.Height / Bounds.Height;
        int x = Math.Clamp((int)Math.Floor(dipRect.X * sx), 0, _shot.Width - 1);
        int y = Math.Clamp((int)Math.Floor(dipRect.Y * sy), 0, _shot.Height - 1);
        int w = Math.Clamp((int)Math.Ceiling(dipRect.Width * sx), 1, _shot.Width - x);
        int h = Math.Clamp((int)Math.Ceiling(dipRect.Height * sy), 1, _shot.Height - y);

        var crop = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(_shot.Bgra, ((y + row) * _shot.Width + x) * 4, crop, row * w * 4, w * 4);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
            },
        };
        var source = new RGBLuminanceSource(crop, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
        var result = reader.Decode(source);
        return result?.Text;
    }

    void Finish(string? text)
    {
        if (_done) return;
        _done = true;
        Close();
        Done?.Invoke(text);
    }
}
