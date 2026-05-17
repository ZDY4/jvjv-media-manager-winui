using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Views.MainPageParts;

public sealed partial class EmptyStateView : UserControl
{
    public event EventHandler? AddFolderRequested;
    public event EventHandler? AddFilesRequested;
    public event EventHandler? SettingsRequested;

    public EmptyStateView()
    {
        InitializeComponent();
    }

    private void AddFolderButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AddFolderRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddFilesButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AddFilesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }
}
