using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudupix.Controls
{
    /// <summary>The app tile: a stack of photos, the front one with hills and a sun, in white on indigo (as docs/logo.svg).</summary>
    public static class LogoTile
    {
        public static readonly Color Indigo = Color.FromRgb(0x4F, 0x46, 0xE5);

        public static Border Create(double size)
        {
            var white = Brushes.White;
            var canvas = new Canvas { Width = 64, Height = 64 };
            void Add(Shape s, double x = 0, double y = 0)
            {
                Canvas.SetLeft(s, x);
                Canvas.SetTop(s, y);
                canvas.Children.Add(s);
            }
            Add(new Rectangle { Width = 35, Height = 27, RadiusX = 4, RadiusY = 4, Stroke = white, StrokeThickness = 3.2, Opacity = 0.6 }, 19 - 1.6, 11 - 1.6);
            Add(new Rectangle { Width = 38 + 3.5, Height = 31 + 3.5, RadiusX = 4.5, RadiusY = 4.5, Fill = new SolidColorBrush(Indigo), Stroke = white, StrokeThickness = 3.5 }, 10 - 1.75, 21 - 1.75);
            Add(new Path
            {
                Data = Geometry.Parse("M 14.5,46 L 24,36 L 31,43 L 36,38 L 43.5,46"),
                Stroke = white,
                StrokeThickness = 3.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
            Add(new Ellipse { Width = 6.8, Height = 6.8, Fill = white }, 37 - 3.4, 29 - 3.4);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.234),
                Background = new SolidColorBrush(Indigo),
                Child = new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false }
            };
        }
    }
}
