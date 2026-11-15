using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

public sealed class PendingLayer : Canvas
{
    private bool _animating;

    public void Update(FieldState? field, FieldOverlayGeometry? geometry, bool enabled,
        double width, double height, bool animate = true)
    {
        if (_animating != animate)
        {
            BeginAnimation(OpacityProperty, animate ? new DoubleAnimation(.75, 1, TimeSpan.FromSeconds(.6))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
            _animating = animate;
        }
        Children.Clear();
        if (!enabled || field is null || geometry is null) return;
        double diameter = height * .024;
        foreach (var unit in field.Units.Where(u => u.Status == "pending"))
        {
            var cell = unit.Cell;
            double x, y;
            if (geometry.Badges.TryGetValue(cell, out var badge))
            {
                x = badge.X * width;
                // 角标中心到顶边半高，再留半高间隔，最后减去圆半径。
                y = (badge.Y - badge.Size) * height - diameter / 2;
            }
            else
            {
                var anchor = geometry.Anchors[cell];
                x = anchor.X * width;
                y = anchor.Y * height - 40 * height / 1080 - diameter / 2;
            }
            // TODO(design): 光晕宽度和问号相对字号按当前参数预览验收。
            var dot = new Border
            {
                Width = diameter, Height = diameter, CornerRadius = new(diameter / 2),
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xB3, 0x22)),
                Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0xDD, 0x88), ShadowDepth = 0, BlurRadius = diameter * .45, Opacity = .7 },
                Child = new TextBlock { Text = "?", Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x1D, 0x02)),
                    FontWeight = FontWeights.Bold, FontSize = diameter * .8, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            SetLeft(dot, x - diameter / 2); SetTop(dot, y - diameter / 2);
            Children.Add(dot);
        }
    }
}
