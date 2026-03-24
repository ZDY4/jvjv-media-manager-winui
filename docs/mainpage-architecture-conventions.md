# MainPage 架构约定

## 页面壳层

- `Views/MainPage.xaml` 负责组合 `LibraryPaneView` 与 `PlayerPaneView`。
- `Views/MainPage.xaml.cs` 只负责页面加载、卸载、快捷键转发与宿主注入。
- `Controllers/MainPage/MainPageShellController.cs` 只保留跨模块协调、播放器切换、对话流入口与快捷键编排。

## 库区模块

- `LibraryPaneController` 负责库区 Pane 的展开、收起与宽度调整。
- `PlaylistRailCoordinator` 负责播放列表 rail 的切换、创建、重命名、改色、删除与重排。
- `MediaBrowserController` 负责搜索、防抖、标签输入、视图模式、排序、拖放导入、缩略图尺寸与媒体选择。
- `MediaContextMenuCoordinator` 负责媒体右键菜单，统一“打开所在目录 / 编辑标签 / 加入播放列表 / 删除”等操作。

## 播放器模块

- `VideoPlaybackController` 只管理视频播放、控制条、全屏与播放模式。
- `ImagePreviewController` 只管理图片预览、缩放、平移与图片翻页相关交互。
- `ClipEditorController` 只管理剪辑状态、入点 / 出点、导出与片段方案。

## ViewModel 边界

- `LibraryShellViewModel` 持有媒体库数据、播放列表、筛选条件以及库区 UI 状态。
- `PlayerShellViewModel` 持有播放器空态、信息浮层与播放器子模块 ViewModel。
- 视图显隐、按钮 tooltip、按钮 glyph、扫描状态等优先通过绑定驱动，而不是在 controller 中直接写控件属性。

## 生命周期与注册

- `App.MainPageModules` 作为 MainPage 模块注册入口。
- `MainPageModuleFactory` 统一创建 `IContentDialogService`、各 controller 与 coordinator。
- 页面代码不直接 `new` 具体模块，实现集中装配，便于后续替换为测试桩或更完整的 DI 容器。
