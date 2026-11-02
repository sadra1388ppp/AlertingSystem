using System.Windows;
using System.Windows.Controls;

namespace AlertingSystem.Services;

public static class TextDirectionHelper
{
    public static void Apply(FrameworkElement element, string? value)
    {
        var direction = GetDirection(value);
        element.FlowDirection = direction;

        var alignment = direction == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;

        if (element is TextBlock textBlock)
        {
            textBlock.TextAlignment = alignment;
        }
        else if (element is TextBox textBox)
        {
            textBox.TextAlignment = alignment;
        }
    }

    private static FlowDirection GetDirection(string? value)
    {
        foreach (var character in value ?? string.Empty)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            return IsRightToLeft(character)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }

        return FlowDirection.LeftToRight;
    }

    private static bool IsRightToLeft(char character) =>
        character is >= '\u0600' and <= '\u06FF' or
            >= '\u0750' and <= '\u077F' or
            >= '\u08A0' and <= '\u08FF' or
            >= '\uFB50' and <= '\uFDFF' or
            >= '\uFE70' and <= '\uFEFF';
}
