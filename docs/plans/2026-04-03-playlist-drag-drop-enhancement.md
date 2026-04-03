# Playlist Drag and Drop Enhancement Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add visual feedback for existing media in playlists, optimize playlist refresh, add highlight animation, and implement drag-drop operations between media and playlists.

**Architecture:** Extend MediaDb with batch check method, modify MediaContextMenuCoordinator for visual feedback, add highlight animation to PlaylistRailView, implement drag-drop on playlist buttons and library panel.

**Tech Stack:** WinUI 3, C#, SQLite, Drag-Drop API

---

### Task 1: Add Database Method for Batch Media Check

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/Data/MediaDb.cs`

**Step 1: Add AreAllMediaInPlaylist method**

Add method in MediaDb.cs after other playlist methods (around line 440):

```csharp
public bool AreAllMediaInPlaylist(string playlistId, IEnumerable<string> mediaIds)
{
    var mediaIdList = mediaIds.ToList();
    if (mediaIdList.Count == 0)
    {
        return false;
    }

    using var connection = CreateConnection();
    connection.Open();

    var placeholders = string.Join(",", mediaIdList.Select((_, i) => $"@id{i}"));
    var sql = $@"
        SELECT COUNT(DISTINCT media_id)
        FROM playlist_media
        WHERE playlist_id = @playlistId
        AND media_id IN ({placeholders})";

    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("@playlistId", playlistId);

    for (int i = 0; i < mediaIdList.Count; i++)
    {
        command.Parameters.AddWithValue($"@id{i}", mediaIdList[i]);
    }

    var count = (long)command.ExecuteScalar();
    return count == mediaIdList.Count;
}
```

**Step 2: Verify code compiles**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/JvJvMediaManager.WinUI/Data/MediaDb.cs
git commit -m "feat: add AreAllMediaInPlaylist batch check method"
```

---

### Task 2: Show Disabled Playlists in Context Menu

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaContextMenuCoordinator.cs`

**Step 1: Modify RefreshPlaylistItems method**

Replace existing `RefreshPlaylistItems()` method (around line 114):

```csharp
private void RefreshPlaylistItems()
{
    _addToPlaylistItem.Items.Clear();

    var createNewItem = new MenuFlyoutItem { Text = "创建新的播放列表" };
    createNewItem.Click += CreateNewPlaylistItem_Click;
    _addToPlaylistItem.Items.Add(createNewItem);

    if (_viewModel.Playlists.Count > 0)
    {
        _addToPlaylistItem.Items.Add(new MenuFlyoutSeparator());
        
        var selectedMediaIds = _currentSelection.Select(m => m.Id).ToList();
        
        foreach (var playlist in _viewModel.Playlists)
        {
            var item = new MenuFlyoutItem 
            { 
                Text = playlist.Name, 
                Tag = playlist.Id 
            };
            
            var allInPlaylist = selectedMediaIds.Count > 0 && 
                _viewModel.AreAllMediaInPlaylist(playlist.Id, selectedMediaIds);
            
            if (allInPlaylist)
            {
                item.IsEnabled = false;
                item.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray);
            }
            else
            {
                item.Click += QuickPlaylistItem_Click;
            }
            
            _addToPlaylistItem.Items.Add(item);
        }
    }
}
```

**Step 2: Add AreAllMediaInPlaylist wrapper in ViewModel**

Add method in LibraryShellViewModel.cs (around line 717):

```csharp
public bool AreAllMediaInPlaylist(string playlistId, IEnumerable<string> mediaIds)
{
    return _db.AreAllMediaInPlaylist(playlistId, mediaIds);
}
```

**Step 3: Build and verify**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaContextMenuCoordinator.cs
git add src/JvJvMediaManager.WinUI/ViewModels/MainPage/LibraryShellViewModel.cs
git commit -m "feat: show disabled playlists for existing media in context menu"
```

---

### Task 3: Optimize Playlist Refresh

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/ViewModels/MainPage/LibraryShellViewModel.cs`

**Step 1: Modify AddMediaToPlaylistAsync to skip reload**

Replace method (around line 708):

```csharp
public async Task AddMediaToPlaylistAsync(string playlistId, IEnumerable<MediaItemViewModel> items)
{
    _db.AddMediaToPlaylist(playlistId, items.Select(item => item.Id));
    
    // Only reload if not currently viewing this playlist
    if (!string.Equals(SelectedPlaylist?.Id, playlistId, StringComparison.Ordinal))
    {
        // Update playlist count without full reload
        UpdatePlaylistMetadata(playlistId);
    }
    else
    {
        await RefreshMediaAsync(true);
    }
}

private void UpdatePlaylistMetadata(string playlistId)
{
    var playlist = Playlists.FirstOrDefault(p => string.Equals(p.Id, playlistId, StringComparison.Ordinal));
    if (playlist != null)
    {
        // RaisePropertyChanged to update count display
        OnPropertyChanged(nameof(Playlists));
    }
}
```

**Step 2: Verify code compiles**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/JvJvMediaManager.WinUI/ViewModels/MainPage/LibraryShellViewModel.cs
git commit -m "feat: optimize playlist refresh on media add"
```

---

### Task 4: Add Playlist Highlight Animation

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/Views/MainPage/PlaylistRailView.xaml`
- Modify: `src/JvJvMediaManager.WinUI/Views/MainPage/PlaylistRailView.xaml.cs`

**Step 1: Add highlight visual state to playlist item template**

Modify `PlaylistItemTemplate` in PlaylistRailView.xaml (around line 30), add VisualStateManager:

```xml
<DataTemplate x:Key="PlaylistItemTemplate">
    <Border x:Name="ItemBorder"
            Background="{Binding Color, Converter={StaticResource PlaylistBackgroundBrushConverter}}"
            BorderThickness="0"
            CornerRadius="{ThemeResource RadiusSmall}"
            Padding="8,6"
            AllowDrop="True"
            DragOver="PlaylistItem_DragOver"
            Drop="PlaylistItem_Drop">
        <VisualStateManager.VisualStateGroups>
            <VisualStateGroup x:Name="HighlightStates">
                <VisualState x:Name="Normal"/>
                <VisualState x:Name="Highlight">
                    <VisualState.Setters>
                        <Setter Target="ItemBorder.Background" Value="{ThemeResource AccentFillColorDefaultBrush}"/>
                        <Setter Target="ItemBorder.BorderThickness" Value="2"/>
                        <Setter Target="ItemBorder.BorderBrush" Value="{ThemeResource AccentTextFillColorPrimaryBrush}"/>
                    </VisualState.Setters>
                </VisualState>
            </VisualStateGroup>
        </VisualStateManager.VisualStateGroups>
        
        <TextBlock Text="{Binding Name}"
                   Foreground="{Binding Color, Converter={StaticResource PlaylistForegroundBrushConverter}}"
                   FontSize="13"
                   TextTrimming="CharacterEllipsis"/>
    </Border>
</DataTemplate>
```

**Step 2: Add HighlightPlaylist method in code-behind**

Add in PlaylistRailView.xaml.cs (around line 50):

```csharp
private readonly Dictionary<string, DispatcherQueueTimer> _highlightTimers = new();

public void HighlightPlaylist(string playlistId)
{
    RunOnUiThread(() =>
    {
        for (int i = 0; i < PlaylistRailListView.Items.Count; i++)
        {
            if (PlaylistRailListView.Items[i] is Playlist playlist && 
                string.Equals(playlist.Id, playlistId, StringComparison.Ordinal))
            {
                if (PlaylistRailListView.ContainerFromIndex(i) is ListViewItem container)
                {
                    var border = FindChild<Border>(container, "ItemBorder");
                    if (border != null)
                    {
                        VisualStateManager.GoToState(border, "Highlight", true);
                        
                        // Cancel previous timer
                        if (_highlightTimers.TryGetValue(playlistId, out var oldTimer))
                        {
                            oldTimer.Stop();
                        }
                        
                        // Create new timer
                        var timer = new DispatcherQueueTimer { Interval = TimeSpan.FromSeconds(1) };
                        timer.Tick += (_, _) =>
                        {
                            VisualStateManager.GoToState(border, "Normal", true);
                            timer.Stop();
                            _highlightTimers.Remove(playlistId);
                        };
                        timer.Start();
                        _highlightTimers[playlistId] = timer;
                    }
                }
                break;
            }
        }
    });
}

private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
{
    if (parent == null) return null;
    
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        
        if (child is T element && element.Name == name)
        {
            return element;
        }
        
        var result = FindChild<T>(child, name);
        if (result != null)
        {
            return result;
        }
    }
    
    return null;
}
```

**Step 3: Add necessary usings**

Add at top of PlaylistRailView.xaml.cs:

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
```

**Step 4: Build**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/JvJvMediaManager.WinUI/Views/MainPage/PlaylistRailView.xaml
git add src/JvJvMediaManager.WinUI/Views/MainPage/PlaylistRailView.xaml.cs
git commit -m "feat: add playlist highlight animation"
```

---

### Task 5: Update Coordinators to Trigger Highlight

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/Controllers/MainPage/PlaylistRailCoordinator.cs`
- Modify: `src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaContextMenuCoordinator.cs`

**Step 1: Add highlight trigger in PlaylistRailCoordinator**

Add field and method in PlaylistRailCoordinator.cs:

```csharp
private PlaylistRailView _playlistRailView;

// Add after constructor
public void HighlightPlaylist(string playlistId)
{
    _playlistRailView.HighlightPlaylist(playlistId);
}
```

**Step 2: Add highlight call after playlist add**

In MediaContextMenuCoordinator.cs, modify `QuickPlaylistItem_Click` (around line 170):

```csharp
private async void QuickPlaylistItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is not MenuFlyoutItem item || item.Tag is not string playlistId || _currentSelection.Count == 0)
    {
        return;
    }

    await _viewModel.AddMediaToPlaylistAsync(playlistId, _currentSelection);
    
    // Trigger highlight (need to pass reference to coordinator)
    OnPlaylistModified?.Invoke(playlistId);
}
```

Add event:

```csharp
public event Action<string>? OnPlaylistModified;
```

**Step 3: Wire up in MainPageShellController**

In MainPageShellController.cs constructor (around line 110), add:

```csharp
_mediaContextMenuCoordinator.OnPlaylistModified += (playlistId) =>
{
    _playlistRailCoordinator.HighlightPlaylist(playlistId);
};
```

**Step 4: Build**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/PlaylistRailCoordinator.cs
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaContextMenuCoordinator.cs
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/MainPageShellController.cs
git commit -m "feat: wire up playlist highlight on add"
```

---

### Task 6: Implement Drag to Playlist

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/Views/MainPage/PlaylistRailView.xaml.cs`
- Modify: `src/JvJvMediaManager.WinUI/Controllers/MainPage/PlaylistRailCoordinator.cs`

**Step 1: Add drag-drop handlers in PlaylistRailView.xaml.cs**

Add event handlers:

```csharp
private void PlaylistItem_DragOver(object sender, DragEventArgs e)
{
    if (e.DataView.Properties.TryGetValue("MediaIds", out var mediaIds))
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }
}

private async void PlaylistItem_Drop(object sender, DragEventArgs e)
{
    if (sender is Border border && border.DataContext is Playlist playlist &&
        e.DataView.Properties.TryGetValue("MediaIds", out var mediaIdsObj) && 
        mediaIdsObj is List<string> mediaIds)
    {
        await PlaylistDropRequested?.Invoke(playlist.Id, mediaIds);
        e.Handled = true;
    }
}

public event Func<string, List<string>, Task>? PlaylistDropRequested;
```

**Step 2: Handle in PlaylistRailCoordinator**

Add in coordinator:

```csharp
_playlistRailView.PlaylistDropRequested += async (playlistId, mediaIds) =>
{
    await _viewModel.AddMediaToPlaylistAsync(playlistId, 
        _viewModel.FilteredMediaItems.Where(m => mediaIds.Contains(m.Id)));
    _playlistRailView.HighlightPlaylist(playlistId);
};
```

**Step 3: Set up drag data in MediaBrowserController**

Modify drag start code (around line 390), add:

```csharp
private void StartMediaDrag(object sender, PointerRoutedEventArgs e)
{
    // ... existing drag setup code ...
    
    var mediaIds = _viewModel.SelectedMedia.Select(m => m.Id).ToList();
    dataPackage.Properties.Add("MediaIds", mediaIds);
    
    // ... rest of drag setup ...
}
```

**Step 4: Build**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/JvJvMediaManager.WinUI/Views/MainPage/PlaylistRailView.xaml.cs
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/PlaylistRailCoordinator.cs
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaBrowserController.cs
git commit -m "feat: implement drag media to playlist"
```

---

### Task 7: Implement Drag Out of Playlist

**Files:**
- Modify: `src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaBrowserController.cs`

**Step 1: Modify LibraryPanel_DragOver**

Modify method (around line 586):

```csharp
private void LibraryPanel_DragOver(object sender, DragEventArgs e)
{
    if (e.DataView.Properties.TryGetValue("MediaIds", out var mediaIds))
    {
        // If current view is a playlist, allow move (remove from playlist)
        if (_viewModel.SelectedPlaylist != null)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
        e.Handled = true;
    }
}
```

**Step 2: Add Drop handler for removal**

Add new method:

```csharp
private async void LibraryPanel_Drop(object sender, DragEventArgs e)
{
    if (_viewModel.SelectedPlaylist == null)
    {
        return;
    }

    if (e.DataView.Properties.TryGetValue("MediaIds", out var mediaIdsObj) &&
        mediaIdsObj is List<string> mediaIds)
    {
        var items = _viewModel.FilteredMediaItems
            .Where(m => mediaIds.Contains(m.Id))
            .ToList();
        
        await _viewModel.RemoveMediaFromSelectedPlaylistAsync(items);
        e.Handled = true;
    }
}
```

**Step 3: Wire up Drop event**

In InitializeAsync, add:

```csharp
_libraryPane.DropTargetBorder.Drop += LibraryPanel_Drop;
```

In Unload, add:

```csharp
_libraryPane.DropTargetBorder.Drop -= LibraryPanel_Drop;
```

**Step 4: Build**

Run: `dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/JvJvMediaManager.WinUI/Controllers/MainPage/MediaBrowserController.cs
git commit -m "feat: implement drag out of playlist to remove"
```

---

### Task 8: Integration Testing

**Step 1: Test right-click menu**
Manual test:
1. Select multiple media items
2. Right-click → Join playlist
3. Verify playlists containing all selected media are grayed out
4. Verify clicking grayed item does nothing
5. Verify clicking other playlist adds media

**Step 2: Test playlist highlight**
Manual test:
1. Add media to playlist via menu
2. Verify playlist button highlights for 1 second
3. Add via drag-drop
4. Verify highlight works

**Step 3: Test drag to playlist**
Manual test:
1. Select media in library
2. Drag to playlist button
3. Verify media added to playlist
4. Verify highlight triggers

**Step 4: Test drag out of playlist**
Manual test:
1. View a playlist
2. Select media
3. Drag to empty library area
4. Verify media removed from playlist

**Step 5: Final commit**

```bash
git add -A
git commit -m "feat: complete playlist drag-drop enhancement"
```

---

## Notes

### Performance
- Batch media check uses single SQL query
- Highlight animation uses DispatcherQueueTimer (lightweight)
- Playlist refresh optimized to avoid full reload

### UX Considerations
- Gray color indicates "already added" (not an error)
- 1-second highlight is brief but noticeable
- Direct removal (no confirmation) for faster workflow

### Edge Cases Handled
- Empty selection: all playlists enabled
- Mixed selection: playlists enabled (will add duplicates silently)
- Drag to same playlist: no-op
- Drag between playlists: remove + add (two operations)