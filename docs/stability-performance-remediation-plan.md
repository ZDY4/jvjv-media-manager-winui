# 稳定性与性能专项修复优化方案

更新日期：2026-07-11

## 1. 文档目的

本文档基于 2026-07-11 对当前工作树进行的只读审计，针对以下维度制定可执行的修复与优化路线：

- 稳定性与数据一致性。
- SQLite 事务、连接配置和查询性能。
- 异步、取消与应用关闭。
- 缩略图、时间轴帧和磁盘缓存。
- 主窗口与独立窗口的播放资源协调。
- UI 响应和播放器切换成本。
- 日志容量、可观测性和故障诊断。
- 核心大类的职责边界和后续可维护性。

本文档不是重写计划。实施原则是先修复确定性的数据一致性和关闭丢数据风险，再补齐资源治理与运行指标，最后实施分页和架构演进。

### 1.1 与现有文档的关系

- [`codebase-optimization-refactor-plan.md`](./codebase-optimization-refactor-plan.md) 是全工程早期总计划，其中 DI、`IUiThreadDispatcher`、`ExternalProcessRunner`、部分控制器拆分等工作已经在当前代码中落地。
- [`media-library-architecture-optimization-plan.md`](./media-library-architecture-optimization-plan.md) 聚焦媒体库选择、分页、分组、刷新和对象身份。
- [`media-library-runtime-validation-checklist.md`](./media-library-runtime-validation-checklist.md) 是现有媒体库运行回归清单。
- 本文档只描述当前审计仍然存在或需要继续收口的专项问题。若与早期计划描述冲突，以当前代码和本文档为准。

## 2. 当前基线

### 2.1 构建基线

审计时执行：

```powershell
dotnet build JvJvMediaManager.sln
```

结果：

- 0 errors。
- 2 warnings，均来自 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 的已知高严重性漏洞 `GHSA-2m69-gcr7-jv3q`。

该警告属于已知依赖问题，不应掩盖本文档列出的数据库正确性问题。后续仍需定期执行：

```powershell
dotnet list src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj package --include-transitive --vulnerable
```

### 2.2 已有基础设施

当前代码已经具备以下基础，不应重复建设：

- `Microsoft.Extensions.DependencyInjection` 服务容器。
- `IUiThreadDispatcher`。
- `ExternalProcessRunner`，支持 stdout/stderr 并行排空、超时和取消后杀进程树。
- `MediaPageStore` generation 失效机制。
- `ThumbnailService` 内存 LRU 和缓存 generation。
- `MediaLibraryService` 后台回填任务跟踪与释放取消。
- `PlaybackResourceCoordinator` 的初版后台 I/O 节流。
- `AppTraceLogger` 的异步批量写入和短期内存尾部日志。

本方案重点是修正这些基础设施尚未闭合的边界。

## 3. 问题与工作包映射

| ID | 优先级 | 问题 | 主要工作包 |
|---|---:|---|---|
| DB-01 | P0 | 显式事务内部分命令未绑定事务 | WP-01 |
| DB-02 | P0 | SQLite 连接级 PRAGMA 和 busy 策略不统一 | WP-02 |
| SET-01 | P0 | 设置防抖保存缺少关闭 flush/dispose | WP-03 |
| PLAY-01 | P0 | 多播放器共享单一 bool，独立窗口未计入 | WP-04 |
| ASYNC-01 | P1 | WinRT 元数据读取不能及时响应取消 | WP-05 |
| CACHE-01 | P1 | 缩略图和时间轴磁盘缓存无配额 | WP-06 |
| PLAY-02 | P1 | 每次切换创建新 `MediaPlayer` 成本高 | WP-07 |
| DB-03 | P1 | OFFSET 深分页退化，现有游标语义错误 | WP-08 |
| LOG-01 | P1 | 日志文件、采样 key、失败队列无上限 | WP-09 |
| SEC-01 | P1 | SQLitePCLRaw 存在已知高严重性依赖漏洞 | WP-11 |
| ARCH-01 | P2 | 核心大类仍有过多编排和状态职责 | WP-10 |

## 4. 总体实施顺序

| 阶段 | 内容 | 预计改动风险 | 发布要求 |
|---|---|---:|---|
| Phase 0 | 建立自动化基线和运行指标 | 低 | 可单独发布 |
| Phase 1 | 数据库事务与连接策略 | 高 | 独立发布并重点回归 |
| Phase 2 | 设置关闭落盘与多播放器资源租约 | 中 | 独立发布 |
| Phase 3 | 取消边界、缓存和日志容量治理 | 中 | 可拆成多个小版本 |
| Phase 4 | 播放器切换策略优化 | 中高 | 功能开关保护 |
| Phase 5 | 正确的复合 keyset 分页 | 高 | 双路径灰度验证 |
| Phase 6 | 架构收口和大类拆分 | 中 | 持续实施 |

依赖关系：

1. WP-01 与 WP-02 必须先于大规模数据库性能调整。
2. WP-04 必须先于 WP-05、WP-06 和 WP-07，否则后台资源优先级判断仍不准确。
3. WP-08 必须在查询契约测试完成后实施，不能直接启用当前 `AfterMediaId`。
4. WP-10 不应阻塞前面的正确性修复，也不应与 WP-01 或 WP-08 放进同一个 PR。
5. WP-11 可与功能修复并行评估，但依赖升级必须单独提交，避免把运行时兼容问题与业务改动混在一起。

## 5. Phase 0：基线与防回归

### WP-00 建立专项验证项目

#### 目标

为数据库事务、分页、设置关闭保存和资源租约建立可重复验证，减少只能依赖 GUI 手测的风险。

#### 方案

新增一个轻量测试项目，例如：

```text
tests/JvJvMediaManager.Core.Tests/
```

优先测试不依赖 WinUI 视觉树的组件：

- `MediaDb`、各 Repository 和 schema 初始化。
- 设置持久化 writer。
- 播放资源租约协调器。
- 缓存清理策略和日志保留策略。
- 媒体查询排序与分页契约。

若暂时不引入正式测试项目，至少把测试逻辑放入 `tools/` 下的 PowerShell/.NET smoke 工具，避免继续复制到 `.build-check/` 临时目录中。

#### 必测场景

- 事务中途取消或抛异常后，`media`、`tags`、`media_search`、`media_relocations` 状态一致。
- 两个并发写任务不会产生未处理的 `SQLITE_BUSY`。
- 设置修改后立即关闭，最终文件包含最后一次修改。
- 两个播放器租约重叠时，释放任意一个不会把全局播放状态清零。
- 所有排序字段在分页后无重复、无遗漏，结果等于一次性全量查询。

#### 完成定义

- 测试可由 PowerShell 7 在仓库根目录执行。
- CI 或本地统一命令可以运行全部专项测试。
- 后续每个工作包至少补一条失败前能复现、修复后通过的用例。

## 6. Phase 1：数据库正确性与并发

### WP-01 显式事务上下文收口

#### 风险说明

`MediaRepository.UpsertMediaBatchAsync` 创建事务后，主 upsert 命令绑定了事务，但以下辅助操作没有统一绑定：

- 媒体 ID 复用查询。
- FTS 可用性检查。
- 搜索索引的查询、删除和插入。
- 删除和 relocation 路径中的搜索索引操作。

这会破坏单次批处理的原子边界，并可能在 `Microsoft.Data.Sqlite` 上触发事务关联异常。

#### 目标设计

所有接受现有连接的方法同时接受当前事务：

```csharp
private static Task RefreshSearchIndexAsync(
    SqliteConnection connection,
    SqliteTransaction transaction,
    string mediaId,
    CancellationToken cancellationToken);
```

规则：

- 只要调用方开启显式事务，该连接上创建的每一个命令都必须设置同一个 `Transaction`。
- Repository 公共方法负责打开连接、创建事务、提交或回滚。
- 内部 helper 不自行提交，不偷偷打开第二条写连接。
- 只读 helper 也显式绑定事务，保证读取的是本次事务内刚写入的数据。

#### 实施步骤

1. 为 `MediaRepository` 内所有 connection-based helper 增加 `SqliteTransaction?` 参数。
2. 统一通过一个小型 `CreateCommand(connection, transaction)` helper 创建命令。
3. 覆盖同步与异步路径，避免两套实现再次漂移。
4. 检查 `MediaTagRepository` 和 `PlaylistRepository` 的批量方法，确保相同规则。
5. 在事务测试中注入异常，验证回滚后四张相关表无部分更新。
6. 在取消测试中从批量循环中间取消，验证事务回滚且 `_writeGate` 正常释放。

#### 建议附带改进

- 删除未使用或只被异步入口替代的同步私有写路径，减少双实现维护成本。
- 为写事务增加结构化操作名和耗时日志，但不要逐行记录媒体路径。

#### 验收标准

- 不再存在显式事务内创建但未设置 `Transaction` 的命令。
- upsert、删除、records-only 删除在异常和取消后保持原子性。
- FTS 搜索结果与媒体、标签状态同步。
- 10,000 条媒体批量导入过程中无事务关联异常。

### WP-02 统一 SQLite 连接初始化和 busy 策略

#### 风险说明

当前 `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;` 只在 schema 初始化连接执行。`synchronous`、`busy_timeout`、外键检查等连接级行为没有在每个物理连接上统一配置。

#### 目标设计

`MediaDbConnectionFactory` 是唯一连接出口。每次打开连接后调用统一初始化：

```text
OpenConnection[Async]
  -> Open
  -> ApplyConnectionPragmas
  -> return configured connection
```

建议配置：

- `PRAGMA foreign_keys=ON;`
- `PRAGMA busy_timeout=5000;`
- `PRAGMA synchronous=NORMAL;`
- `PRAGMA temp_store=MEMORY;` 仅在实测收益明确后启用。
- `journal_mode=WAL` 在数据库初始化/迁移时设置并验证返回值，不必每条连接重复切换模式。

#### 实施步骤

1. 把连接级 PRAGMA 从 `MediaDbSchema` 移到连接工厂。
2. 将数据库级 WAL 初始化保留在 schema 初始化阶段。
3. 为连接工厂提供同步与异步配置方法，或使用单条轻量同步命令完成配置。
4. 明确写并发模型：Repository 写统一经过共享写门，读连接允许并发。
5. 捕获并记录 SQLite error code、extended error code、operation name 和等待耗时。
6. 对 `SQLITE_BUSY/LOCKED` 只在幂等操作上考虑有限次数退避重试；事务中间步骤不得局部重试。

#### 数据完整性增强

当前 schema 未声明主要关联表的外键。建议分两个版本实施：

1. 先启用 `foreign_keys=ON`，运行 orphan 数据审计。
2. 清理历史 orphan 后，通过表重建迁移为 `tags.mediaId`、`playlist_media.mediaId`、`playlist_media.playlistId` 添加外键和合适的 `ON DELETE CASCADE`。

不要在未审计历史数据前直接添加强制外键。

#### 验收标准

- 任意连接查询 `PRAGMA busy_timeout`、`foreign_keys`、`synchronous` 均符合约定。
- 扫描、后台回填、标签修改并发运行 30 分钟，无未处理 `SQLITE_BUSY`。
- 数据库初始化验证 WAL 实际启用；无法启用时有明确日志和降级行为。
- 迁移失败时保留原数据库，并提供可诊断的失败信息。

## 7. Phase 2：关闭可靠性与多播放器资源协调

### WP-03 设置保存生命周期闭合

#### 风险说明

当前设置有 500ms 防抖，但 `SettingsService` 不实现释放协议。修改设置后立即关闭可能在 debounce 到期前销毁进程，最后一次修改丢失。

#### 目标设计

使 `SettingsService` 实现 `IDisposable` 或 `IAsyncDisposable`，提供明确的 flush：

```csharp
Task FlushAsync(CancellationToken cancellationToken = default);
```

状态要求：

- 内存中保存最新序列化快照或设置版本号。
- 新保存会取消旧 debounce，但不丢失最新快照。
- `FlushAsync` 取消 debounce，并立即持久化最新状态。
- 释放后拒绝或忽略新的更新。
- 写盘仍使用临时文件加原子替换。

#### 实施步骤

1. 把 fire-and-forget 保存任务保存为字段。
2. 用 generation/version 区分过期保存任务。
3. 实现 `FlushAsync`，避免在持有 `_settingsSync` 时等待异步任务。
4. 在 `App.DisposeServices` 前或容器异步释放路径中等待 flush。
5. 对异常关闭仍依赖临时文件恢复；启动时处理残留 `.tmp`。

#### 验收标准

- 修改任意设置后 0 到 500ms 内关闭，重启后值不丢失。
- 连续高频修改只产生合并写入。
- 模拟写入失败时旧设置文件仍可读取，临时文件不会替换有效文件。
- 应用关闭不会因等待设置保存无限挂起，建议总超时不超过 2 秒。

### WP-04 将播放状态改为租约/引用计数模型

#### 风险说明

当前全局状态是单个 bool。多播放器并存时，一个播放器暂停或释放即可覆盖另一个仍在播放的状态；独立窗口当前没有上报状态。

#### 目标设计

`PlaybackResourceCoordinator` 改为按播放会话持有租约：

```csharp
IPlaybackActivityLease Acquire(string ownerId);

interface IPlaybackActivityLease : IDisposable
{
    void SetState(PlaybackActivityState state);
}
```

建议状态：

- `Inactive`
- `Opening`
- `Buffering`
- `Playing`

全局 `IsPlaybackActive` 由所有未释放租约聚合产生，而不是由最后一次写入决定。

#### 实施步骤

1. 主页面 `VideoPlaybackController` 构造时获取一个 owner lease。
2. 每个 `IndependentMediaWindow` 或其 controller 获取独立 lease。
3. 在播放器 `PlaybackStateChanged` 中更新对应租约。
4. 窗口关闭、播放器替换和 controller dispose 时释放租约。
5. 增加活跃 owner 数、状态分布和最长活跃时间诊断。
6. 后台 I/O 门只读取聚合状态；不要让后台任务持有播放租约。

#### 节流策略调整

当前 `EnterBackgroundIoAsync` 在进入时只检查一次播放状态，并用全局单通道 gate 串行所有后台 I/O。建议：

- 缩略图、扫描和指纹回填使用不同优先级，而非全部同等串行。
- 播放活跃时：指纹回填最低优先级，扫描并发降为 1，当前视口缩略图允许有限并发。
- 播放暂停后：恢复默认并发，但不要瞬间释放大量积压任务，可用短暂 ramp-up。

#### 验收标准

- 主窗口和两个独立窗口同时播放时，关闭任一窗口不会错误清除全局播放状态。
- 独立窗口单独播放时，扫描并发和后台回填仍按播放活跃策略运行。
- 所有窗口关闭后活跃租约数归零。
- 日志能定位未释放租约的 owner。

## 8. Phase 3：取消、缓存与日志治理

### WP-05 后台媒体 I/O 的取消边界

#### 目标

应用关闭、数据目录切换、扫描取消或请求过期后，不再长时间等待不可见的元数据和指纹工作。

#### 实施方案

1. `ReadMediaMetadataAsync` 增加 `CancellationToken` 参数。
2. WinRT `IAsyncOperation` 使用支持取消传播的等待方式；若底层操作不能真正取消，至少在每个阶段后检查 token 并丢弃结果。
3. `MediaContentFingerprint.Compute` 增加异步版本：
   - 使用 `FileStream` 异步读取。
   - 每个 chunk 前后检查 token。
   - 复用 `ArrayPool<byte>`，避免每文件分配缓冲。
4. `BackfillMediaDetailsAsync` 不要把 `OperationCanceledException` 当普通失败采样；取消应直接向外传播到任务跟踪器。
5. 每个后台操作记录 queued、started、canceled、completed 四类指标。

#### 超时策略

- 元数据读取：不建议对本地正常文件使用很短硬超时；对网络盘、脱机盘可配置较长超时或按文件跳过。
- FFmpeg 帧提取继续保留硬超时。
- 单文件失败不终止整批，取消请求必须终止整批。

#### 验收标准

- 扫描期间取消后 2 秒内停止新增数据库写入。
- 应用关闭时后台回填在约定超时内退出。
- 取消日志不再被记录成错误或失败样本。
- 大文件指纹计算期间取消能在一个读取 chunk 内响应。

### WP-06 缩略图与时间轴缓存配额

#### 目标设计

为两类磁盘缓存增加统一的 quota policy：

```csharp
record CacheQuota(
    long MaxBytes,
    int MaxFiles,
    TimeSpan MaxAge,
    long TargetBytesAfterTrim);
```

建议初始值应配置化并通过实测调整：

- 普通缩略图：最大 2 GB 或 50,000 文件，清理目标 1.5 GB。
- 时间轴帧：最大 4 GB 或 100,000 文件，清理目标 3 GB。
- 最大年龄：90 天；近期访问文件优先保留。

不要把上述数值硬编码为永远正确的产品规则，应允许随版本和用户设置调整。

#### 实施步骤

1. 抽 `DiskCacheTrimmer`，两个服务复用。
2. 缓存命中时避免每次同步更新文件时间；用内存采样或批量更新访问元数据。
3. 启动后低优先级执行一次快速配额检查，不阻塞首屏。
4. 每新增一定字节或文件数后触发增量 trim，而非每次写入全目录扫描。
5. 清理时跳过正在生成的临时文件和 inflight key。
6. 写缓存采用临时文件 + rename，避免取消或崩溃留下可被误读的半文件。
7. 清除单媒体缓存时保持缩略图、时间轴帧和内存缓存一致。

#### 缩略图请求取消改进

当前 `GetThumbnailAsync` 没有调用方 token，调用者离开视口后只能依赖全局缓存取消。建议增加 token，同时保持同 key inflight 共享：

- 单个 waiter 取消不应取消其他 waiter 仍需要的共享生成。
- 所有 waiter 都取消后才尝试取消底层生成。
- UI item recycling 后禁止把旧结果应用到新 item。

#### 验收标准

- 连续导入和浏览大库后缓存总量稳定在配额附近。
- trim 不阻塞 UI 线程，不与当前缩略图写入互相破坏。
- 应用异常关闭后不存在可被当作有效图片读取的 partial 文件。
- 清理后下一次请求能正常重新生成。

### WP-09 日志容量和可观测性治理

#### 风险说明

当前日志按天生成但不清理，采样 key 字典不淘汰；磁盘持续失败时 pending queue 也没有硬上限。

#### 目标设计

为日志引入三层边界：

1. 单文件/单日最大大小，例如 32 MB，超限后滚动分片。
2. 总保留天数和总大小，例如 14 天且不超过 512 MB。
3. 内存 pending queue 最大条目数，例如 10,000，超限丢弃低价值采样日志并记录 drop count。

#### 实施步骤

1. 日志文件名增加分片序号。
2. 启动或每日切换时后台执行 retention cleanup。
3. `LastLogTicksByKey` 改为带最后访问时间的有界缓存，定期清理过期 key。
4. 对路径和文件名进行隐私与容量控制：默认记录 media ID、扩展名和哈希化路径；需要完整路径的诊断模式单独开启。
5. 增加以下聚合计数：
   - dropped pending lines。
   - flush failures。
   - current queue length。
   - log bytes written。
6. 崩溃路径继续执行同步 flush，但设置短超时，避免崩溃处理本身死锁。

#### 验收标准

- 长期运行时日志目录不会无限增长。
- 模拟无写权限或磁盘满时，进程内存不会随日志持续增长。
- 采样 key 数量可观测且有稳定上限。
- 崩溃日志仍包含最近高价值事件和日志丢弃计数。

### WP-11 SQLite 原生依赖安全治理

#### 风险说明

当前构建报告 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 命中高严重性漏洞 `GHSA-2m69-gcr7-jv3q`。该包是传递依赖，不能只通过消除 warning 处理，必须确认实际打包的原生 SQLite 版本和上游兼容性。

#### 实施步骤

1. 使用以下命令记录直接和传递依赖来源：

   ```powershell
   dotnet list src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj package --include-transitive
   dotnet list src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj package --include-transitive --vulnerable
   ```

2. 优先升级 `Microsoft.Data.Sqlite` 到包含已修复 SQLitePCLRaw/SQLite 原生库的兼容版本。
3. 若上游尚未提供可用版本，评估显式覆盖 SQLitePCLRaw bundle/lib 包；必须验证 `Microsoft.Data.Sqlite` 的 provider 初始化和 RID 资产选择，禁止只为压制告警添加不匹配版本。
4. 检查 Debug、Release、portable publish 中最终复制的 `e_sqlite3.dll` 文件版本和哈希，确认没有旧 DLL 被工具目录或历史输出覆盖。
5. 执行数据库迁移、FTS5、WAL、并发读写和 portable 启动回归。
6. 若短期无法升级，在发布说明和风险登记中记录受影响版本、暴露条件、上游跟踪链接和下次复核日期；不要永久设置 `NoWarn=NU1903`。

#### 验收标准

- 漏洞审计不再报告该 advisory，或存在有负责人和复核日期的临时风险接受记录。
- x64 portable 包可在干净机器启动并完成数据库读写、FTS 搜索和 WAL 检查。
- 依赖升级为独立提交，可单独回滚。

## 9. Phase 4：播放管线优化

### WP-07 降低视频切换时的播放器重建成本

#### 风险说明

当前每次视频切换都会创建新的 `MediaPlayer`，旧播放器随后在低优先级 Dispatcher 回调中释放。连续切换会造成原生解码器、媒体会话和 GPU 资源短时叠加。

#### 不建议直接做的事

不要直接把实现改为永远复用一个播放器并删除现有版本保护。WinUI/MediaPlayer 对快速换源、旧事件到达和失败源清理有复杂时序，直接复用可能重新引入旧画面、错误事件污染和卡死。

#### 推荐分两步实施

##### Step A：测量并限制旧播放器积压

- 记录播放器创建、source assigned、first playing、旧播放器 dispose 的时间。
- 维护待释放播放器计数，正常情况下不得长期大于 1。
- 新切换到来时优先同步停止最旧播放器，再安排释放。
- 对快速切换增加短 debounce/coalescing，只为尚未开始播放的中间选择跳过建播放器；最终选择必须立即生效。

##### Step B：受控播放器复用实验

在功能开关后实现两种策略：

- `ReplacePlayerPerSource`：当前稳定策略。
- `ReusePlayer`：复用同一 player，仅替换 source。

通过运行指标比较：

- 选择到 source assigned。
- source assigned 到 first playing。
- 视频 surface reveal。
- 峰值 private bytes、GPU dedicated/shared memory。
- `MediaFailed`、黑屏、旧帧闪现次数。

只有复用策略在目标格式和快速切换压力测试中稳定后，才考虑设为默认。

#### 播放源创建

本地文件可评估使用 `StorageFile`/stream source，而不是只使用 file URI。是否切换必须以以下实测为依据：

- 长路径和特殊字符。
- 文件被移动/删除时的错误行为。
- seek、字幕、容器格式兼容性。
- source dispose 生命周期。

#### 验收标准

- 连续切换 200 次无播放器对象持续积压。
- 旧媒体事件不会更新当前媒体 UI。
- 首帧 P95 不劣于基线，内存/GPU 峰值明显下降后才启用新策略。
- 主窗口和独立窗口使用相同的播放资源生命周期原则。

## 10. Phase 5：数据库查询和分页性能

### WP-08 实施正确的复合 keyset 分页

#### 风险说明

主列表当前使用 OFFSET。大偏移时成本随页数增长。现有 `AfterMediaId` 只追加 `m.id > $afterMediaId`，但实际排序包含：

```text
folderPath ASC,
selected sort field ASC/DESC,
id ASC
```

因此当前游标不能启用，否则会漏项或重复。

#### 目标查询契约

游标必须包含完整排序 tuple：

```csharp
record MediaPageCursor(
    string FolderPath,
    string? TextSortValue,
    long? IntegerSortValue,
    double? RealSortValue,
    string MediaId);
```

具体字段按 `MediaSortField` 选择，游标还应绑定：

- sort field。
- sort order。
- 查询条件 fingerprint/generation。
- playlist、folder、tag、search 等过滤条件版本。

#### SQL 规则示例

对 `ModifiedAt DESC`，下一页条件应等价于：

```sql
WHERE
  m.folderPath COLLATE NOCASE > $folder
  OR (
    m.folderPath = $folder COLLATE NOCASE
    AND m.modifiedAt < $modifiedAt
  )
  OR (
    m.folderPath = $folder COLLATE NOCASE
    AND m.modifiedAt = $modifiedAt
    AND m.id > $id
  )
```

不同字段、ASC/DESC、NULL 归一化规则都必须有独立构造逻辑和测试。不要通过拼接任意用户输入生成排序字段。

#### 实施步骤

1. 先为所有 sort field 建立“全量结果 vs 多页拼接结果”契约测试。
2. 新增 `MediaPageCursor`，保留 OFFSET 路径作为 fallback。
3. `MediaPageResult` 返回 `NextCursor`。
4. `MediaPageStore` 按 generation 保存 cursor，不跨查询复用。
5. 只对稳定排序启用 keyset；随机排序继续使用独立策略。
6. 对 playlist 自定义顺序单独定义 tuple，不复用普通媒体排序。
7. 用 `EXPLAIN QUERY PLAN` 检查索引；按真实查询补复合索引，避免盲目增加索引。

#### 随机播放/随机查询

`ORDER BY RANDOM()` 会扫描并随机排序整个候选集，不适合大库分页。建议：

- 播放随机逻辑由 `ShufflePlaybackController` 维护候选 ID/历史，不依赖随机分页。
- 若需要随机抽样，使用稳定 seed + 持久随机键，或分段采样方案。
- 不对 `RandomOrder=true` 返回可继续分页的普通 cursor。

#### 验收标准

- 每个排序字段和方向在 100,000 条测试数据上无重复、无遗漏。
- 分页期间插入/删除记录时行为有明确契约，至少 UI 集合边界继续按 ID 去重。
- 深页查询耗时不再随 offset 线性增长。
- 新旧路径结果一致后才删除 OFFSET fallback。

## 11. Phase 6：架构职责收口

### WP-10 将剩余编排职责从大类中拆出

#### 原则

拆分类不是按行数机械切文件，而是让每个状态机有单一 owner、明确输入输出和可取消生命周期。

#### 11.1 `LibraryShellViewModel`

建议继续迁出：

- `FolderWatcherChangeProcessor`
  - 串行消费 watcher batch。
  - 处理 rename/import/delete 合并。
  - 持有自己的 lifetime token。
- `MediaDetailsBackfillCoordinator`
  - 负责回填排队、优先级、取消和刷新通知。
- `LibraryPersistenceCoordinator`
  - 浏览状态、删除持久化清理任务和关闭 drain。

ViewModel 最终只保留可绑定状态和用户命令入口。

#### 11.2 `MediaBrowserController`

现有滚动、选择等子控制器已经出现，但主 controller 仍负责大量 Dispatcher 重试和可见性确认。建议：

- 把“滚动到媒体并确认可见”建模为可取消 operation。
- 新请求到来时取消旧 operation，而不是继续排多个低优先级回调。
- 将 List/Grid 两种视图适配抽成 `IMediaItemsViewAdapter`，减少分支复制。

#### 11.3 `MediaRepository`

在 WP-01 和 WP-08 后拆分为：

- `MediaWriteRepository`。
- `MediaQueryRepository`。
- `MediaSearchIndexRepository`。
- `MediaRelocationRepository`。

共享连接工厂和事务 helper，但避免再引入跨 repository 的隐式事务。需要原子组合时由上层 unit-of-work 方法明确编排。

#### 11.4 播放控制器

`VideoPlaybackController` 保留 source/player 生命周期和 transport command。进一步迁出：

- `PlaybackSessionTelemetry`：首帧、切换、seek 和 surface 指标。
- `MediaPlayerFactory`：便于 A/B 两种切换策略和测试替身。
- `PlaybackActivityLease` 集成逻辑。

#### 架构验收标准

- 每个后台组件都有明确的 `Dispose`/取消入口。
- 不再通过散落的 bool 同时表达生命周期、请求版本和 UI 状态。
- 新子组件可以脱离 WinUI 页面进行单元测试。
- 拆分 PR 不改变用户可见行为，并通过前述专项测试。

## 12. 可观测性指标

为判断优化是否有效，建议统一记录以下指标。日志应采样或聚合，避免每项媒体逐条刷屏。

### 数据库

- connection open/configure elapsed。
- write gate wait。
- transaction elapsed、batch size、commit/rollback。
- `SQLITE_BUSY/LOCKED` 次数。
- query elapsed、returned count、query generation、paging mode。

### 媒体库与缩略图

- scan queue wait、metadata elapsed、fingerprint elapsed。
- thumbnail memory/disk/generated hit ratio。
- thumbnail inflight count、waiter count、cancel count。
- cache total bytes/files、trim duration、trim failures。

### 播放

- selected-to-source-assigned。
- source-assigned-to-media-opened。
- source-assigned-to-first-playing。
- selected-to-surface-reveal。
- pending player disposal count。
- active playback lease count and owners。

### UI

- UI heartbeat stall 次数及持续时间。
- 列表刷新/append/scroll operation duration。
- 被 generation 或 cancellation 丢弃的过期请求数。

指标字段命名应稳定，避免把文件路径或媒体 ID拼进 sample key 造成无限基数。

## 13. 回归验证矩阵

### 13.1 数据库

- 新库初始化、旧库迁移、迁移中断恢复。
- 10、10,000、100,000 条媒体导入。
- 导入中取消、应用关闭、磁盘满、只读目录。
- 标签新增/删除与 FTS 搜索即时一致。
- 文件移动、重命名、删除后的 relocation 和 playlist 关系。
- 扫描、标签修改、删除、后台回填并发。

### 13.2 异步与关闭

- 扫描时关闭应用。
- 指纹计算时关闭应用。
- 缩略图批量生成时清缓存和关闭应用。
- 设置修改后立即关闭。
- 页面重复 Loaded/Unloaded，所有后台工作和事件订阅不累积。

### 13.3 播放

- 主窗口连续快速切换 200 次。
- 主窗口与多个独立窗口同时播放、暂停和关闭。
- 缺失文件、不支持编码、损坏媒体、超长视频。
- 播放时启动扫描、滚动大库、生成 hover preview 和时间轴帧。
- seek、暂停、恢复、播放结束和自动切换。

### 13.4 缓存与日志

- 缓存超过字节和文件数配额。
- 清理期间仍有读取和写入。
- 日志目录无权限、磁盘满、跨日运行。
- 日志 retention 不删除当前会话或崩溃工件。

## 14. 性能目标

以下目标用于比较优化前后，不代表所有机器的绝对 SLA。基线数据应先在同一台机器、同一媒体 fixture 上采集。

| 指标 | 目标 |
|---|---|
| 首屏数据库查询 P95 | 不高于当前基线，建议小于 200ms |
| 深页查询 P95 | 使用 keyset 后基本不随页数增长 |
| 设置关闭 flush | 通常小于 200ms，硬超时小于 2s |
| 扫描取消停止写入 | 小于 2s |
| 播放期间 UI heartbeat stall | 不出现持续超过 500ms 的后台 I/O 导致停顿 |
| 快速视频切换待释放播放器 | 稳态 0，瞬时不超过 1 |
| 缩略图/时间轴缓存 | 保持在配置配额附近 |
| 日志 pending queue | 正常稳态接近 0，异常时有硬上限 |

## 15. PR 与提交拆分建议

每项保持可独立回滚，不要把架构拆分和数据库语义修改混在一起。

1. `test: add database transaction and paging contract coverage`
2. `fix: bind repository commands to active sqlite transactions`
3. `fix: configure every sqlite connection consistently`
4. `fix: flush debounced settings during shutdown`
5. `fix: track playback activity with per-window leases`
6. `fix: propagate cancellation through media detail backfill`
7. `feat: enforce thumbnail and timeline cache quotas`
8. `feat: bound trace log retention and in-memory queues`
9. `fix: update vulnerable sqlite native dependencies`
10. `perf: limit media player churn during rapid switching`
11. `perf: add composite keyset pagination behind a feature flag`
12. `refactor: isolate watcher and persistence coordinators`
13. `refactor: split media query and write repositories`

每个 PR 应包含：

- 所解决的工作包 ID。
- 行为变化和失败模式。
- 自动化测试与手工验证命令。
- 性能前后数据，若属于性能优化。
- 回滚方式或功能开关，若改动播放/分页核心路径。

## 16. 发布与回滚策略

### 数据库改动

- schema 迁移前备份数据库或使用 SQLite backup API。
- 迁移在单事务完成，失败不修改 `user_version`。
- 新查询路径不能要求不可逆 schema 后才可启动旧版本，除非发布包明确禁止降级。

### 播放与分页改动

- 使用设置项、内部 feature flag 或环境变量保留旧策略。
- 新策略出现黑屏、错页、重复或崩溃时，可不迁移数据直接切回旧策略。

### 缓存与日志改动

- 缓存属于可再生数据，可安全清理；清理器失败不得影响应用启动。
- retention 只处理明确命名规则内的文件，禁止递归删除不受控目录。

## 17. 总完成定义

本专项计划完成时，应满足：

- 所有显式 SQLite 事务内命令绑定正确，异常/取消测试证明原子性。
- 每条数据库连接执行统一配置，并有 busy/locked 诊断。
- 设置在正常关闭时保证最后一次修改落盘。
- 主窗口和所有独立窗口使用引用计数式播放活动租约。
- 后台元数据、指纹和缩略图工作具备明确取消边界。
- 缩略图、时间轴和日志目录都有容量/保留治理。
- SQLite 原生依赖漏洞已修复，或有明确的临时风险接受和复核计划。
- 播放器快速切换不存在持续原生资源积压。
- 大库分页采用经过契约测试的复合 keyset，或保留明确的 OFFSET fallback。
- 核心指标可用于比较修复前后，而不是只依赖主观体验。
- `dotnet build JvJvMediaManager.sln` 通过，专项自动化和相关运行清单全部通过。
