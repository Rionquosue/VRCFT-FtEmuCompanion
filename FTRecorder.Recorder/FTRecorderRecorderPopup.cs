using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>A modal error popup: a title, a message and OK.</summary>
    internal sealed class FTRecorderRecorderPopup : Window
    {
        private FTRecorderRecorderPopup(string title, string message)
        {
            Title = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var ok = new Button { Content = "OK", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => Close();
            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 });
            panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(ok);
            Content = panel;
        }

        /// <summary>Shows the popup over owner until OK (or Escape, or closing it).</summary>
        internal static Task Show(Window owner, string title, string message) => new FTRecorderRecorderPopup(title, message).ShowDialog(owner);
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Recorder popup
// ============================================================================
//
// FTRecorderRecorderPopup (this file)  A modal error window over the Recorder: the title in bold, the message
//                            (selectable, so a path or link can be copied) and OK (Enter or Escape too).
//   Show(owner, title, message)  Shows it; completes when it is closed.
