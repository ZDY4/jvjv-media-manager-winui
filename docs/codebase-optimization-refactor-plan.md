# 全工程优化与重构计划

更新日期：2026-06-19

## 目的

这份文档基于一次覆盖全工程（约 33k 行）的分模块代码审查，给出**可分阶段落地**的优化与重构计划。它是一份跨模块的总计划，覆盖 Controllers / ViewModels / Services / Data / Views / Utilities 六个模块。

与已有的 [`media-library-architecture-optimization-plan.md`](./media-library-architecture-optimization-plan.md) 的关系：
- 那份文档聚焦**媒体库界面**的具体问题（闪烁、切换失灵、扫描卡住、分组层级等）。
- 本文档覆盖**全工程的工程化质量**（上帝类拆分、进程死锁、资源泄漏、异步崩溃点、DI、Data 层异步化等）。
- 两者有交集的部分（`LibraryShellViewModel`、`MediaBrowserController` 拆分）以本文档的阶段划分为准，落地时交叉引用。

优化目标不是重写，而是：**先消除崩溃/死锁/泄漏等线上风险，再做横切治理降低复制粘贴，最后逐个拆分上帝类。**

> 说明：文中行号来自审查时的快照，实施前请以当前文件为准复核（代码在持续变动）。

---

## 总体评估

### 核心问题：上帝类泛滥

5 个文件占了全工程近一半代码：

| 文件 | 行数 | 状态 |
|---|---|---|
| `ViewModels/MainPage/LibraryShellViewModel.cs` | 3466 | 严重超载 |
| `Controllers/MainPage/MediaBrowserController.cs` | 2875 | 严重超载 |
| `Controllers/MainPage/ClipEditorController.cs` | 2821 | 严重超载 |
| `Controllers/MainPage/VideoPlaybackController.cs` | 2531 | 严重超载 |
| `Controllers/MainPage/MainPageShellController.cs` | 2315 | 严重超载 |

### 三个系统性（横切）问题

收益最大、应优先治理：

1. **UI 线程调度逻辑被复制 5+ 份** —— `RunOnUiThreadAsync` / `DispatcherQueue` 空判断散落在 `LibraryShellViewModel`、`IncrementalMediaCollection` 等处。
2. **FFmpeg 子进程 stdout/stderr 未保证并行排空** —— `VideoClipService`、`TimelineThumbnailStripService`、`VideoHoverPreviewFrameService` 共性高危，缓冲区填满会导致进程死锁。
3. **无 DI 容器** —— 全靠 `MainPageModuleFactory` 手工接线，难测试、生命周期无人管理。

### 做得好的部分（保持）

- Data 层：列名显式（无 `SELECT *`）、参数化到位、批量写有事务、ordinal 读取、索引策略合理、UPSERT 用 `ON CONFLICT DO UPDATE`。
- `MediaItemViewModel` / `MediaItemStore` 用 `Interlocked` CAS 做无锁加载标记。
- `IncrementalMediaCollection` / `MediaPageStore` 的分代（generation）失效机制正确。

---

## 阶段划分总览

| 阶段 | 主题 | 风险 | 改动范围 | 预估 |
|---|---|---|---|---|
| **P0** | 崩溃 / 死锁 / 泄漏热修 | 高 | 局部 | 2-3 天 |
| **P1** | 横切治理（Dispatcher、进程、DI） | 中 | 中等 | 4-6 天 |
| **P2** | Data 层异步化 + Schema 版本化 | 中 | 中等 | 3-4 天 |
| **P3** | 拆分上帝类 | 中 | 大 | 2-3 周 |
| **P4** | 性能与打磨 | 低 | 局部 | 持续 |

每个阶段独立可发布，建议严格按顺序推进（后阶段依赖前阶段铺路）。

---

## P0 — 崩溃 / 死锁 / 泄漏热修（最高优先）

这些是潜在的线上崩溃、卡死、资源泄漏源，改动局部、风险低、收益高。**先做这一批。**

### P0-1 FFmpeg 进程 stdout/stderr 排空死锁（高危）

- **位置**：`Services/VideoClipService.cs`（约 :259-260）、`Services/TimelineThumbnailStripService.cs`（约 :166-167）、`Services/VideoHoverPreviewFrameService.cs`（约 :88-91）。
- **问题**：在 `WaitForExitAsync` 前没有保证 stdout 与 stderr **同时**被异步排空。当 ffmpeg 向其中一个管道写入超过缓冲区容量、而另一个未被读取时，进程阻塞、永不退出。
- **修复**：统一改为 `BaseStream.CopyToAsync` / 异步逐行读取，并 `await Task.WhenAll(stdoutTask, stderrTask)` **之后**再 `WaitForExitAsync`；对整体加超时。
- **附带**：取消时 `Kill(true)` 后应 `await WaitForExitAsync(超时)` 再进 `finally`，避免僵尸进程与残留 partial 文件。
- **验证**：用一个会产生大量 stderr 的输入（如错误编码参数）触发，确认进程能正常退出、不卡死。

### P0-2 VideoPlaybackController 的 async void lambda（高危）

- **位置**：`Controllers/MainPage/VideoPlaybackController.cs:1244` 与 `:1795`（`_dispatcherQueue.TryEnqueue(async () => ...)`）。
- **现状**：lambda 内部已有 try-catch（:1246+），所以不是裸奔，但 `TryEnqueue(async ...)` 本质是 async void，一旦未来有人在 catch 外加代码就会变成未观测异常并崩应用。
- **修复**：确认所有此类 lambda 全程被 try-catch 包裹；建议封装一个 `EnqueueSafeAsync(Func<Task>)` 辅助方法统一兜底（与 P1-1 的 Dispatcher 服务合并）。
- **验证**：在 lambda 内人为抛异常，确认被记录而非崩溃。

### P0-3 MediaPlayer 双重释放竞态（高危）

- **位置**：`VideoPlaybackController.cs`，主 `Dispose`（约 :310）与延迟 `TryEnqueue` 回调里换播放器（约 :756-769）都可能 `Dispose()` 同一个 `_player`。
- **修复**：用 `Interlocked.Exchange` 取出并置空引用后再 dispose，保证单次释放。
- **验证**：快速连续切换媒体 + 关闭窗口压力测试，无 `ObjectDisposedException`。

### P0-4 ThumbnailService 显存泄漏 + 取消不彻底

- **位置**：`Services/ThumbnailService.cs`，LRU 淘汰（约 :406-412）未对 `BitmapImage` 释放原生资源；`ClearCache`（约 :66）后仍有 in-flight 任务可能写回旧缓存。
- **修复**：淘汰时对可释放的位图调用释放；`ClearCache` 用 `CancellationTokenSource` 真正取消在途任务，而非仅靠分代跳过写入。
- **验证**：长时间滚动大库观察 GPU 内存曲线趋于平稳。

### P0-5 MainPage 事件处理器移除失败（泄漏）

- **位置**：`Views/MainPage.xaml.cs:60-61`：`RemoveHandler(..., new KeyEventHandler(MainPage_KeyDown))` 传入**新建的 delegate 实例**，与原注册实例不相等，移除静默失败 → Loaded/Unloaded 循环里处理器累积。
- **修复**：把 delegate 缓存为字段，注册/移除用同一实例。
- **验证**：反复触发 Loaded/Unloaded，确认 KeyDown 只响应一次。

### P0-6 SettingsService 无写防抖（磁盘 I/O 风暴）

- **位置**：`Services/SettingsService.cs`（约 :389），每次 `UpdateSettings` 立即写盘。滑块/开关连续变动会触发上百次写入。
- **修复**：加 ~500ms 防抖，合并为单次落盘；保留临时文件 + 原子重命名，但增加写入完整性校验。
- **验证**：拖动设置滑块，确认落盘次数被合并。

### P0-7 ClipEditor 时间轴片段处理器累积

- **位置**：`Controllers/MainPage/ClipEditorController.cs`，`UpdateUi` 重建 timeline canvas 时为片段重复订阅 `PointerPressed`（约 :1721），未先反订阅。
- **修复**：清空 canvas 前先反订阅，或改用事件委托（父容器统一处理）。
- **验证**：反复编辑片段，内存中处理器数量稳定。

---

## P1 — 横切治理

消除复制粘贴、统一基础设施，为后续拆分铺路。

### P1-1 抽出 `IUiThreadDispatcher` 服务

- **现状**：`RunOnUiThreadAsync` / `DispatcherQueue` 空判断逻辑在 `LibraryShellViewModel`（多处）、`IncrementalMediaCollection`（:235-291）等被复制 5+ 份。
- **方案**：定义 `IUiThreadDispatcher`，提供 `EnqueueAsync(Func<Task>)`、`Enqueue(Action)`、`HasThreadAccess`，内部统一空判断与异常兜底（吸收 P0-2）。所有 VM/控制器改为注入使用。
- **收益**：去重 + 统一异步异常兜底 + 可在测试里替换为同步实现。

### P1-2 FFmpeg 进程执行封装

- **方案**：抽 `FfmpegProcessRunner`（或 `ExternalProcessRunner`），统一负责：参数构造、并行排空 stdout/stderr、超时、取消时优雅结束、退出码处理、partial 文件清理。三个进程类服务改为复用它（落实 P0-1 的统一实现）。
- **收益**：死锁/僵尸进程修复只需做一次；进程逻辑可单测。

### P1-3 引入 DI 容器

- **现状**：`Services/MainPage/MainPageModuleFactory.cs` 手工 new 所有服务，无生命周期管理。
- **方案**：引入 `Microsoft.Extensions.DependencyInjection`，在 `App` 启动时注册服务与控制器，`MainPageModuleFactory` 退化为从容器解析。让工厂/容器负责 `IDisposable` 链式释放。
- **收益**：可测试、可替换实现、集中生命周期管理；为 P3 拆分上帝类时注入子控制器铺路。

### P1-4 Windows 键盘拦截共享化

- **现状**：键盘子类化 + 低级钩子的 P/Invoke 在 `MainWindow.xaml.cs`（:264-409）与 `IndependentMediaWindow.xaml.cs`（:1024-1042）重复约 150 行。
- **方案**：抽 `WindowKeyboardInterceptor`，封装子类化与可选的低级钩子；两个窗口复用。
- **验证**：主窗口全局快捷键与独立窗口窗内快捷键均正常。

### P1-5 集合 diff-patch 工具化

- **现状**：`MediaFolderGroupViewModel`（:199-287）与 `IncrementalMediaCollection.ReplaceLoadedWindow`（:414-491）有近似的 diff-and-patch。
- **方案**：抽 `CollectionDiffApplier<T>`（按 key 做最小增删移），两处复用。

---

## P2 — Data 层异步化与 Schema 版本化

Data 层质量本身好，主要补齐异步与迁移健壮性。

### P2-1 仓储方法真异步

- **现状**：所有仓储方法同步，靠 `MediaLibraryService.cs:29-45` 用 `Task.Run` 包一层（sync-over-async）。
- **方案**：改用 `ExecuteReaderAsync` / `ExecuteScalarAsync` / `ExecuteNonQueryAsync`，去掉 `Task.Run` 包装，全链路异步并贯通 `CancellationToken`。
- **注意**：SQLite 连接非线程安全，异步化后仍需保证单连接不被并发使用（见 P2-3）。

### P2-2 Schema 迁移版本化 + 单事务

- **现状**：`Data/MediaDbSchema.cs:92-251` 逐条 `ALTER TABLE`，无版本号、无事务，迁移中途崩溃会留下不一致状态。
- **方案**：加 `schema_version` 表（或用 `PRAGMA user_version`），按版本号顺序执行迁移，全部包在单事务里；记录每次迁移幂等。
- **附带**：FTS 索引重建（:453-500）目前每次 init 在条件命中时全量重建，改为按需/带时间戳触发。

### P2-3 连接管理与并发

- **方案**：在 `MediaDbConnectionFactory` 启用连接池（`Pooling=true`）；为批量写在仓储层加 `SemaphoreSlim` 串行化，避免并发批量操作的锁竞争。
- **附带**：`GetMediaById` 的 N+1（`MediaRepository.cs:520` 先查 media 再单独查 tags）→ 提供 `JOIN` 版本 `GetMediaWithTagsAsync`。

### P2-4 查询性能（按需）

- 文件夹路径用 `substr(...)` 在 `ORDER BY`/`GROUP BY` 逐行计算（`MediaRepository.cs:1102` 等）→ 评估存储独立 `folderPath` 列并建索引。
- 大偏移 OFFSET 分页 O(n) → 为大库提供 keyset 分页（`WHERE id > $lastId`）。
- 用 `EXPLAIN QUERY PLAN` 验证 `COLLATE NOCASE` 查询确实命中既有索引。

---

## P3 — 拆分上帝类

依赖 P1（DI、Dispatcher 服务）。每个控制器单独一个 PR，拆完即可回归。

### P3-1 VideoPlaybackController (2531) — 建议先拆

理由：有明确崩溃风险点（已在 P0 处理），职责边界清晰。
- `ProgressPreviewController` —— 进度预览帧生成/缓存（约 :1023-1456）。
- `ShufflePlaybackController` —— 随机播放（约 :1884-2038）。
- `PlaybackWatchdogController` —— 启动看门狗/验证（约 :2351-2445）。
- 附带性能：进度预览缓存的 LRU 用 `LinkedList<string>`，每次命中 O(N) 查找 → 改 `Dictionary<string, LinkedListNode<string>>`。

### P3-2 LibraryShellViewModel (3466) — 与媒体库 UI 计划交叉

混了 8 类职责。拆分（与 `media-library-architecture-optimization-plan.md` 对齐）：
- `MediaLibraryRefreshOrchestrator` —— 扫描/增量刷新（最大一块）。
- `MediaFolderHierarchyManager` —— 折叠/展开/缩进状态。
- `WatchedFolderManager`、`PlaylistManager`、`SettingsAndStateManager`。
- 附带：手写 INPC 级联（:214-226，单 setter 触发 3-4 个 `OnPropertyChanged`）→ 迁移到源生成 `[ObservableProperty]`（CommunityToolkit.Mvvm）。
- 附带：扫描状态用「字符串常量 + 原子 int + bool」混合表达（:47-50）→ 抽正式 `ScanState` 枚举状态机。

### P3-3 MediaBrowserController (2875)

- `DragSelectionController` —— 拖拽框选状态机（约 :617-1843）。
- `MediaScrollController` —— 滚动重试逻辑（约 :1975-2164）。
- `MediaSelectionSyncController` —— 选择同步。
- 附带性能：`FilteredMediaItems.Any(...)` 在每次 SelectionChanged 做 O(N) 查找（:1170）→ 用 `HashSet`；拖拽时每次指针移动 `new List`（:1872）→ 复用缓冲。
- 高危：构造函数订阅 40+ 事件，全靠 `Dispose` 反订阅 —— 拆分后务必保证每个子控制器自带订阅/反订阅成对。

### P3-4 ClipEditorController (2821)

- `ClipTimelineUIController` —— 时间轴视觉更新（含 174 行的 `UpdateUi` 拆解）。
- `ClipSegmentEditor` —— 片段编辑。
- `ClipThumbnailLoader` —— 缩略图异步加载（CTS 生命周期收敛）。

### P3-5 MainPageShellController (2315)

- `KeyboardShortcutHandler` —— `HandleShortcutKeyAsync`（328 行）。
- `MissingMediaCleanupWorker` —— 缺失媒体清理（注意 :738 的同步 `File.Exists` 阻塞 UI，改异步批量）。

### P3-6 IndependentMediaWindow code-behind 瘦身

- `Views/IndependentMediaWindow.xaml.cs`（1050 行）900+ 行业务逻辑（导航/删除/标签/手势）堆在 code-behind → 抽 `IndependentMediaWindowController`（仿 `VideoPlaybackController`）。

---

## P4 — 性能与打磨（持续）

- `IncrementalMediaCollection.IndexOfMediaId` 线性查找致 `ReplaceLoadedWindow` 最坏 O(n²)（:493）→ `HashSet` 缓存 ID；两遍去重合并为一遍。
- `MediaItemViewModel`：`PlayerDetailsText` / `TagsText` 每次绑定都 `Where`/`Join`（:39-49），路径操作在 getter 里（:26-28）→ 缓存，仅在底层变更时失效。
- `MediaItemStore`：`GetOrUpdate` 命中也加锁 → 评估 `ConcurrentDictionary`。
- `FolderGroupingStore`：祖先枚举 O(n²)（:36-51）→ 先建祖先映射再迭代。
- `AppTraceLogger`：每次 flush 都 `Directory.CreateDirectory` + `DateTime.Now`（:143-144）→ logPath 每会话算一次。
- `WerReportHarvester`：两次全目录遍历（:127-149）→ 合并为单次遍历。
- `WatchedFolderMonitorService`：`FileSystemWatcher` 默认 8KB 缓冲，短时大量变动静默丢事件 → 增大缓冲并检测溢出。
- `MediaLibraryService`：`_ = Task.Run(BackfillMediaDetailsAsync)` 脱管 fire-and-forget（:627）→ 纳入可追踪集合并在释放时等待；`Volatile` 与 `Interlocked` 在同一变量混用 → 统一。
- Views：`PlaylistRailView`、`SettingsPanel` 订阅事件/定时器但无 `Unloaded` 清理 → 补清理。
- XAML：媒体库面板绑定用 `Binding`（运行时）→ 静态属性评估迁移 `x:Bind`（编译期）。

---

## 落地原则

1. **每个 P0 项一个小 PR**，独立验证后合并，不要攒大改动。
2. **P1 的 Dispatcher 服务与进程封装先行**，因为 P0-1/P0-2 的最终形态依赖它们 —— 可先在 P0 做局部热修，P1 再统一收口。
3. **拆上帝类（P3）必须在 DI（P1-3）落地后做**，否则子控制器接线会再次变成手工泥潭。
4. 每项改动配套验证步骤（见各条目"验证"），媒体库相关回归参考 [`media-library-runtime-validation-checklist.md`](./media-library-runtime-validation-checklist.md)。

---

## 附：审查范围

- Controllers：`MediaBrowserController`、`ClipEditorController`、`VideoPlaybackController`、`MainPageShellController`、`ClipTimelineWebBridge`、`MediaContextMenuCoordinator`、`PlaylistRailCoordinator`、`ImagePreviewController`、`LibraryPaneController`、`MediaSelectionCoordinator`
- ViewModels：`LibraryShellViewModel`、`IncrementalMediaCollection`、`LibraryRefreshScheduler`、`MediaPageStore`、`FolderGroupingStore`、`MediaFolderGroupViewModel`、`MediaItemViewModel`、`MediaItemStore`
- Services：`MediaLibraryService`、`VideoClipService`、`SettingsService`、`ThumbnailService`、`WatchedFolderMonitorService`、`MediaDeletionWorkflow`、`MainPageModuleFactory`、`ContentDialogService`、`TimelineThumbnailStripService`、`VideoHoverPreviewFrameService`、`IndependentMediaWindowService`
- Data：`MediaRepository`、`MediaDbSchema`、`PlaylistRepository`、`MediaTagRepository`、`MediaDb`、`MediaDbConnectionFactory`、`Models/MediaModels`、`Models/MediaQueryModels`
- Views / Utilities：`IndependentMediaWindow`、`MainWindow`、`MainPage`、`SettingsPanel`、`PlaylistRailView`、`MediaBrowserView`、`SettingsDialogCoordinator`、`AppTraceLogger`、`WerReportHarvester`、`CrashDiagnostics`
