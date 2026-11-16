using System.Windows;
using System.Windows.Controls;

namespace AlertingSystem;

internal static class TextDirectionHelper
{
    public static void Apply(FrameworkElement element, string? value)
    {
        var direction = FlowDirection.LeftToRight;

        foreach (var character in value ?? string.Empty)
        {
            if (!char.IsLetter(character))
                continue;

            direction = character is >= '\u0600' and <= '\u06FF' or
                >= '\u0750' and <= '\u077F' or
                >= '\u08A0' and <= '\u08FF' or
                >= '\uFB50' and <= '\uFDFF' or
                >= '\uFE70' and <= '\uFEFF'
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            break;
        }

        element.FlowDirection = direction;
        var alignment = direction == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;

        if (element is TextBlock textBlock)
            textBlock.TextAlignment = alignment;
        else if (element is TextBox textBox)
            textBox.TextAlignment = alignment;
    }
}
