# 播放列表拖拽功能 - 未完成任务

## 概述

播放列表拖拽功能共 8 个任务，已完成 Task 1-3 和部分 Task 4，剩余 Task 4-7 需要继续实现。

## 已完成任务

### Task 1: 数据库批量检查方法
**提交**: `6ca336b`

- 文件: `Data/MediaDb.cs`
- 新增: `AreAllMediaInPlaylist(playlistId, mediaIds)` 方法
- 功能: 检查所有指定媒体是否都在某个播放列表中
- 用途: 右键菜单显示灰色的已添加播放列表

### Task 2: 右键菜单显示灰色播放列表
**提交**: `70a2823`

- 文件: `Controllers/MainPage/MediaContextMenuCoordinator.cs`
- 功能: 如果选中媒体全部已在播放列表中，该播放列表项显示为灰色且不可点击
- 实现: 在 `RefreshPlaylistItems()` 中检查并设置 `IsEnabled=false`

### Task 3: 播放列表刷新优化
**提交**: `70a2823`

- 文件: `ViewModels/MainPage/LibraryShellViewModel.cs`
- 功能: 添加媒体到播放列表时不重新加载整个列表
- 实现: 只调用 `UpdatePlaylistMetadata()` 更新元数据

### Task 4: 高亮动画基础架构（部分完成）
**提交**: `9591fe2`

- 文件: `Views/MainPage/PlaylistRailView.xaml.cs`
- 文件: `Views/MainPage/MediaLibraryResources.xaml`
- 功能: 添加高亮动画视觉状态和控制方法
- 状态: `HighlightPlaylist()` 方法已实现，但未被调用

---

## 未完成任务

### Task 4 (剩余部分): 触发高亮动画

**目标**: 添加媒体到播放列表后，触发 1 秒高亮动画

**需要修改的文件**:
- `Controllers/MainPage/MediaContextMenuCoordinator.cs`
- `Controllers/MainPage/MainPageShellController.cs`

**实现步骤**:

#### 1. MediaContextMenuCoordinator 添加事件

```csharp
// MediaContextMenuCoordinator.cs

public event Action<string>? OnPlaylistModified;

private async void QuickPlaylistItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is not MenuFlyoutItem item || item.Tag is not string playlistId || _currentSelection.Count == 0)
    {
        return;
    }

    await _viewModel.AddMediaToPlaylistAsync(playlistId, _currentSelection);
    
    // 触发高亮
    OnPlaylistModified?.Invoke(playlistId);
}
```

#### 2. MainPageShellController 连接事件

```csharp
// MainPageShellController.cs (在构造函数中，约 line 110)

_mediaContextMenuCoordinator.OnPlaylistModified += (playlistId) =>
{
    _playlistRailCoordinator.HighlightPlaylist(playlistId);
};
```

#### 3. PlaylistRailCoordinator 添加高亮方法

```csharp
// PlaylistRailCoordinator.cs

public void HighlightPlaylist(string playlistId)
{
    _playlistRailView.HighlightPlaylist(playlistId);
}
```

---

### Task 5: 实现拖拽媒体到播放列表

**目标**: 拖拽媒体项到左侧播放列表按钮，添加到该播放列表

**需要修改的文件**:
- `Controllers/MainPage/MediaBrowserController.cs`
- `Controllers/MainPage/PlaylistRailCoordinator.cs`
- `Views/MainPage/PlaylistRailView.xaml`
- `Views/MainPage/PlaylistRailView.xaml.cs`

**实现步骤**:

#### 1. MediaBrowserController 添加拖拽数据

在媒体拖拽开始时，向 DataPackage 添加 MediaIds:

```csharp
// MediaBrowserController.cs

private void MediaView_PointerMoved(object sender, PointerRoutedEventArgs e)
{
    // ... 现有代码 ...
    
    // 在开始拖拽时添加媒体 ID 列表
    if (_isDragSelecting && _viewModel.SelectedMedia.Count > 0)
    {
        var dataPackage = new DataPackage();
        var mediaIds = _viewModel.SelectedMedia.Select(m => m.Id).ToList();
        dataPackage.Properties.Add("MediaIds", mediaIds);
        // 设置其他拖拽数据...
    }
}
```

**注意**: 需要找到当前项目实际的拖拽实现位置，可能不是 `PointerMoved`

#### 2. PlaylistRailView 启用拖放

修改 `MediaLibraryResources.xaml` 中的 `PlaylistRailTemplate`:

```xml
<DataTemplate x:Key="PlaylistRailTemplate" x:DataType="models:Playlist">
    <Border x:Name="ItemBorder"
            Width="34"
            Height="34"
            Background="{Binding ColorHex, Converter={StaticResource PlaylistBackgroundBrushConverter}}"
            CornerRadius="{ThemeResource RadiusSmall}"
            ToolTipService.ToolTip="{Binding Name}"
            AllowDrop="True"
            DragOver="PlaylistItem_DragOver"
            Drop="PlaylistItem_Drop">
        <!-- ... -->
    </Border>
</DataTemplate>
```

#### 3. PlaylistRailView.xaml.cs 添加拖放处理器

```csharp
// PlaylistRailView.xaml.cs

public event Func<string, List<string>, Task>? PlaylistDropRequested;

private void PlaylistItem_DragOver(object sender, DragEventArgs e)
{
    if (e.DataView.Properties.TryGetValue("MediaIds", out _))
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
        if (PlaylistDropRequested != null)
        {
            await PlaylistDropRequested.Invoke(playlist.Id, mediaIds);
        }
        e.Handled = true;
    }
}
```

#### 4. PlaylistRailCoordinator 连接事件

```csharp
// PlaylistRailCoordinator.cs (构造函数中)

_playlistRailView.PlaylistDropRequested += async (playlistId, mediaIds) =>
{
    var items = _viewModel.FilteredMediaItems
        .Where(m => mediaIds.Contains(m.Id))
        .ToList();
    
    if (items.Count > 0)
    {
        await _viewModel.AddMediaToPlaylistAsync(playlistId, items);
        _playlistRailView.HighlightPlaylist(playlistId);
    }
};
```

---

### Task 6: 实现从播放列表拖出移除

**目标**: 在播放列表视图中，拖拽媒体到空白区域，从播放列表移除

**需要修改的文件**:
- `Controllers/MainPage/MediaBrowserController.cs`
- `Views/MainPage/LibraryPaneView.xaml`

**实现步骤**:

#### 1. LibraryPaneView.xaml 启用拖放

找到媒体库面板的空白区域 Border/Grid，添加:

```xml
<!-- LibraryPaneView.xaml -->

<Border x:Name="DropTargetBorder"
        AllowDrop="True"
        DragOver="LibraryPanel_DragOver"
        Drop="LibraryPanel_Drop">
    <!-- 媒体内容 -->
</Border>
```

**注意**: 名字可能已经存在为 `DropTargetBorder`，需要确认

#### 2. MediaBrowserController 添加拖出处理

```csharp
// MediaBrowserController.cs

private void LibraryPanel_DragOver(object sender, DragEventArgs e)
{
    // 检查是否是媒体拖拽
    if (e.DataView.Properties.TryGetValue("MediaIds", out _))
    {
        // 如果当前在播放列表视图，允许 Move 操作（移除）
        if (_viewModel.SelectedPlaylist != null)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
        }
        else
        {
            // 在媒体库视图不允许拖回
            e.AcceptedOperation = DataPackageOperation.None;
        }
        e.Handled = true;
    }
}

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
        
        if (items.Count > 0)
        {
            await _viewModel.RemoveMediaFromSelectedPlaylistAsync(items);
        }
        e.Handled = true;
    }
}
```

#### 3. 在 InitializeAsync 中注册事件

```csharp
// MediaBrowserController.cs InitializeAsync()

_libraryPane.DropTargetBorder.DragOver += LibraryPanel_DragOver;
_libraryPane.DropTargetBorder.Drop += LibraryPanel_Drop;
```

#### 4. 在 Unload 中取消注册

```csharp
// MediaBrowserController.cs Unload()

_libraryPane.DropTargetBorder.DragOver -= LibraryPanel_DragOver;
_libraryPane.DropTargetBorder.Drop -= LibraryPanel_Drop;
```

---

### Task 7: 测试与调试

**测试清单**:

#### 1. 高亮动画测试
- [ ] 通过右键菜单添加媒体到播放列表
- [ ] 验证播放列表按钮高亮 1 秒后恢复
- [ ] 验证通过拖拽添加也触发高亮

#### 2. 拖拽到播放列表测试
- [ ] 在媒体库选择媒体
- [ ] 拖拽到左侧播放列表按钮
- [ ] 验证媒体添加到播放列表
- [ ] 验证高亮动画触发
- [ ] 验证播放列表视图不刷新（优化）

#### 3. 从播放列表移除测试
- [ ] 打开一个播放列表
- [ ] 选择媒体
- [ ] 拖拽到媒体库空白区域
- [ ] 验证媒体从播放列表移除
- [ ] 验证在媒体库视图不能拖回（无操作）

#### 4. 边缘情况测试
- [ ] 全部媒体已在播放列表 → 菜单项灰色
- [ ] 部分媒体在播放列表 → 菜单项可点击
- [ ] 空选择 → 拖拽无效果
- [ ] 拖到相同播放列表 → 无操作/忽略重复

---

## 技术挑战与注意事项

### 1. 拖拽数据传递
**问题**: 当前的拖拽实现可能不使用 DataPackage，而是直接操作 selection

**解决方案**:
- 需要找到实际的拖拽入口点
- 可能是 `DragItemsStarting` 事件
- 或者自定义的拖拽逻辑

**调查点**:
```bash
# 搜索拖拽相关代码
rg "DragItems" --type cs
rg "StartDrag" --type cs
rg "DataPackage" --type cs
```

### 2. 事件生命周期管理
**问题**: 需要正确注册和取消注册事件，避免内存泄漏

**检查清单**:
- [ ] PlaylistRailCoordinator: Dispose() 中取消注册
- [ ] MediaBrowserController: Unload() 中取消注册
- [ ] MediaContextMenuCoordinator: 使用弱事件或明确生命周期

### 3. 视觉状态管理
**问题**: ListViewItem 的 VisualState 管理较复杂

**注意**:
- `VisualStateManager.GoToState()` 需要传入 Control，Border 不是 Control
- 需要在 ListViewItem 上设置状态，而不是内部的 Border
- 可能需要修改 XAML 模板结构

### 4. 性能优化
**问题**: 大量媒体项时的性能

**优化点**:
- 高亮动画使用 DispatcherQueueTimer（已实现）
- 拖放操作只在松开时处理，不在 DragOver 中处理
- FindChild 泛型方法可能较慢，考虑缓存

---

## 实现优先级

### 高优先级
1. **Task 4 剩余部分**: 高亮触发（2-3 个文件，代码量小）
2. **Task 5**: 拖拽到播放列表（核心功能）

### 中优先级
3. **Task 6**: 从播放列表拖出移除

### 低优先级
4. **Task 7**: 全面测试与边缘情况处理

---

## 后续开发建议

### 方案 A: 分阶段实现
1. **阶段 1**: 完成 Task 4 高亮触发（1-2 小时）
2. **阶段 2**: 实现 Task 5 拖拽到播放列表（2-3 小时）
3. **阶段 3**: 实现 Task 6 从播放列表移除（1-2 小时）

### 方案 B: 一次性完成
- 在新会话中使用 `executing-plans` skill 批量执行
- 使用设计文档: `docs/plans/2026-04-03-playlist-drag-drop-enhancement.md`
- 预计 4-5 小时完成所有功能

### 方案 C: 简化版本
- 只实现右键菜单和拖拽到播放列表（Task 4-5）
- 暂时不实现从播放列表拖出（Task 6）
- 预计 3-4 小时

---

## 相关文档

- 设计文档: `docs/plans/2026-04-03-playlist-drag-drop-design.md`
- 实现计划: `docs/plans/2026-04-03-playlist-drag-drop-enhancement.md`
- 提交历史:
  - `6ca336b`: 数据库批量检查方法
  - `70a2823`: 灰显菜单和刷新优化
  - `9591fe2`: 高亮动画基础架构

---

## 联系与问题

如在实现过程中遇到问题:

1. **拖拽数据传递问题**: 检查 `MediaBrowserController` 现有拖拽逻辑
2. **视觉状态问题**: 参考 WinUI 3 VisualStateManager 文档
3. **事件生命周期问题**: 确保所有事件在 Dispose/Unload 中取消注册
4. **性能问题**: 使用性能分析器检查 FindChild 方法开销

---

**创建时间**: 2026-04-03
**预期完成**: 2026-04-04 (方案B) 或 2026-04-05 (方案A)
**状态**: 进行中 (Task 1-3 已完成，Task 4-7 待实现)