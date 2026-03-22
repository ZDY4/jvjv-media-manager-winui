using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Views.Controls;

public sealed partial class TagEditorPanel : UserControl
{
    public TagEditorPanel(bool isSingle, int itemCount, string initialTags)
    {
        InitializeComponent();

        DescriptionText.Text = isSingle
            ? "编辑当前媒体的标签。"
            : $"将对选中的 {itemCount} 个媒体统一编辑标签。";

        TagTextBox.Text = initialTags;

        if (!isSingle)
        {
            ModeComboBox.Visibility = Visibility.Visible;
            ModeComboBox.ItemsSource = new[]
            {
                new ComboBoxItem { Content = "覆盖标签", Tag = TagUpdateMode.Replace },
                new ComboBoxItem { Content = "追加标签", Tag = TagUpdateMode.Append }
            };
            ModeComboBox.SelectedIndex = 1;
        }
    }
}
