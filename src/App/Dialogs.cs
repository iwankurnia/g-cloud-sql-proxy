using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CloudSqlProxyManager
{
    /// <summary>A plain message window: Avalonia has no built-in message box.</summary>
    public static class Dialogs
    {
        public static Task Warn(Window owner, string message)
        {
            return Show(owner, message, new string[] { "OK" });
        }

        /// <summary>Returns true when the first (primary) button was chosen.</summary>
        public static async Task<bool> Ask(Window owner, string message, string yes, string no)
        {
            return await Show(owner, message, new string[] { yes, no }) == 0;
        }

        private static Task<int> Show(Window owner, string message, string[] buttons)
        {
            TaskCompletionSource<int> result = new TaskCompletionSource<int>();
            Window dialog = new Window();
            dialog.Title = "Cloud SQL Proxy Manager";
            dialog.Width = 440;
            dialog.SizeToContent = SizeToContent.Height;
            dialog.CanResize = false;
            dialog.ShowInTaskbar = false;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Icon = owner.Icon;
            dialog.Background = Brushes.White;

            TextBlock text = new TextBlock();
            text.Text = message;
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(24, 22, 24, 18);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Spacing = 8;
            row.Margin = new Thickness(24, 0, 24, 20);
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                Button button = new Button();
                button.Content = buttons[i];
                if (i == 0) { button.Classes.Add("primary"); button.IsDefault = true; }
                else button.IsCancel = true;
                button.Click += delegate { result.TrySetResult(index); dialog.Close(); };
                row.Children.Add(button);
            }

            StackPanel panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(row);
            dialog.Content = panel;
            dialog.Closed += delegate { result.TrySetResult(buttons.Length - 1); };
            dialog.ShowDialog(owner);
            return result.Task;
        }
    }
}
