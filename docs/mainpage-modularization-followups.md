# MainPage 模块化拆分后续优化建议

## 当前状态

`MainPage` 已经降级为页面壳层与组合根：

- `MainPage.xaml` 仅装配 `LibraryPaneView` 与 `PlayerPaneView`
- `MainPage.xaml.cs` 仅负责模块初始化、销毁与快捷键路由接线
- 播放器、图片预览、剪辑、对话框流程已拆到独立 controller / coordinator

这次改造已经完成“页面不再承载完整业务逻辑”的核心目标，但仍有一些很值得继续推进的优化点。

## 建议 1：继续拆分库区行为

目前 `MainPageShellController` 仍承担较多库区相关职责，建议继续抽离：

- `LibraryPaneController`
- `PlaylistRailCoordinator`
- `MediaBrowserController`
- `MediaContextMenuCoordinator`

优先把以下逻辑从 `MainPageShellController` 迁出：

- 搜索防抖、`#tag` 快捷输入
- 播放列表创建、重命名、删除、调色、重排
- 列表 / 网格切换与项尺寸计算
- 右键菜单与“打开所在目录 / 删除 / 加入播放列表”
- 拖放导入与库区状态条刷新

这样可以把当前壳层 controller 再收缩一大截，接近纯协调者角色。

## 建议 2：把更多 UI 状态改成绑定驱动

播放器区已经开始转向 ViewModel 绑定，但库区仍有不少“控制器直接改控件”的代码。

建议继续把以下状态推到 ViewModel：

- 扫描进度显隐与状态文本
- 库区展开 / 收起状态
- 当前视图模式按钮图标与提示
- 排序按钮图标与提示
- 选中标签条显隐
- 播放器信息浮层、空态显隐

目标是减少：

- 直接写 `TextBlock.Text`
- 直接写 `Visibility`
- 直接写 `Opacity`
- 直接写按钮 glyph / tooltip

这样更利于测试，也更容易把模块复用到其他页面或窗口。

## 建议 3：让 controller 构造与生命周期走统一注册

当前 `MainPage` 壳层已经很薄，但 controller / coordinator 仍是手工 `new`。

建议下一步统一接入应用级注册：

- 在应用启动时注册 `IContentDialogService`
- 注册 `DialogWorkflowCoordinator`
- 注册 `VideoPlaybackController` / `ImagePreviewController` / `ClipEditorController`
- 为壳层 ViewModel 与 controller 增加统一的生命周期约定

这样做的价值：

- 更容易做单元测试与替换假对象
- 更容易在未来引入多窗口或多页面复用
- 页面 code-behind 可以进一步只保留宿主能力注入

## 建议 4：补回归测试与冒烟验证清单

这次改造触达交互面非常大，建议把高风险流程沉淀成可重复验证项。

最值得优先覆盖的场景：

- 搜索、标签过滤、排序、列表 / 网格切换
- 播放列表创建、重命名、删除、颜色修改、拖拽排序
- 视频播放、进度拖动、音量、全屏、自动隐藏控制条
- 图片缩放、双击、拖拽平移、热点翻页
- 剪辑入点 / 出点、片段方案、导出目录、导出完成后回灌媒体库
- 设置、锁定管理、标签编辑、播放列表选择等对话流程

建议至少形成：

- 一个手工回归清单
- 一组关键 ViewModel / coordinator 单元测试

## 建议 5：收尾命名与边界一致性

现在总体结构已经清晰，但仍建议再统一一轮命名边界：

- `Views/MainPage` 与命名空间 `Views.MainPageParts` 的长期约定
- `Controller` 与 `Coordinator` 的职责边界定义
- `ShellViewModel` / `ModuleViewModel` 的命名标准
- 结果对象与请求对象的命名对称性

可以补一个简短的架构约定文档，避免后续功能继续回流到 `MainPageShellController`。

## 建议 6：逐步淘汰遗留兼容层

本次为了平滑迁移保留了一些兼容式桥接写法，这很合理。后续建议逐步清理：

- 再次审视 `MainPageShellController` 中仍保留的库区杂项逻辑
- 清理不再需要的旧辅助方法
- 确认旧 `MainViewModel` 是否还能继续压缩使用面或彻底退场

目标不是一次性“做绝”，而是在每次功能迭代时顺手把桥接层再削薄一点。
