# Architecture status

本文档跟踪架构优化计划与当前实现的差异，避免依据过期审查结论继续安排工作。

## 已落地

- SQLite 连接统一配置 busy timeout、foreign keys、WAL 和 synchronous 策略。
- SQLite 数据库迁移现在使用显式 `user_version`，并记录 `schema_migrations` 历史；遇到未来版本会 fail closed。
- 媒体查询已具备经过 smoke contract 验证的复合 keyset 分页；全库流式读取使用 keyset continuation。
- Upsert 和批量标签写入在事务内批量刷新 FTS 行，避免每个媒体逐条重建索引。
- 播放资源使用 lease 模型，后台媒体 I/O 会根据播放状态限速。
- FFmpeg 进程统一通过 `ExternalProcessRunner` 排空标准输出和错误输出。
- 设置保存、缓存配额、日志容量和播放器租约已有专项 smoke tests。
- 发布脚本和 solution 配置已经明确为 `win-x64` / `x64-only`。
- Release 构建默认关闭 Clip Editor 的 WebView2 DevTools。
- 启动维护具有单进程串行闸门，并在数据库释放时等待或取消。

## 部分落地

- DI 已用于应用级基础服务和主页面 ViewModel，但主页面控制器、独立窗口和停靠播放器仍有手工组装；窗口 scope 和播放会话 scope 尚未完全收口。
- `IUiThreadDispatcher` 已存在并在部分 ViewModel 使用，控制器仍直接操作 `DispatcherQueue`。
- 文件监控已有防抖、合并和 overflow 重扫；处理失败会重新入队，但尚无持久化重试队列。
- 媒体身份已有内容指纹和 relocation 机制，但路径哈希仍是初始 ID，canonical path 和内部 opaque ID 尚未完全分离。
- FTS 失败时有 LIKE fallback，但 dirty/generation 状态和后台重建队列仍待补齐。

## 后续工作

1. 为主窗口、独立窗口、停靠会话建立明确的 DI scope 和 `PlayerSession` 抽象。
2. 将扫描、元数据、指纹和缩略图任务统一到可取消、有界的后台调度器。
3. 将 watcher reconciliation 从内存重试升级为可恢复的目录级任务。
4. 为扫描、watcher、迁移、FTS 一致性补充集成 smoke tests。
5. 建立 Windows CI：x64 restore/build、portable publish、漏洞审计、smoke tests 和干净机器启动验证。
6. 在真实大库上用指标验证 keyset、批量 FTS、缓存配额和播放器切换策略，再决定是否拆分程序集或复用播放器。

## 约束

- 在数据契约和生命周期边界稳定前，不进行 WinUI 技术栈迁移或大规模程序集拆分。
- 不引入服务端数据库、微服务或重型 MVVM 框架来解决当前的边界问题。
- 每个结构性改动保持独立提交，并通过 `dotnet build JvJvMediaManager.sln` 和相关 smoke suite。
