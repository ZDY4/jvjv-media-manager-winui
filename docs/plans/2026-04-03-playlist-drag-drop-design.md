# Playlist Drag and Drop Enhancement Design

## Overview
Enhance playlist functionality with visual feedback for existing media, optimized refresh, highlight animations, and drag-drop operations between media library and playlists.

## Requirements

### 1. Visual Feedback for Existing Media
- When right-clicking multiple selected media, playlist items in submenu should be grayed out and disabled if ALL selected media are already in that playlist
- Disable checking: if every selected media ID exists in the playlist

### 2. Optimized Playlist Refresh
- Don't reload entire playlist list when adding media to existing playlist
- Only refresh playlist list when creating new playlist
- Update only the affected playlist's media count

### 3. Playlist Highlight Animation
- After adding media to playlist, briefly highlight the playlist button (1 second)
- Use visual state to show highlight effect
- Auto-revert to normal state after animation

### 4. Drag Media to Playlist
- Enable drag-drop on playlist buttons
- When media items dropped on playlist, add them to that playlist
- Trigger highlight animation on successful add
- Works from both media library and other playlists

### 5. Drag Media Out of Playlist
- When viewing a playlist, dragging media items to blank areas/other playlists removes them from current playlist
- Direct removal without confirmation dialog
- Prevents removing from "All Media" view

## Technical Design

### Files Modified

#### 1. Data/MediaDb.cs
Add method:
```csharp
public bool AreAllMediaInPlaylist(string playlistId, IEnumerable<string> mediaIds)
```
Batch check if all media IDs exist in a playlist.

#### 2. Controllers/MainPage/MediaContextMenuCoordinator.cs
Modify `RefreshPlaylistItems()`:
- Check each playlist against current selection
- Set `IsEnabled = false` and gray foreground for playlists containing all selected media

#### 3. ViewModels/MainPage/LibraryShellViewModel.cs
Modify `AddMediaToPlaylistAsync()`:
- Don't reload entire playlist list
- Only update playlist metadata (count)
- Provide callback/event for highlight animation

#### 4. Views/MainPage/PlaylistRailView.xaml
Add visual state for highlight:
```xml
<VisualState x:Name="Highlight">
    <VisualState.Setters>
        <Setter Target="RootBorder.Background" Value="{ThemeResource AccentFillColorDefaultBrush}" />
    </VisualState.Setters>
</VisualState>
```

#### 5. Views/MainPage/PlaylistRailView.xaml.cs
Add method:
```csharp
public void HighlightPlaylist(string playlistId)
{
    // Find playlist button by ID
    // Trigger visual state change
    // Start timer to revert after 1 second
}
```

#### 6. Controllers/MainPage/PlaylistRailCoordinator.cs
Add drag-drop handlers:
- `PlaylistItem_DragOver` - set accepted operation
- `PlaylistItem_Drop` - add media to playlist, trigger highlight

#### 7. Views/MainPage/LibraryPaneView.xaml
Set `AllowDrop="True"` on media area root element.

#### 8. Controllers/MainPage/MediaBrowserController.cs
Modify `LibraryPanel_DragOver`:
- Check if dragging from playlist view to blank area
- Allow drop for removal

Modify drop handler:
- If current view is playlist and drop on blank area, call `RemoveMediaFromSelectedPlaylistAsync`

### Data Flow

#### Right-Click Menu Flow
1. User right-clicks media selection
2. `MediaContextMenuCoordinator.RefreshPlaylistItems()` called
3. For each playlist: check `AreAllMediaInPlaylist(playlistId, selectedMediaIds)`
4. If all media in playlist: set `IsEnabled=false`, foreground=gray
5. Menu displays with visual feedback

#### Add to Playlist Flow
1. User clicks playlist in menu or drops media on playlist button
2. `AddMediaToPlaylistAsync()` called
3. Add media to database playlist
4. Update playlist metadata only (no list reload)
5. Call `HighlightPlaylist(playlistId)` for visual feedback
6. Highlight animates for 1 second then reverts

#### Drag to Playlist Flow
1. User starts dragging media items
2. Hover over playlist button triggers `DragOver`
3. Set `AcceptedOperation = DataPackageOperation.Copy`
4. Drop triggers handler
5. Extract media IDs from data package
6. Call `AddMediaToPlaylistAsync()`
7. Trigger highlight animation

#### Remove from Playlist Flow
1. User viewing playlist (SelectedPlaylist != null)
2. User drags media to blank area
3. `DragOver` on library panel detects source
4. Allow drop operation
5. Drop handler calls `RemoveMediaFromSelectedPlaylistAsync()`
6. Media removed from playlist, list refreshes

## Edge Cases

### Mixed Selection (Some Media in Playlist)
- Show playlist as enabled
- Context: "Add (new) media to playlist"
- Duplicates handled by database silently

### Drag to Same Playlist
- No-op (media already in playlist)
- Or allow but show no change

### Drag Between Playlists
- Remove from source playlist
- Add to target playlist
- Two separate operations

### Empty Selection
- Drag-drop operations disabled
- Right-click menu shows but playlists disabled

## Performance Considerations

### Batch Media Check
- Use single SQL query with `IN` clause and `COUNT`
- Efficient for large selections

### Minimized Refresh
- Only update affected playlist
- Avoid reloading entire playlist collection
- Use incremental update pattern

### Highlight Animation
- Use visual states (lightweight)
- Timer-based reversion (no polling)
- Clean up timer on page unload