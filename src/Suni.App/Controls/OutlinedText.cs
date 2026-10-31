using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Suni.App.Controls;

/// <summary>
/// Chữ có viền (SDD 8.2): đọc được trên mọi nền video/game. Vẽ viền trước, chữ đè lên;
/// tự xuống dòng theo bề rộng có sẵn, căn giữa.
/// </summary>
public sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedText),
        new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private FormattedText? _formatted;

    protected override Size MeasureOverride(Size availableSize)
    {
        _formatted = Format(double.IsInfinity(availableSize.Width) ? 2000 : availableSize.Width);
        if (_formatted is null)
            return new Size(0, 0);
        // Chữ căn giữa trong toàn bộ bề rộng được cấp → chiếm hết bề rộng đó.
        var pad = StrokeThickness;
        var width = double.IsInfinity(availableSize.Width) ? _formatted.Width + pad * 2 : availableSize.Width;
        return new Size(width, _formatted.Height + pad * 2);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _formatted = Format(finalSize.Width);
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_formatted is null)
            return;
        var pad = StrokeThickness;
        var geometry = _formatted.BuildGeometry(new Point(pad, pad));
        var pen = new Pen(Stroke, StrokeThickness * 2) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawGeometry(Fill, null, geometry);
    }

    private FormattedText? Format(double width)
    {
        if (string.IsNullOrEmpty(Text))
            return null;
        var ft = new FormattedText(
            Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElementFontFamily(), FontStyles.Normal, FontWeight, FontStretches.Normal),
            FontSize, Fill, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(1, width - StrokeThickness * 2),
            Trimming = TextTrimming.None,
        };
        return ft;
    }

    private FontFamily TextElementFontFamily() =>
        (FontFamily?)TryFindResource("UiFont") ?? new FontFamily("Segoe UI");
}
