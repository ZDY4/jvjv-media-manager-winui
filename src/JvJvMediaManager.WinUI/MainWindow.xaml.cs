using Microsoft.UI.Xaml;
using JvJvMediaManager.Views;

namespace JvJvMediaManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootFrame.Content = new MainPage();
    }
}
