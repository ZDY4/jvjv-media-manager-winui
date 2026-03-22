using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.Models;

namespace JvJvMediaManager.Views.Controls;

public sealed partial class LockManagerPanel : UserControl
{
    public ObservableCollection<WatchedFolder> ProtectedFolders { get; }

    public LockManagerPanel(IEnumerable<WatchedFolder> protectedFolders)
    {
        InitializeComponent();

        ProtectedFolders = new ObservableCollection<WatchedFolder>(protectedFolders);
        FoldersListView.ItemsSource = ProtectedFolders;

        if (ProtectedFolders.Count > 0)
        {
            FoldersListView.SelectedIndex = 0;
        }
    }

    public WatchedFolder? SelectedFolder => FoldersListView.SelectedItem as WatchedFolder;
}
