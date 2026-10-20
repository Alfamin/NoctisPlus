using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Views;

public partial class WhatsNewPanel : UserControl
{
    public WhatsNewPanel() => InitializeComponent();

    /// <summary>The features rise in one after another (subtle, ~0.3 s each, 55 ms apart). Whatever happens, every row
    /// is shown after 1.5 s: an animation never hides content.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            var rows = this.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("feature-row")).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                DispatcherTimer.RunOnce(() => row.Classes.Add("shown"), TimeSpan.FromMilliseconds(90 + i * 55));
            }
        }, DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(() =>
        {
            foreach (var row in this.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("feature-row")))
                if (!row.Classes.Contains("shown")) row.Classes.Add("shown");
        }, TimeSpan.FromMilliseconds(1500));
    }
}
