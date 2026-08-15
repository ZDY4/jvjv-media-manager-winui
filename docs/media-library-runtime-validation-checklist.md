# 媒体库运行验证清单

这份清单用于验证 `media-library-architecture-optimization-plan.md` 中已经落地的优化是否真的改善了运行体验。

## 构建

```powershell
dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:OutDir=G:\Projects\jvjv-media-manager-winui\.build-check\manual-validation\
```

期望：

- 0 warnings。
- 0 errors。
- Release 配置也应能通过构建，用于确认发布路径没有遗漏编译问题。
- self-contained publish 应能成功生成 `JvJvMediaManager.WinUI.exe`，用于确认便携发布路径没有遗漏运行时依赖。

## 启动缓存优先

步骤：

1. 确保数据库中已有媒体。
2. 启动应用。
3. 不点击刷新或扫描。

期望：

- 媒体库能直接显示数据库中的首屏媒体。
- 启动期间不自动触发完整 `RescanFoldersAsync`。
- 日志中应看到 `InitializeAsync start`、`RefreshMediaAsync completed`。
- 日志中应看到 `Startup cache validation completed`，它只做 watched folder 的浅层快照校验，不应枚举完整子目录树。
- 浅层快照会检查 watched folder 顶层媒体文件数量/最新修改时间，以及顶层子文件夹数量/最新修改时间；它不递归扫描子文件夹内容。
- 如果浅层快照与上次手动扫描记录不同，界面可以提示“媒体库已从缓存打开，部分文件夹可能有变化，可手动刷新”，但不应自动打断首屏交互。

## 新增监控文件夹

步骤：

1. 通过设置窗口添加一个新的可见监控文件夹。
2. 等待导入完成。
3. 关闭并重启应用。

期望：

- 新增 watched folder 时会导入该文件夹媒体。
- 只修改已有 watched folder 的锁定/可见性时不触发完整重新扫描。
- 重启应用后不自动重新扫描该文件夹，只执行启动缓存浅层校验。
- 日志中应看到 `UpdateWatchedFoldersAndImportNewAsync importing new folders`，之后看到扫描完成和 `Saved watched folder scan snapshots`。

## 删除当前播放媒体

步骤：

1. 选择并播放一个媒体。
2. 确认下一个媒体已经在当前加载窗口中。
3. 删除当前播放媒体。

期望：

- 播放器快速切到下一个媒体。
- 媒体库列表中被删媒体立即消失。
- UI 不等待数据库删除和缩略图清理完成。
- 日志中应看到：
  - `Selected replacement before file delete`
  - `DeleteMediaAsync UI removal finished`
  - `Deleted media persistence cleanup completed`
- 删除后立即关闭应用时，日志中可以看到 `Draining deleted media persistence cleanup tasks`，下次启动不应重新出现已移到回收站的媒体。

## 视频切换预览

步骤：

1. 准备两个不同画面或不同纵横比的视频。
2. 切换到第一个视频并等待画面出现。
3. 立即切换到第二个视频。

期望：

- 第二个视频加载期间不显示第一个视频的旧预览图。
- 如果第二个视频已有缩略图，则加载期间显示它自己的缩略图并保持 `Uniform` 适配。
- 如果第二个视频还没有缩略图，则只显示播放器背景，直到当前缩略图或视频 surface ready。
- 日志中应看到 `ShowVideoLoadingPreview hasCurrentThumbnail=...`，不再出现 fallback thumbnail 语义。
- 视频 surface reveal 会等待 `Playing` 且自然尺寸可用后再隐藏 loading preview；兜底等待窗口从约 1.5 秒延长到约 5 秒，减少自然尺寸尚未 ready 时露出未适配首帧的概率。

## 选择状态稳定性

步骤：

1. 多选几个媒体。
2. 执行搜索、排序、删除或等待 loaded window 刷新。
3. 清空搜索或继续滚动加载更多。

期望：

- 选择协调器不会保留已经不在 loaded items 中的旧多选 ID。
- 当前主选中媒体可以在暂时未 loaded 时保留逻辑选择，用于后续恢复。
- 旧多选项重新加载后不会因为过期 selected id 被错误恢复成选中态。
- 日志中可看到 `selection-pruned`，其中 `Remaining` 不应大于当前 loaded selection 加当前主选中媒体。
- 多选同步到 ListView/GridView 时，当前媒体是否在文件夹树中可见应走 folder path/group 内 media id 索引，不应对每个 loaded item 反复线性扫描全部分组和分组内媒体。
- 多选同步到 ListView/GridView 时，如果目标可见选择已经应用，不应先 `Clear` 再重新 `Add` 相同媒体项，避免无意义 `SelectionChanged` 和视觉闪动。
- 多选 no-op 快路径必须严格确认 `SelectedItems` 全部是目标 `MediaItemViewModel`；如果混入非媒体对象或目标不一致，应继续重新同步。

## 分页追加去重

步骤：

1. 在搜索、排序或后台刷新后继续滚动加载下一页。
2. 观察列表/网格和按文件夹分组中的媒体项。

期望：

- 同一个媒体 ID 不应因为 offset 漂移、FTS 索引异常或刷新竞态被追加成重复项。
- 如果下一页返回的全部媒体都已经加载，增量集合会跳过追加并停止继续重复加载，日志中可看到 `range-add-duplicates-skipped`。
- 正常追加时日志中 `range-add-incremental` 会包含 `SkippedDuplicates`，应长期保持较低或为 0。
- 首屏刷新、当前窗口刷新、分页追加三条入口都会在集合边界按媒体 ID 去重；如果仓储或 FTS 竞态返回同页重复记录，日志中可看到 `page-duplicates-skipped`，UI 不应出现重复媒体项。
- 分页请求应绑定发起刷新时的 query generation 快照；搜索、筛选、播放列表快速切换时，旧 generation 的加载更多请求不应读取新 query，新 generation 也不应复用旧 query。
- `MediaPageStore` 的 query state 读取和 generation 映射应通过同一把锁；刷新当前窗口、加载下一页和诊断日志应使用同一次读取到的 query 快照。
- `MediaPageStore.RefreshAsync(query)` 应通过本次 refresh 调用携带的 generation 回调登记 query 快照，不应依赖可被后续 refresh 覆盖的共享 pending query 字段。
- 日志 `load-page` 应包含 `Generation`，用于排查错页、重复页或筛选条件交错问题。

自动化覆盖：

- `.build-check/IncrementalMediaCollectionSmoke` 验证首屏刷新、当前窗口刷新和分页追加在收到同页重复 ID 时不会把重复媒体项放进 UI 集合。
- `.build-check/IncrementalMediaCollectionSmoke` 同时验证本次 refresh 传入的 `generationStarted` 与 page loader 收到的 generation 一致，且构造函数默认 generation callback 不会覆盖本次 refresh 绑定，作为 `MediaPageStore` query generation 快照绑定的底层保障。

## 媒体项 Store 缓存修剪

步骤：

1. 在一个大媒体库中执行多次搜索、排序和筛选切换。
2. 触发完整刷新并等待媒体库恢复可交互。
3. 观察刷新日志中的 `PrunedStoreItems` 和 `PruneStoreMs`。

期望：

- 同一个媒体 ID 仍复用同一个 `MediaItemViewModel` 实例，避免列表、分组、播放器之间对象身份不一致。
- 完整刷新成功并完成选择恢复后，`MediaItemStore` 会只保留当前 loaded window 和当前选中媒体对应的对象。
- 当前窗口刷新和后台元数据刷新成功后，也会按当前 loaded window 和当前选中媒体修剪 `MediaItemStore`，避免长时间只走增量刷新时旧窗口对象滞留。
- 已删除媒体仍通过 `RemoveMany` 立即从 store 移除，不会被修剪保留列表重新保护。
- 空 ID 媒体只返回临时 ViewModel，不进入 store 缓存。
- 修剪发生在选择恢复之后，不应导致当前播放媒体或当前选择丢失。
- `MediaItemStore` 的内部字典读写应具备字典级锁保护，避免删除、修剪和分页对象复用在不同调度路径交错时破坏缓存结构；`MediaItemViewModel.UpdateFrom` 不在锁内执行，避免锁内触发属性通知。

自动化覆盖：

- `.build-check/MediaItemStoreSmoke` 验证同 ID 复用、同 ID 更新、`PruneExcept` 保留列表、`RemoveMany`、`Clear`、空 ID 不入缓存，以及并发 distinct insert + prune/remove 下缓存计数保持一致。

## 缩略图刷新

步骤：

1. 已加载一个媒体并生成缩略图。
2. 修改同一媒体记录的路径、类型、大小、修改时间、分辨率或内容指纹。
3. 触发媒体库 loaded window refresh。

期望：

- `MediaItemViewModel.UpdateFrom` 复用同一媒体对象时，会在缩略图身份变化后清空旧 `Thumbnail` 并重置缩略图加载标记。
- `ThumbnailService` 缓存 key 和磁盘文件名应包含 `Id`、`ModifiedAt`、`Size`、`Type`，降低同一媒体内容变化后命中旧缩略图的风险。
- UI 后续可重新请求新缩略图，不应长期显示旧文件的缩略图。

自动化覆盖：

- `.build-check/ThumbnailStateSmoke` 验证媒体未变化时不会重置缩略图加载标记，大小/类型变化时会重置加载标记，并验证缩略图缓存 key 会随大小/类型变化。

## 独立窗口删除当前媒体

步骤：

1. 使用 `在新窗口中打开` 打开多个媒体。
2. 在独立窗口播放其中一个媒体。
3. 点击删除当前媒体。

期望：

- 如果还有其他媒体，独立窗口先切到下一个媒体，再执行移入回收站和库记录删除。
- 文件删除失败时，独立窗口恢复到原媒体。
- 日志中应看到 `DeleteCurrentMediaAsync switched replacement before file delete`。

## 播放器区域右键菜单目标

步骤：

1. 媒体库中保留一个不包含当前播放媒体的多选。
2. 在播放器区域右键打开菜单。
3. 点击 `在新窗口中打开` 或其他媒体命令。

期望：

- 如果当前播放媒体不在媒体库多选中，播放器区域右键菜单只作用于当前播放媒体。
- 如果当前播放媒体本身属于当前多选，播放器区域右键菜单保留这组多选目标。
- 这样可避免播放器区域右键打开新窗口时打开的是媒体库里旧选中的其他媒体。

## 扫描进度收尾

步骤：

1. 手动扫描一个含少量媒体的文件夹。
2. 等待扫描完成。

期望：

- 完成后扫描进度条隐藏。
- 不再长时间停留在“正在扫描 1 个媒体”。
- 日志中应看到 `AddMediaInternalAsync scan UI finalized`，且 `IsScanning=False`、`ScanValue=0`、`ScanMax=0`。
- 扫描收尾前会递增 `_scanProgressSequence`，使已经投递但尚未执行的旧进度回调失效，避免 finally 清零后又被旧回调写回“正在扫描 ...”。

## 扫描中断检测

步骤：

1. 开始扫描一个较大的文件夹。
2. 在扫描过程中强制结束应用进程。
3. 重新启动应用。

期望：

- 应用仍优先显示数据库缓存中的媒体。
- 不自动触发完整重新扫描。
- 界面提示“媒体库已从缓存打开，上次扫描未正常完成，可手动刷新。”
- 日志中应看到 `Detected interrupted media library scan on startup`。

## 移动或改名后标签保留

步骤：

1. 给一个媒体添加标签。
2. 在 watched folder 内改名，或移动到另一个 watched folder。
3. 执行刷新/扫描。

期望：

- 重新出现的媒体仍保留原标签。
- 不生成 `.json` sidecar 文件。
- 日志中可能看到：
  - `Reusing media id by content fingerprint`
  - `Reusing media id by relocation fingerprint`
  - `Reusing media id by weak relocation fingerprint`

## 无 sidecar 标签存储

源码审查：

- 标签读写走 `MediaDb` / `MediaTagRepository`，不写入媒体旁边的 `.json` sidecar。
- `settings.json` 只保存应用设置、watched folder、浏览状态和扫描快照，不作为单个媒体的标签 sidecar。
- 媒体移动/改名匹配依赖数据库中的 `media_relocations`、内容指纹和弱指纹。
- 导入主路径只在发现同 size 且已有强 `contentHash` 的可复用 media/relocation 候选时同步计算强指纹；普通导入和弱 relocation 匹配会把缺失的内容指纹延后到后台 backfill，降低大文件扫描时的首轮阻塞。

## 文件夹分组

步骤：

1. 打开按文件夹显示。
2. 使用包含多级子文件夹的媒体库。
3. 折叠/展开父文件夹。

期望：

- 子文件夹 header 有递进缩进。
- header 显示当前文件夹名。
- tooltip 显示完整路径。
- 折叠父文件夹后子文件夹隐藏，展开后恢复。
- 当父文件夹处于折叠状态时，通过播放器上一项/下一项或外部同步选中某个子文件夹媒体，应自动展开从 watched root 到该媒体文件夹的祖先链，并能滚动/定位到该媒体。
- 程序化展开祖先链后，应立即更新 folder projection，再继续应用选择和滚动同步，避免 item 还没回到 grouped source 时选择被错过。
- 日志中应看到 `media-folder-groups-diff` 和 `visible-items-diff`，不应频繁出现整组 reset。
- full refresh 期间可以看到 `media-folder-groups-rebuild-suppressed`，表示集合变更触发的冗余分组重建被合并到显式重建里。
- folder projection rebuild 更新折叠状态时不应触发用户折叠事件链；`VisibleItems` 应按最终折叠状态应用一次，减少展开/折叠边界的短暂闪烁。
- folder diff 移除旧 group 时应解除 `CollapseChanged` 事件订阅，避免旧 UI 容器短暂持有 group 时继续反向引用主 ViewModel；日志中可看到 `DetachedGroupCount`。

自动化覆盖：

- `.build-check/FolderGroupingSmoke` 验证父文件夹折叠时隐藏子分组，以及父链重新展开后子文件夹和兄弟分组恢复可见。
- `.build-check/FolderGroupingSmoke` 同时验证 folder projection 更新折叠状态时会立即同步 `VisibleItems`，并且不会触发 `CollapseChanged` 用户事件链。

## 后台 watcher

步骤：

1. 在 watched folder 中连续新增、删除、改名多个媒体文件。
2. 等待 watcher debounce。

期望：

- 多个文件系统事件合并为少量刷新。
- 当前播放不被意外清空。
- 日志中应看到：
  - `Watched folder batch ready`
  - `folder-watcher-processed`
  - `refresh-request-execute`
- 如果 watcher 成功处理了新增/删除/移动，应看到 `Saved watched folder scan snapshots`，重启后不应因刚处理过的变更再次提示浅层快照变化。
- watcher debounce 任务在服务释放或 pending 变化已被清空时不会再派发空 batch；空 batch 会记录 `empty-batch-skipped`，不应触发媒体库刷新。
- watched folder 列表没有变化时，`WatchedFolderMonitorService.SetWatchedFolders` 不应重建全部 `FileSystemWatcher`；日志可看到 `watchers-unchanged`。
- watched folder 集合相同但顺序不同，也不应重建全部 `FileSystemWatcher`。
- watcher requeue 或文件事件传入空路径集合时不应启动 debounce；日志可看到 `empty-change-skipped`，不应产生后续空 batch。
- 如果同一路径在同一个 watcher batch 中同时出现 delete 和仍然存在的 import，应把 delete 视为被 import 覆盖，日志中可看到 `ShadowedDeletes`；这类保存软件常见的 delete+create 同路径事件不应先删除数据库记录，从而避免误清标签。

自动化覆盖：

- `.build-check/WatchedFolderMonitorSmoke` 验证空 requeue 不触发 `ChangesReady`，以及 watched folder 规范化路径未变化时不会重建现有 watcher。
- `.build-check/WatchedFolderMonitorSmoke` 验证相同 watched folder 集合以不同顺序传入时不会重建现有 watcher。
- `.build-check/WatchedFolderMonitorSmoke` 同时验证同路径 live import 会从 effective delete 集合中剔除，普通 delete 仍保留。

## 刷新调度器

期望：

- 同一个 debounce 窗口内的后台刷新请求应合并派发，避免 watcher、后台 metadata、用户筛选连续触发造成刷新风暴。
- 合并请求应选择更高等级的刷新类型和更高优先级的来源。
- 被合并的多个调用方 completion 都应在实际刷新完成后完成，不能因为合并而永久等待。
- 当前刷新执行期间收到的 pending 后台请求，在当前刷新完成后仍应按自身 debounce 延迟派发，不能绕过 debounce 立即连发。

自动化覆盖：

- `.build-check/LibraryRefreshSchedulerSmoke` 验证 debounce 窗口内的 `BackgroundMetadataRefresh` 和 `FullRefresh` 合并成一次 `FullRefresh` 派发，并验证两个 completion 都完成。
- `.build-check/LibraryRefreshSchedulerSmoke` 同时验证刷新执行期间收到的 `BackgroundMetadataRefresh` 会在当前刷新结束后继续等待 debounce 窗口，而不是立刻派发第二次刷新。

## 浏览设置持久化

步骤：

1. 调整媒体库/播放器比例。
2. 切换列表/网格、排序、类型筛选、图标大小。
3. 折叠几个文件夹。
4. 关闭并重启应用。

期望：

- 上述浏览设置恢复。
- 拖动比例时设置文件不会高频写入。

## 旧设置兼容

步骤：

1. 准备包含旧版字段、重复路径大小写或斜杠写法不同的 settings 文件。
2. 启动应用。

期望：

- 应用正常启动，不因重复的文件夹折叠状态或 watched folder 快照 key 崩溃。
- 重复 watched folder 路径按规范化路径合并，并保留最后一次写入的锁定/可见状态。
- 不同斜杠写法或大小写不同的 watched folder 不应创建重复 watcher。
- 新保存的 watched folder 路径应使用规范化路径，后续启动不应再次产生重复项。

## 旧数据库 schema 兼容

场景：

- 开发环境 `run-dev.bat` 使用仓库内 `data/media.db`。
- 旧数据库可能已有 `media`、`media_relocations` 等表，但缺少当前版本新增的核心列，例如 `media.size` 或 `media_relocations.size`。

期望：

- 启动时 `MediaDbSchema.Initialize` 自动补齐缺失列，再创建依赖这些列的索引。
- 不应因为旧库缺列而在启动早期抛出 `SQLite Error 1: 'no such column: size'`。

验证：

- 复现到 `run-dev.bat` 启动后无窗口，日志显示 `Unhandled exception captured. Source='MainPage startup' ... SQLite Error 1: 'no such column: size'`。
- 修复后新增 `EnsureMediaCoreColumns`，并把依赖列的索引创建移到补列之后；`media_relocations` 同步补齐 `path`、`filename`、`type`、`size`、`modifiedAt` 等列。
- `.build-check/DevDbInitializeSmoke` 指向 `.\data` 执行通过，输出 `Dev DB initialize smoke passed.`。
- 再次运行 `cmd /c run-dev.bat` 后，进程窗口标题为 `媒体管理器`，`MainWindowHandle` 非 0。
- 日志出现 `MainPage created`、`OnLaunched completed`、`LibraryShell InitializeAsync completed. LoadedCount=120`、`MediaBrowser InitializeAsync completed. LoadedIndex=120`。
- Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`，结果 0 warnings，0 errors。
- Release 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -c Release -p:Platform=x64`，结果 0 warnings，0 errors。

## 设置文件写入可靠性

步骤：

1. 调整媒体库布局、浏览模式或 watched folder。
2. 确认 settings 文件更新。
3. 模拟连续多次设置保存。

期望：

- settings 写入不会留下长期 `.tmp` 文件。
- settings 文件始终是完整 JSON。
- 应用重启后能恢复最近一次成功保存的设置。
- 如果上次退出前留下了更新的有效 `.tmp` 设置文件，重启时应恢复它；如果 `.tmp` 无效或更旧，应被清理或忽略。
- 重复保存完全相同的浏览状态时，不应再次更新 `settings.json` 文件时间。

## 设置持久化源码审查

结论：

- `LibraryShellViewModel.FlushMediaLibraryViewState` 和 `SaveMediaLibraryViewStateNow` 都会检查 `_persistMediaLibraryViewState`，防止独立媒体窗口的私有 ViewModel 通过直接 flush 路径覆盖主窗口媒体库/播放区比例、浏览模式或文件夹折叠状态。
- `SettingsService` 读取损坏 settings 或无法提升 `.tmp` 文件时会使用默认设置继续启动，并写入采样日志，避免设置损坏变成静默失败。
- 设置保存仍使用写入 `.tmp` 后 move 覆盖的方式，降低半写入 JSON 留在主 settings 文件中的风险。
- settings 落盘写入已在 `SettingsService` 内部串行化，避免扫描状态、watcher 快照、浏览状态等后台路径并发保存时争用同一个 `.tmp` 文件。
- 设置对象的内存修改和 settings 文件原子写入已合并到同一把锁内，避免浏览状态保存、扫描快照保存、设置窗口保存并发时序列化到混合状态。
- 设置 getter 也通过同一把 settings state 锁读取，避免 UI 初始化或后台任务读取设置时撞上并发保存中的中间状态。
- 设置更新前后会比较序列化后的 JSON；如果内容完全一致，会跳过落盘并记录 `save-skipped-unchanged`，减少浏览状态 debounce 后的无意义磁盘写入。

## 完成定义 Audit

对应 `media-library-architecture-optimization-plan.md` 的完成定义：

- 项目可构建：已多次执行 Debug / Release x64 构建，均为 0 warnings、0 errors；self-contained publish 也已通过，并验证 `runtimeconfig.json` 包含 `includedFrameworks`。
- 媒体切换、上一项/下一项、删除后切换稳定：选择链路、播放器切换链路、删除当前媒体先切换再清理、独立窗口删除先切换再删除均有源码审查和 UIA / 日志 smoke；删除当前媒体日志中 `SwitchElapsedMs=6`。
- 媒体库刷新不再频繁整屏闪烁：`IncrementalMediaCollection`、`MediaItemStore`、`FolderGroupingStore`、`LibraryRefreshScheduler` smoke 均通过；日志包含 loaded window / folder group diff 指标，分组更新不走频繁整组 reset。
- 文件夹树缩进、折叠、展开稳定：`FolderGroupingStore smoke passed.`，覆盖 depth、header display name、父折叠隐藏子分组、父链展开后恢复子分组；XAML 使用 spacer width 绑定 `HeaderIndent`。
- 大库启动可先显示缓存数据：1500 媒体大库启动 smoke 证明首屏 `Returned=120, HasMore=True`，`RefreshMediaAsync TotalMs=106`，浅层 cache validation `ElapsedMs=20`，未触发完整扫描日志。
- 扫描状态不会卡住：扫描进度收尾修复有源码审查；清单要求日志 `AddMediaInternalAsync scan UI finalized`，并通过扫描中断启动验证确认不会自动重扫或卡住旧运行状态。
- 标签在移动/改名场景下尽量保留：`MediaDb relocation smoke passed.`，覆盖强指纹和弱指纹 relocation 后原媒体 ID、标签、播放列表关联保留。
- 不生成额外 JSON sidecar：源码审查确认标签读写走 `MediaDb` / `MediaTagRepository`，移动/改名匹配依赖数据库 relocation、内容指纹和弱指纹；settings 只保存应用设置和浏览状态，不作为单媒体 tag sidecar。
- 浏览设置和主界面比例能跨启动恢复：`MediaLibraryViewState` 持久化、独立窗口禁用浏览状态持久化、settings 原子写入/损坏恢复/no-op 保存均有源码审查和 `SettingsNoOpSaveSmoke` 覆盖。
- 日志足以定位下一轮性能问题：清单记录了刷新来源、diff 计数、query generation、删除切换耗时、scan finalize、watcher debounce、cache validation、独立窗口打开/关闭/删除等日志证据。

当前 thread 额外功能要求：

- 独立窗口：已验证多窗口、ESC 关闭、快捷 Tag、删除按钮、播放模式/全屏状态、图片/真实视频普通窗口与全屏截图。
- 右键 `在新窗口中打开`：真实菜单项 click handler 已通过 DEBUG hook + UIA 验证；键盘菜单键 / `Shift+F10` 路径复用同一 coordinator。唯一未被自动化证明的是“真实物理鼠标右键事件 -> 菜单显示 -> 点击在新窗口中打开”。

验证：

- Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`。
- 构建结果：0 warnings，0 errors。
- `.build-check/SettingsNoOpSaveSmoke` 验证重复保存同一 `MediaLibraryViewState` 不会改变 `settings.json` 的 `LastWriteTimeUtc`，而真实变更仍会写入。

## 已执行烟测记录

### 2026-06-07 核心媒体库 smoke 汇总复验

命令范围：

- `dotnet run --project .\.build-check\MediaItemStoreSmoke\MediaItemStoreSmoke.csproj`
- `dotnet run --project .\.build-check\IncrementalMediaCollectionSmoke\IncrementalMediaCollectionSmoke.csproj`
- `dotnet run --project .\.build-check\FolderGroupingSmoke\FolderGroupingSmoke.csproj`
- `dotnet run --project .\.build-check\LibraryRefreshSchedulerSmoke\LibraryRefreshSchedulerSmoke.csproj`
- `dotnet run --project .\.build-check\WatchedFolderMonitorSmoke\WatchedFolderMonitorSmoke.csproj`
- `dotnet run --project .\.build-check\MediaDbRelocationSmoke\MediaDbRelocationSmoke.csproj -- .\.build-check\MediaDbRelocationSmokeData`
- `dotnet run --project .\.build-check\ThumbnailStateSmoke\ThumbnailStateSmoke.csproj`
- `dotnet run --project .\.build-check\SettingsNoOpSaveSmoke\SettingsNoOpSaveSmoke.csproj -- .\.build-check\settings-no-op-save-data`
- `dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`

结果：

- 媒体对象 store、分页去重/query generation、文件夹投影、刷新调度、watcher 合并、移动/改名标签保留、缩略图状态、settings no-op 保存 smoke 均通过。
- WinUI Debug x64 构建通过，0 warnings，0 errors。

续跑复验：

- 2026-06-07 继续优化时重新执行上述 8 个核心 smoke，全部通过：
  - `MediaItemStore smoke passed.`
  - `IncrementalMediaCollection duplicate-page smoke passed.`
  - `FolderGroupingStore smoke passed.`
  - `LibraryRefreshScheduler smoke passed.`
  - `WatchedFolderMonitor smoke passed.`
  - `MediaDb relocation smoke passed.`
  - `Thumbnail state smoke passed.`
  - `Settings no-op save smoke passed.`
- 重新执行 Debug 构建：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`，结果 0 warnings，0 errors。
- 新增执行 Release 构建：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -c Release -p:Platform=x64`，结果 0 warnings，0 errors。
- 2026-06-07 右键菜单稳定性加固后再次顺序执行上述 8 个核心 smoke，全部通过：
  - `MediaItemStore smoke passed.`
  - `IncrementalMediaCollection duplicate-page smoke passed.`
  - `FolderGroupingStore smoke passed.`
  - `LibraryRefreshScheduler smoke passed.`
  - `WatchedFolderMonitor smoke passed.`
  - `MediaDb relocation smoke passed.`
  - `Thumbnail state smoke passed.`
  - `Settings no-op save smoke passed.`

### 2026-06-07 发布版 XAML 资源启动验证

命令范围：

- self-contained publish：`tools/Invoke-WindowsAppSdkDotNet.ps1 -Action Publish ... --self-contained true -p:WindowsAppSDKSelfContained=true`
- 发布目录：`.build-check/publish-self-contained`
- 临时 storage：`.build-check/smoke-publish-after-xaml-fix`

结果：

- 发布目录包含应用自身的 `App.xbf`、`MainWindow.xbf`、`Views/**/*.xbf` 和 `JvJvMediaManager.WinUI.pri`。
- 发布版启动日志出现 `MainPage created` 和 `OnLaunched completed`。
- 未再出现 `InitializeComponent failed` 或 `XamlParseException`。

### 2026-06-07 当前优化版发布 smoke

命令范围：

- self-contained publish：`tools/Invoke-WindowsAppSdkDotNet.ps1 -Action Publish ... --self-contained true -p:WindowsAppSDKSelfContained=true`
- 发布目录：`.build-check/publish-self-contained`
- 临时 storage：`.build-check/smoke-publish-current`
- 启动环境变量：`JVJVMM_STORAGE_ROOT=.build-check/smoke-publish-current`

结果：

- 发布目录包含 `IndependentMediaWindow.xbf`、`App.xbf`、`MainWindow.xbf`、`Views/**/*.xbf` 和 `JvJvMediaManager.WinUI.pri`。
- 发布版进程保持运行到手动停止，没有启动即退出。
- 日志出现 `MainPage created`、`OnLaunched completed`、`LibraryShell InitializeAsync completed` 和 `MediaBrowser InitializeAsync completed`。
- 未出现 `InitializeComponent failed` 或 `XamlParseException`。
- WER crash report harvesting 遇到系统目录无权限时应记录采样 `Skipped WER ... access was denied`，不应把 best-effort 诊断写成启动异常。

复验：

- 2026-06-07 最新优化版 self-contained publish 成功输出到 `.build-check/publish-self-contained`。
- 使用临时 storage `.build-check/smoke-publish-latest` 启动发布版 8 秒后进程仍保持运行，随后手动停止。
- 日志出现 `MainPage created`、`OnLaunched completed`、`LibraryShell InitializeAsync completed` 和 `MediaBrowser InitializeAsync completed`。
- 未出现 `InitializeComponent failed` 或 `XamlParseException`。
- 2026-06-07 当前优化版重新发布时，确认仅设置 `WindowsAppSDKSelfContained=true` 不等于 .NET 自包含；缺少 `--self-contained true` 时会出现 `Required: Microsoft.NETCore.App` runtime 对话框，且不会进入 `App()` 日志。
- 使用 `--self-contained true -p:WindowsAppSDKSelfContained=true` 重新发布后，`runtimeconfig.json` 包含 `includedFrameworks` / `Microsoft.NETCore.App 10.0.8`，不再要求外部 .NET runtime。
- 使用临时 storage `.build-check/smoke-publish-current-verify-fixed` 启动当前发布版 8 秒后进程仍保持运行，随后手动停止。
- 日志出现 `Session started`、`Application initialized`、`MainPage created`、`OnLaunched completed`、`LibraryShell InitializeAsync completed` 和 `MediaBrowser InitializeAsync completed`。
- 未出现 `InitializeComponent failed`、`XamlParseException` 或 runtime 缺失对话框。
- 2026-06-07 继续优化时重新执行 self-contained publish，输出到 `.build-check/publish-self-contained`，命令包含 `--self-contained true` 和 `-p:WindowsAppSDKSelfContained=true`。
- 使用临时 storage `.build-check/smoke-publish-current-verify-continued` 启动当前发布版 8 秒后进程仍保持运行，随后手动停止。
- 重新确认 `JvJvMediaManager.WinUI.runtimeconfig.json` 包含 `includedFrameworks`，并且发布目录存在 `Views/IndependentMediaWindow.xbf`。
- 日志出现 `Session started`、`Application initialized`、`MainPage created`、`OnLaunched completed`、`LibraryShell InitializeAsync completed` 和 `MediaBrowser InitializeAsync completed`。
- 未出现 `InitializeComponent failed`、`XamlParseException` 或 runtime 缺失对话框。

### 2026-06-07 缓存优先启动验证

命令范围：

- 使用发布版 exe。
- 临时 storage：`.build-check/smoke-cache-startup`
- settings 中预置 1 个 visible watched folder 和匹配的 `WatchedFolderScanSnapshots`。

结果：

- 启动后执行数据库查询和浅层快照校验。
- 日志出现 `Startup cache validation completed. Checked=1, Changed=0, MissingPrevious=0`。
- 未出现 `AddMediaInternalAsync scan started`、`AddFolderAsync start` 或 `RescanFoldersAsync start`。
- 浅层快照模型包含顶层子文件夹数量和最新修改时间，用于在不递归扫描的前提下发现软件关闭期间的子文件夹层级变化。

大库复验：

- 使用 `.build-check/LargeLibraryStartupSmoke` 生成 1500 个真实小图片文件、预置 `media.db`、visible watched folder 和匹配的 `WatchedFolderScanSnapshots`。
- 使用发布版 exe 和临时 storage `.build-check/large-library-startup-smoke` 启动，不点击刷新或扫描。
- 日志出现 `QueryMediaPage completed. Offset=0, Limit=120, Returned=120, HasMore=True, ... ElapsedMs=8`。
- 日志出现 `RefreshMediaAsync completed. ... ItemCount=120, ... TotalMs=106`。
- 日志出现 `InitializeAsync completed. LoadedCount=120`。
- 日志出现 `Startup cache validation completed. Checked=1, Changed=0, MissingPrevious=0, ElapsedMs=20`。
- 未出现 `AddMediaInternalAsync scan started`、`AddFolderAsync start` 或 `RescanFoldersAsync start`，说明大库启动走缓存首屏和浅层校验，没有自动完整扫描。

### 2026-06-07 扫描中断启动验证

命令范围：

- 使用发布版 exe。
- 临时 storage：`.build-check/smoke-interrupted-scan`
- settings 中预置 `MediaLibraryScanState.Status = Running`。

结果：

- 启动后日志出现 `Detected interrupted media library scan on startup`。
- 应用继续完成 `InitializeAsync completed`。
- 未自动触发完整扫描。

### 2026-06-07 删除、选择和标签保留源码审查

审查范围：

- `MediaDeletionWorkflow`
- `LibraryShellViewModel.DeleteMediaAsync`
- `MediaSelectionCoordinator`
- `MediaPageStore`
- `MediaRepository`

结论：

- 删除当前媒体时会先调用 `SelectReplacementBeforeDelete`，在移入回收站前预选替代媒体并强制刷新播放器，避免等待文件删除、数据库清理或完整刷新后才切换。
- UI 集合删除后，数据库、缩略图和时间线缩略图清理由后台任务处理；查询层会通过 `ExcludedMediaIds` 排除 pending deleted，避免后台清理完成前被刷新重新带回。
- 选择状态由 `MediaSelectionCoordinator` 管理逻辑 ID，`MediaItemStore` 保证同一媒体 ID 复用同一 `MediaItemViewModel` 实例，降低 List/Grid/ViewModel 之间对象身份不一致造成的切换失灵风险。
- 移动/改名场景使用 `RemoveMediaRecordsOnly` 记录 `media_relocations`，保留 tags 和 playlist 关联；真正删除文件时才清理 tags、playlist 关联和 relocation 记录。
- 普通 Debug 构建通过：`dotnet build src/JvJvMediaManager.WinUI/JvJvMediaManager.WinUI.csproj -p:Platform=x64`。

### 2026-06-07 MediaDb relocation smoke

命令范围：

- 临时 smoke 项目：`.build-check/MediaDbRelocationSmoke`
- 临时数据库：`.build-check/MediaDbRelocationSmokeData`
- 命令：`dotnet run --project .build-check/MediaDbRelocationSmoke/MediaDbRelocationSmoke.csproj -- .build-check/MediaDbRelocationSmokeData`

结果：

- 强指纹 relocation 复用了原媒体 ID。
- 强指纹 relocation 后，原媒体标签仍可通过原媒体 ID 读取。
- 强指纹 relocation 后，原播放列表成员关系仍保留。
- 同 size 强指纹 relocation 会被识别为可复用强指纹候选，不同 size relocation 不会触发同步强指纹计算。
- 弱指纹 relocation 复用了原媒体 ID。
- 弱指纹 relocation 后，原媒体标签仍可通过原媒体 ID 读取。
- 弱 relocation 不会被当作需要同步计算内容指纹的强候选，仍可通过弱匹配复用原媒体 ID。
- 命令输出：`MediaDb relocation smoke passed.`

复验：

- 2026-06-07 当前优化版单独重跑通过：`MediaDb relocation smoke passed.`
- 2026-06-07 最新优化版再次重跑通过：`MediaDb relocation smoke passed.`
- 注意：不要和其他引用 WinUI 项目的 dotnet smoke 并行运行，否则 XAML 编译中间文件可能出现 `obj/.../input.json` 文件锁冲突。

### 2026-06-07 FolderGroupingStore smoke

命令范围：

- 临时 smoke 项目：`.build-check/FolderGroupingSmoke`
- 命令：`dotnet run --project .build-check/FolderGroupingSmoke/FolderGroupingSmoke.csproj`

结果：

- 根文件夹 depth 为 `0`，一级/二级/三级子文件夹 depth 递增。
- header 显示名使用当前文件夹名，例如 `Parent`、`Child`、`Grandchild`。
- `FolderTreePath` 保留相对树路径，例如 `Parent\Child\Grandchild`。
- 父文件夹折叠后，子文件夹节点会被隐藏，父节点仍保留。
- 命令输出：`FolderGroupingStore smoke passed.`
- 源码审查确认 folder projection rebuild 使用非事件式折叠状态更新，避免重建时把状态同步误当成用户折叠操作。
- 源码审查确认 folder diff 移除旧 group 后会解除 `CollapseChanged` 订阅，降低分组频繁变化时的事件生命周期风险。

复验：

- 2026-06-07 当前优化版重跑通过：`FolderGroupingStore smoke passed.`
- 2026-06-07 最新优化版再次重跑通过：`FolderGroupingStore smoke passed.`

### 2026-06-07 媒体项刷新通知收敛

审查范围：

- `MediaItemViewModel.UpdateFrom`
- `MediaItemViewModel.UpdateTags`

结论：

- 同一个媒体 ID 刷新时仍复用同一个 `MediaItemViewModel` 实例。
- `UpdateFrom` 只在对应字段真实变化时触发 `PropertyChanged`，避免每次 loaded window refresh 都让文件名、路径、尺寸、时长、分辨率等绑定全部重绘。
- 标签集合内容未变化时不再执行 `Clear/Add`，避免 Tag 面板和媒体卡片收到无意义集合变更。

验证：

- Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`。
- 构建结果：0 warnings，0 errors。

### 2026-06-07 MediaItemStore 缓存修剪 smoke

审查范围：

- `MediaItemStore`
- `LibraryShellViewModel.RefreshMediaAsync`

结论：

- `MediaItemStore` 新增 `PruneExcept`，用于在完整刷新成功并完成选择恢复后修剪不再属于当前 loaded window 的旧 ViewModel。
- `LoadedWindowRefresh` 和 `BackgroundMetadataRefresh` 完成当前窗口刷新后也会执行同一套 store 修剪，并记录 `refresh-loaded-window-prune`。
- `MediaItemStore` 内部字典读写已加锁，降低外部删除标记、store 修剪和分页对象复用交错时的字典并发风险。
- 修剪保留当前 `FilteredMediaItems` 和当前 `SelectedMedia` 的 ID，避免影响当前播放/选择。
- 刷新完成日志新增 `PrunedStoreItems` 和 `PruneStoreMs`，用于观察大库多轮搜索/筛选后的 store 回收情况。

验证：

- `dotnet run --project .\.build-check\MediaItemStoreSmoke\MediaItemStoreSmoke.csproj` 通过，输出 `MediaItemStore smoke passed.`
- Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`，0 warnings，0 errors。
- `dotnet run --project .\.build-check\IncrementalMediaCollectionSmoke\IncrementalMediaCollectionSmoke.csproj` 通过，输出 `IncrementalMediaCollection duplicate-page smoke passed.`
- `dotnet run --project .\.build-check\ThumbnailStateSmoke\ThumbnailStateSmoke.csproj` 通过，输出 `Thumbnail state smoke passed.`
- 2026-06-07 补充验证：`dotnet run --project .\.build-check\LibraryRefreshSchedulerSmoke\LibraryRefreshSchedulerSmoke.csproj` 通过，输出 `LibraryRefreshScheduler smoke passed.`

### 2026-06-07 FTS 搜索结果去重保护

审查范围：

- `MediaDbSchema.EnsureMediaSearchIndex`
- `MediaRepository.BuildQueryCommand`
- `MediaRepository.BuildFolderSummaryCommand`

结论：

- 启动 schema 校验现在同时检查 `media_search` 总行数和 `COUNT(DISTINCT mediaId)`，如果历史数据或异常中断导致 FTS 索引重复/缺失，会触发重建。
- 媒体分页查询和文件夹摘要查询中的 FTS 子查询使用 `SELECT DISTINCT mediaId`，避免重复索引行把同一媒体重复返回到 UI。
- 该保护减少搜索状态下重复媒体破坏选择、切换、分组计数和分页窗口的风险。

验证：

- Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`。
- 构建结果：0 warnings，0 errors。

### 2026-06-07 独立媒体窗口源码审查

审查范围：

- `IndependentMediaWindow`
- `IndependentMediaWindowService`
- `IndependentMediaWindowMedia`
- `VideoPlaybackController`
- `PlayerPaneView`

结论：

- 独立窗口使用与主窗口相同的 `PlayerPaneView`，视频播放、进度条、音量、播放模式和全屏按钮由同一个 `VideoPlaybackController` 接管。
- 独立窗口支持在窗口媒体队列内左右切换；窗口队列来自打开时的媒体快照，不改变主窗口选中项和播放列表状态。
- 独立窗口已接入快捷 Tag 提示、快捷 Tag 应用、Tag 移除、删除当前媒体和 `Esc` 关闭窗口。
- 独立窗口中的 Tag 变化会同步回主窗口当前加载集合；如果主窗口正在显示该媒体，播放器信息区的快捷 Tag 会同步刷新。
- 独立窗口删除媒体后会通知主窗口立即移除当前加载集合中的对应媒体；如果删除的是主窗口当前播放媒体，主窗口会释放当前播放源并切到替代媒体或清空播放器。
- 独立窗口删除当前媒体有重入保护；连续点击删除按钮或按住 Delete 时，同一窗口不会并发执行多个删除流程，重复请求会记录 `delete-current-reentrant`。
- 如果独立窗口删除的媒体不在主窗口当前 loaded window 中，主窗口会先调用 `MarkMediaDeletedExternally` 将 ID 加入 pending deleted 集合并排队幂等数据库清理，再刷新媒体库，避免刷新早于独立窗口数据库清理时把已删除媒体重新查出。
- 独立窗口关闭时会释放视频播放控制器、图片预览控制器和窗口私有的库 ViewModel，避免保留 watcher/scheduler 等后台对象。
- 独立窗口的私有库 ViewModel 不启动 watched-folder 监听，避免每开一个独立窗口就额外挂一组文件系统 watcher。
- 窗口私有库 ViewModel 禁用媒体库浏览状态持久化，避免关闭独立窗口时用旧快照覆盖主窗口的媒体库/播放区比例、浏览模式或文件夹折叠状态。
- 独立窗口媒体快照转回 `MediaFile` 时会规范化路径并补充文件大小，保持与主媒体库路径模型一致。
- 视频切换加载预览不再复用上一个视频的 preview 图，避免切换时短暂出现旧图或旧比例，直到当前视频缩略图/视频 surface ready。
- 独立窗口安装窗口级 keyboard subclass 捕获 `Esc`，避免焦点落在 MediaPlayer 或内部控件时仅靠 XAML `KeyDown` 漏掉关闭请求。
- 媒体列表、媒体网格、媒体项和播放器侧按钮增加稳定的 `AutomationProperties`，便于 UI Automation 定位，也改善键盘/辅助功能可访问性。
- 播放模式按钮和全屏按钮会把当前状态同步到 `AutomationProperties.Name` / `HelpText`：例如 `播放模式：列表循环`、`播放模式：单曲循环`、`全屏（当前窗口）`、`退出全屏（当前全屏）`，方便辅助功能和 smoke 读取真实状态。
- 媒体列表和网格支持键盘菜单键 / `Shift+F10` 打开与鼠标右键相同的媒体上下文菜单；该路径复用 `MediaContextMenuCoordinator`，不新增可见按钮。
- 媒体列表和网格额外监听 WinUI `ContextRequested` 事件，并在右键 `PointerReleased` 上保留兜底入口；三个入口都会复用 `MediaContextMenuCoordinator` 并有短时间去重，降低真实右键、触控长按或系统上下文菜单请求被 `RightTapped` 漏掉的风险。
- `JVJVMM_SMOKE_OPEN_INDEPENDENT_WINDOW_IDS` 是 DEBUG-only smoke hook；设置为一个或多个媒体 ID 时，主页面初始化完成后会复用真实 `IndependentMediaWindowService` 打开独立窗口，默认运行和 Release 构建不受影响。
- `JVJVMM_SMOKE_SHOW_CONTEXT_MENU_MEDIA_ID` 是 DEBUG-only smoke hook；设置为媒体 ID 时，主页面初始化完成后会复用真实 `MediaContextMenuCoordinator` 显示该媒体的上下文菜单，默认运行和 Release 构建不受影响。

验证：

- Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`。
- 构建结果：0 warnings，0 errors。
- Release 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -c Release -p:Platform=x64`，结果 0 warnings，0 errors。
- `.build-check/UiIndependentWindowSmoke` 可生成一个含 2 张图片、快捷 Tag、watched folder 设置和预置数据库的小媒体库夹具。
- 使用该夹具启动发布版时，日志显示 `QueryMediaPage completed ... Returned=2`、`RefreshMediaAsync completed ... ItemCount=2`、`InitializeAsync completed. LoadedCount=2`。
- Windows UI Automation RawView 可以定位 `MediaListView`、媒体项 `independent-window-alpha.png` / `independent-window-beta.png`，以及快捷 Tag 按钮。
- 通过 UIA `SelectionItemPattern.Select()` 选择 `independent-window-alpha.png` 后，日志显示 `SelectedMedia changed`、`SelectionSyncStart`、`UpdatePlayerStart` 和 `ShowImageState invoked`，证明预置媒体库和主播放器切换链路在运行时可用。
- 设置 `JVJVMM_SMOKE_OPEN_INDEPENDENT_WINDOW_IDS=ui-independent-alpha,ui-independent-beta` 启动发布版后，UIA 观察到主窗口、alpha 独立窗口和 beta 独立窗口同时存在，`WindowCountBeforeEsc=3`。
- 向 alpha 独立窗口句柄投递 `WM_KEYDOWN/Escape` 后，UIA 观察到 alpha 窗口关闭、beta 独立窗口和主窗口仍存在：`IndependentAlphaStillOpenAfterPostedEsc=False`、`IndependentBetaStillOpenAfterPostedEsc=True`、`MainWindowStillOpenAfterPostedEsc=True`。
- 日志确认 `Escape received by window subclass. MediaId='ui-independent-alpha'.`，随后 `Closed. ActiveWindowCount=1`。
- 设置 `JVJVMM_SMOKE_SHOW_CONTEXT_MENU_MEDIA_ID=ui-independent-alpha` 启动 Debug 发布版后，UIA 能定位真实菜单项 `在新窗口中打开` 并通过 `InvokePattern` 触发。
- 菜单项 smoke 结果：`ContextMenuInvokableItemFound=True`、`ContextMenuItemInvoked=True`、`IndependentAlphaOpenedByMenu=True`、`IndependentAlphaStillOpenAfterPostedEsc=False`、`MainWindowStillOpenAfterPostedEsc=True`。
- 日志确认菜单链路进入真实 click handler：`OpenInNewWindow requested. SelectionCount=1.`，随后 `IndependentMediaWindow Opened`、`Escape received by window subclass` 和 `Closed. ActiveWindowCount=0`。
- 设置 `JVJVMM_SMOKE_OPEN_INDEPENDENT_WINDOW_IDS=ui-independent-alpha` 启动发布版后，UIA 能在独立窗口内定位 `DeleteCurrentMediaButton` 并通过 `InvokePattern` 触发。
- 独立窗口删除按钮 smoke 结果：`DeleteButtonFound=True`、`DeleteButtonInvoked=True`、`IndependentWindowSwitchedToBeta=True`、`MainWindowStillOpenAfterDelete=True`、`AlphaFileExistsAfterDelete=False`、`BetaFileExistsAfterDelete=True`。
- 日志确认独立窗口删除路径先切换再删除：`DeleteCurrentMediaAsync switched replacement before file delete ... SwitchElapsedMs=6.`，随后主窗口同步 `Synced deleted media to main window`，独立窗口完成 `DeleteCurrentMediaAsync completed ... Remaining=1`。
- 设置 `JVJVMM_SMOKE_OPEN_INDEPENDENT_WINDOW_IDS=ui-independent-alpha` 启动发布版后，UIA 能在独立窗口内定位并 Invoke `PlaybackModeButton` 和 `FullScreenButton`。
- 播放模式/全屏按钮 smoke 结果：`PlaybackModeButtonInvoked=True`、`FullScreenButtonInvoked=True`、`IndependentWindowStillOpenAfterButtonInvokes=True`、`MainWindowStillOpenAfterButtonInvokes=True`。
- 补充按钮状态 smoke：使用 `.build-check/Invoke-IndependentButtonStateSmoke.ps1` 打开 `ui-independent-alpha` 独立窗口并读取 UIA 状态。
  - 初始状态：`PlaybackButtonInitialName=播放模式：列表循环`、`FullScreenButtonInitialName=全屏（当前窗口）`。
  - 选择播放模式菜单中的 `单曲循环` 后：`PlaybackButtonAfterSingleLoop=播放模式：单曲循环`。
  - 点击全屏后：`FullScreenButtonAfterEnter=退出全屏（当前全屏）`。
  - 再次点击退出全屏后：`FullScreenButtonAfterExit=全屏（当前窗口）`。
  - 结果：`IndependentButtonStateSmokePassed=True`。
- 补充独立窗口视觉 smoke：使用 `.build-check/Invoke-IndependentVisualSmoke.ps1` 打开 `ui-independent-alpha` 独立窗口并保存截图。
  - 普通窗口截图：`.build-check/independent-window-visual-smoke-output/independent-window-windowed.png`，窗口 rect 为 `1440,246,960,540`。
  - 全屏截图：`.build-check/independent-window-visual-smoke-output/independent-window-fullscreen.png`，窗口 rect 为 `0,0,3840,1080`。
  - 全屏状态：`FullScreenButtonAfterEnter=退出全屏（当前全屏）`，退出后 `FullScreenButtonAfterExit=全屏（当前窗口）`。
  - 视觉检查结果：普通窗口和全屏截图中标题、分辨率、标签、快捷 Tag、播放模式/全屏/删除按钮可见，没有明显控件重叠；全屏截图无标题栏，窗口覆盖整个工作区。
- 补充真实视频独立窗口视觉 smoke：使用 `.build-check/UiIndependentVideoWindowSmoke` 生成一个 2 秒、320x180、30fps 的真实 AVI 视频，媒体 ID 为 `ui-independent-video`，再使用 `.build-check/Invoke-IndependentVisualSmoke.ps1` 打开独立窗口并保存截图。
  - 普通视频窗口截图：`.build-check/independent-video-window-visual-smoke-output/independent-window-windowed.png`。
  - 全屏视频窗口截图：`.build-check/independent-video-window-visual-smoke-output/independent-window-fullscreen.png`。
  - 普通窗口 rect 为 `1440,246,960,540`；全屏 rect 为 `0,0,3840,1080`。
  - 视频画面在普通窗口和全屏状态下均成功渲染；全屏下按比例适配并左右留黑边，底部播放控制条、快捷 Tag、播放模式/全屏/删除按钮可见，没有明显控件重叠。
- 2026-06-07 补充右键稳定性修复后，Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`，结果 0 warnings，0 errors。
- 2026-06-07 补充右键稳定性修复后，Release 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -c Release -p:Platform=x64`，结果 0 warnings，0 errors。
- 2026-06-07 重新 self-contained publish 成功，输出到 `.build-check/publish-self-contained`。
- 2026-06-07 播放模式/全屏可访问状态补充后，Debug 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -p:Platform=x64`，结果 0 warnings，0 errors。
- 2026-06-07 播放模式/全屏可访问状态补充后，Release 构建通过：`dotnet build .\src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj -c Release -p:Platform=x64`，结果 0 warnings，0 errors。
- 物理右键自动化 smoke 使用 `.build-check/Invoke-PhysicalRightClickSmoke.ps1`、`SendInput`、UIA RawView 和 `.build-check/UiIndependentWindowSmoke` 夹具。脚本已确认在置顶应用窗口后右键坐标命中应用进程：`PreRightClickHitHwnd=... HitPid=<app pid> ForegroundPid=<app pid>`，但当前 `SendInput` 仍未触发 WinUI 的 `RightTapped` / `ContextRequested` / `PointerReleased` 菜单入口，因此该脚本不能作为真实鼠标右键验收证据。

仍需手动验证：

- 物理鼠标右键是否能在列表/网格媒体项上稳定弹出上下文菜单；当前 smoke 已验证真实菜单项 click handler，但不是由鼠标右键事件触发菜单显示。
- 当前 UIA / SendInput 输入注入未能可靠触发 WinUI 右键事件，即使命中应用窗口也没有产生菜单事件。因此“物理右键 -> 菜单显示 -> 在新窗口中打开”的端到端自动化证据仍缺失；需要真实鼠标手动验收或后续引入更稳定的 WinUI UI 测试驱动。
