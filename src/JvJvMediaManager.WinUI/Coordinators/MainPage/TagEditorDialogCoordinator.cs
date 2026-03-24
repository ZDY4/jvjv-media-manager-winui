using JvJvMediaManager.Models;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels;
using JvJvMediaManager.Views.Controls;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Coordinators.MainPage;

public sealed class TagEditorDialogCoordinator
{
    private readonly IContentDialogService _dialogService;

    public TagEditorDialogCoordinator(IContentDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task<TagEditorResult?> ShowAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        var isSingle = items.Count == 1;
        var panel = new TagEditorPanel(
            isSingle,
            items.Count,
            isSingle ? string.Join(", ", items[0].Tags) : string.Empty);

        var dialog = new ContentDialog
        {
            Title = isSingle ? "编辑标签" : "批量编辑标签",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await _dialogService.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        var mode = isSingle
            ? TagUpdateMode.Replace
            : (TagUpdateMode?)((panel.ModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? TagUpdateMode.Append) ?? TagUpdateMode.Append;
        return new TagEditorResult
        {
            Tags = ParseTags(panel.TagTextBox.Text),
            Mode = mode
        };
    }

    private static IReadOnlyList<string> ParseTags(string value)
    {
        return value
            .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
