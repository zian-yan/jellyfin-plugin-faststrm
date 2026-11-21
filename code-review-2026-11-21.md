# FastSTRM 代码审查报告

> **修复状态（2026-11-21 追记）**：本报告的条目已全部处理，改动清单见 `CHANGELOG.md` 的「未发布」段。
> 实施过程中订正了报告里的两处判断：
> - **C1** 属 3.5.x 为修「退出时误标已观看」而做的有意取舍，不是普通 bug。改动会回退那次修复，
>   因此只修正了日志措辞并补注释，持久化行为保持不变。
> - **C2** 的「网络盘抖动放大成远端请求雪崩」不成立：`ResolveAsync` 读不到 URL 时会直接返回，
>   不会发出任何 HTTP 请求。真实代价只是重复的 `stat` 与重复告警，已由 P1 的长度缓存消掉。

- 审查对象：`Jellyfin.Plugin.FastSTRM` v3.7.2（net10.0，约 3800 行生产代码）
- 审查范围：`Probing/`、`Playback/`、`Api/`、`Common/`、`Configuration/`、CI 与前端配置页
- 基线：`dotnet build -c Release` 通过，0 警告 0 错误

严重度定义：**高** = 可被外部利用或造成明显故障；**中** = 特定配置/规模下稳定复现的问题；**低** = 健壮性或可维护性。

---

## 一、安全隐患

### S1 [高] SSRF：默认放行内网，且代理/重定向场景可绕过

`.strm` 文件内容是完全外部可控的（用户上传、SMB/NFS 共享库、他人投喂的媒体库）。插件会按文件里的 URL 主动发 `HEAD` 与 `Range: 0-1` 的 `GET`。

**风险 1：默认允许内网**

`Configuration/PluginConfiguration.cs:31`

```csharp
public const bool DefaultAllowPrivateNetworkTargets = true;
```

开箱即用状态下，`127.0.0.1`、`10.0.0.0/8`、`192.168.0.0/16` 全部可打。云主机上 `169.254.169.254`（实例元数据）会被 `StreamTargetPolicy.IsAllowed` 的 `169.254` 分支拦掉，但同网段的内网管理面（Redis 6379、ES 9200、Docker API 2375、Jenkins 8080）没有被拦，构成典型的盲 SSRF。

**风险 2：解析失败时 fail-open**

`Probing/RemoteSizeService.cs:401-416`

```csharp
catch (Exception)
{
    return true;   // DNS 失败 → 视为合法
}
```

在"走代理"分支下，这是唯一的目标校验点，抛异常即放行。

**风险 3：重定向不逐跳校验（代理场景下真绕过）**

`Probing/RemoteSizeService.cs:619-627` 配置了 `AllowAutoRedirect = true, MaxAutomaticRedirections = 3`。

- 直连：`ConnectCheckedAsync` 是 `SocketsHttpHandler.ConnectCallback`，每一跳都会按解析到的 IP 校验 → 安全。
- 走代理：`ConnectCallback` 拿到的是**代理地址**，不是目标地址。真正的目标校验只在 `AreResolvedTargetsAllowedAsync` 里对**首跳 host** 做过一次。因此 `http://公网域名/302 → http://10.0.0.5:2375/` 经代理可以直达内网。

**风险 4：`Any` 语义过宽**

`Probing/RemoteSizeService.cs:409-410`

```csharp
return addresses.Length == 0
       || addresses.Any(address => StreamTargetPolicy.IsAllowed(address, allowPrivateTargets));
```

多 A 记录中只要有一条合法即放行，而代理侧会自行挑选解析结果，可能挑到那条非法的。

**建议修复**

1. `DefaultAllowPrivateNetworkTargets` 改为 `false`（已有开关，改默认值不影响现有用户显式配置）。
2. 关闭自动重定向，改为手动循环 3 次，每一跳都重新走 `EvaluateLiteralHost` + `AreResolvedTargetsAllowedAsync`。
3. `AreResolvedTargetsAllowedAsync` 的 `catch` 改为 `return false`（fail-closed）。
4. `Any(...)` 改为 `All(...)`。
5. 若必须支持代理，增加"目标 host 白名单"配置项，代理模式下强制走白名单。

### S2 [中] 盲 SSRF 附带响应 oracle

`LookupNotes` 会把探测结果拼进日志与返回值，例如：

- `Probing/RemoteSizeService.cs:434` — `HEAD returned HTTP 404 Not Found`
- `Probing/RemoteSizeService.cs:440` — `answered as text/html, which is not a media file`
- `Probing/RemoteSizeService.cs:503` — `HTTP 200 without a usable Content-Range (...)`

配合 S1，攻击者可对内网做端口与服务的指纹识别（端口开/关、状态码、Content-Type）。日志仅管理员可见，但 `/FastStrm/*` 全部是 `RequiresElevation`，能读日志的人多半也能调接口，实际暴露面有限。建议对 `Reason` 做脱敏分级：只在 `Debug` 输出细节，`Warning` 只输出分类码。

### S3 [低] 完整 URL（含签名 token）作为缓存 key 长期驻留内存

`Probing/RemoteSizeService.cs:59`、`:777-781`

```csharp
private readonly ConcurrentDictionary<string, CachedSize> _urlSizes = new(StringComparer.Ordinal);
```

缓存 key 是含 `?token=...` 的完整 URL，TTL 6 小时，最多 8192 条。签名 URL 的凭证会在进程内存中长期驻留，并被 `PruneIfNeeded` 溢出时的 `cache.Clear()` 一次性丢弃（顺带引发缓存雪崩，见 P12）。建议 key 用 `scheme://authority/path` 的哈希，或至少去掉 query。

### S4 [低] 供应链：CI 中 action 未 pin 到 commit SHA

`.github/workflows/release.yml`

- `permissions: contents: write`（已是最小必要，这点是好的）
- 但 `actions/checkout@v4`、`actions/setup-dotnet@v4`、`softprops/action-gh-release@v2` 都是浮动 tag。持有 `GITHUB_TOKEN`（可写仓库）的 job 使用浮动 tag，一旦上游 tag 被移动即可被投毒。
- 同一 job 末尾执行 `git checkout -B main origin/main && git push origin main`，把标签发布流程反向写入了主干，建议改为 PR 或至少加保护分支校验。

建议：action 固定到 40 位 SHA；发布 job 与 manifest 更新 job 拆分，后者单独收窄权限。

### S5 [低] 已确认安全的部分（无需改）

- 控制器 `[Authorize(Policy = "RequiresElevation")]` 全覆盖，无匿名端点（`Api/FastStrmController.cs:16`）。
- 前端全程 `textContent` / `createElement`，未出现 `innerHTML` 拼接用户数据，无 XSS（`Configuration/configPage.js`）。
- `LogSafe.Sanitize` 清除控制字符并截断，无日志注入（`Common/LogSafe.cs`）。
- `UrlForLog` 剥离 query，不会把 token 写进日志（`Probing/RemoteSizeService.cs:805-808`）。
- `pluginId` 前端/后端/manifest 三处一致（`123456789abc`），无配置读写错位。
- SQL 层无直接拼接（全部走 Jellyfin 的 `ILibraryManager` / `IMediaStreamRepository`）。

---

## 二、性能问题

### P1 [高] 列表页逐条同步磁盘 I/O

`Playback/MediaSourceInterceptor.cs:412-423`

```csharp
public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
{
    var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
    if (StrmItem.IsStrm(item))
    {
        ApplyShortcutTarget(item, sources);          // → 打开 .strm 读前 10 行
        _remoteSize.ApplySizeToSources(item, sources); // → new FileInfo(path).Length
    }
    return sources;
}
```

`GetStaticMediaSources` 在条目 DTO 映射时**逐条**调用，也就是浏览媒体库列表、`/Users/{id}/Items`、搜索、首页推荐都会命中。每个 `.strm` 条目固定产生：

- 1 次 `FileStream` 打开 + `ReadLine`（`Common/StrmItem.cs:63-64`）
- 1 次 `FileInfo.Length`（`Probing/RemoteSizeService.cs:122`，经 `IsRemoteSizeMissing`）

100 条一页 = 200+ 次 syscall。库放在 SMB/NFS 上时单次可达数毫秒，列表页出现秒级卡顿，且这些是**同步阻塞 I/O**，会占满线程池。

**修复**：按 `(path, length, mtime)` 缓存解析出的 URL（URL 极少变化）；`IsRemoteSizeMissing` 的 `FileInfo` 结果加短 TTL（如 30s）缓存。可直接复用 `ProbeService` 已有的 `_strmItems` 快照思路。

### P2 [高] 播放取源关键路径上叠加多次 stat + 最长 2s 远端查询 + 一次 DB 写

`Playback/MediaSourceInterceptor.cs:99-164` 的调用链：

```
IsPlaybackReady(item)                    → FileInfo（RemoteSizeService.cs:305 → :122）
EnsureRemoteSizeAsync(item, ct)          → 最长 2s 网络等待（:37 PlaybackSizeLookupTimeout）
   └─ PersistAsync → UpdateItemAsync     → DB 写（:707-709）
ApplySizeToSources(item, sources)        → 又一次 FileInfo
LogPlaybackCall → ReadStrmLength(item)   → 又一次 FileInfo
```

首次播放一个未探测条目，起播前最多 **+2s**，而且 `UpdateItemAsync` 是同步等待的数据库写。用户感知就是"点了播放，转圈两秒"。

**修复**：
- `EnsureRemoteSizeAsync` 只写内存缓存并返回，落库交给已有的后台 `ProbeService` 流程；
- `PlaybackSizeLookupTimeout` 从 2s 降到 800ms 左右，超时就先放行播放；
- 三次 `FileInfo` 合并成一次。

### P3 [中] `GetSummary` 缓存未命中时同步阻塞 HTTP 请求

`Probing/ProbeService.cs:459-474`

```csharp
var cached = _summaryCache;
if (cached is not null && !forceRefresh) { /* 陈旧则后台刷新 */ return cached.ToTuple(); }
return ComputeSummary().ToTuple();   // ← 缓存为 null 时在请求线程内同步算
```

`ComputeSummary`（:476-493）做两件重活：一次无 `Limit` 的全库 `GetItemList`，以及逐条 `items.Count(RemoteSizeService.IsRemoteSizeMissing)`（= N 次 `FileInfo`）。10 万条目约 10 万次 stat，并且整个过程在 `_summaryLock` 内持锁做 I/O。

启动后第一次打开配置页、或前端带 `?refresh=true` 调用，会直接撞上这条路径。

**修复**：`GetSummary` 永不阻塞——首帧返回 `(0,0,0,0)` 并触发后台计算；`SizeMissing` 改为增量维护或按需触发。

### P4 [中] `ConcurrencyGate` 的 permit 累积导致惊群 + 自旋

`Common/ConcurrencyGate.cs:20-50`

```csharp
public void Release()
{
    Interlocked.Decrement(ref _active);
    _released.Release();   // 无条件 +1，即使没有等待者
}
```

`_released` 是 `SemaphoreSlim(0)`，被当作自动重置事件用，但每次 `Release` 都无条件加计数。没有等待者时 permit 会累积（例如限流 1、串行跑 1000 次 → 计数 1000）。之后并发进入时，大量等待者被瞬间放行、进入 `while(true)` 重新判定、再次 `WaitAsync`，形成惊群；同时 `current < limit` 分支用的是 `Task.Yield()` 自旋（:41），高并发下纯烧 CPU。

现有 `ConcurrencyGateTests` 只验证了"不超限"的语义，没有验证唤醒次数，所以测试是绿的。

**修复**：直接用 `SemaphoreSlim` 的标准用法（动态限额变化时重建实例），或改用 `Channel<byte>` 做令牌桶。另外 `SemaphoreSlim` 未 dispose，`ConcurrencyGate` 也没实现 `IDisposable`。

### P5 [中] 全库实体常驻内存且缓存不随变更失效

`Probing/ProbeService.cs:1164-1188`

```csharp
var items = _libraryManager.GetItemList(new InternalItemsQuery {
    MediaTypes = new[] { MediaType.Video },
    Recursive = true          // 无 Limit
}).Where(StrmItem.IsStrm).ToList();
_strmItems = items;           // 挂 5 分钟
```

- 无 `Limit`，10 万 `.strm` 会把全部 `BaseItem` 实例钉在内存里；
- 没有任何 `ItemRemoved` / `ItemUpdated` 订阅来清理 `_strmItems`，删除的条目在 5 分钟内仍会被探测流程执行 `UpdateItemAsync`（对已删除 ID 写库）。

**修复**：探测时按页取（`Limit`/`StartIndex`）流式处理，不要一次 `ToList`；或订阅 `ItemRemoved` 主动置空缓存。

### P6 [中] 每个播放请求写一条 Information 级日志

`Playback/MediaSourceInterceptor.cs:183-196`

```csharp
_logger.LogInformation(
    "FastSTRM: 取源 #{Call} {Name} | 距上次 {Gap}ms | 分支 {Branch} | ... 12 个字段",
    ...);
```

Jellyfin 默认日志级别是 Information。取源会被播放、进度上报、scrobble 反复触发，日志文件体积与磁盘 I/O 随之线性增长。`LogAccelerationSucceeded`（:258）同样是 Information。

**建议**：降到 `LogDebug`；或保留 Information 但按 item+5 秒采样（`ShouldLogAcceleration` 已有这套限流，可复用）。

### P7 [低] 探测的外层并发与内层限流重复

`Probing/ProbeService.cs:992-1004`

```csharp
var options = new ParallelOptions { MaxDegreeOfParallelism = PluginConfiguration.MaxConcurrentProbesLimit }; // 16
await Parallel.ForEachAsync(items, options, async (item, token) => {
    using var slot = await AcquireProbeSlotAsync(token);   // 再按 1~16 限流
```

先开 16 个任务，再去抢 1~16 个槽位，多出来的任务全部堵在 gate 里（叠加 P4 的自旋）。直接用 `config.GetMaxConcurrentProbes()` 作为 `MaxDegreeOfParallelism` 即可，去掉内层 gate。

### P8 [低] `_strmItems` 与 `_strmItemsUtcTicks` 的发布顺序无保证

`Probing/ProbeService.cs:1185-1186`

```csharp
_strmItems = items;
Volatile.Write(ref _strmItemsUtcTicks, DateTime.UtcNow.Ticks);
```

`_strmItems` 是 `volatile` 引用，但两个字段是分别发布的，读端可能看到"新 ticks + 旧 list"，从而把过期列表当作新鲜数据返回。应像 `_summaryCache` 那样打包成单个不可变快照对象整体替换。

### P9 [低] `MarkerSet` 清理与并发写入竞争导致计数漂移

`Common/MarkerSet.cs:18-33`：`_keys.Clear()` 与其它线程的 `Interlocked.Increment` 并发时 `_count` 会漂移，可能提前触发清理（导致重复告警）或延后（多占内存）。只影响日志限流，可接受，但值得加注释说明。

### P10 [低] `ProbeSupport` 缓存无失效键

`Common/StrmItem.cs:35-49`：按 `path` 缓存 `IsProbeUnsupported` 结果，没有 mtime 参与 key，`.strm` 内容从 `.iso` 换成 `.mkv` 后进程生命周期内一直返回旧判断；达到 8192 条时 `ProbeSupport.Clear()` 整体清空，后续请求集中回到"打开文件"路径。

### P11 [低] `RemoteSizeService.Dispose()` 是空实现

`Probing/RemoteSizeService.cs:810-812`。注入自定义 `handler` 的分支（测试用）创建的 `HttpClient` 从不释放（:75-78）。静态 `SharedHttpClient` 确实不该释放，但注入分支应 `_http.Dispose()`，并加上 `_disposed` 保护。

### P12 [低] 缓存溢出用 `Clear()` 造成雪崩

`Probing/RemoteSizeService.cs:783-803`：`_itemSizes` / `_urlSizes` 达到 8192 且没有过期项时直接 `cache.Clear()`。清空瞬间所有条目变成"未解析"，紧接着的全量探测会把远端 HEAD 请求一次性打满。建议按最旧时间淘汰（`ProbeService.PrunePlaybackProbeAttempts` 里已有类似实现可复用）。

---

## 三、顺带发现的正确性问题

### C1 [中] 无时长的 `.strm` 播放进度完全不落库

`Playback/PlaybackStateInterceptor.cs:88-129`

```csharp
data.PlaybackPositionTicks = reportedPositionTicks.Value;
...
return false;   // ← 不调用 _inner.UpdatePlayState
```

`data` 是传进来的 `UserItemData`，改了字段却返回 `false` 跳过入库，等于这次进度更新被丢弃。日志写的是"Preserving playback position"，但实际效果是**续播点与已看状态都不会保存**，直到有一次探测成功。这可能是有意为之（避免把"strm 文件长度"当进度基准），但和日志文案不一致，且用户可感知（换设备续播失效）。建议明确取舍并在 UI 提示，或改为写入一个"仅位置、不带时长"的记录。

### C2 [中] 网络盘抖动会放大成全量重探测

`Probing/RemoteSizeService.cs:115-128`：`IsRemoteSizeMissing` 里 `new FileInfo(path).Length` 抛异常就 `return true`（判定为"缺大小"）。SMB 短暂断连时，全库条目会同时被判定为需要补大小，进而触发大量远端 HEAD 请求 + DB 写，把一次网络抖动放大成一次雪崩。

建议区分"文件确实很短"与"读不到文件"：读不到时返回"未知"而不是"缺失"，并跳过该条目的补全。

### C3 [低] `TryRangedGetAsync` 采信无 Range 支持的 200 响应

`Probing/RemoteSizeService.cs:497-501`：服务器忽略 `Range` 返回 200 时直接采信 `Content-Length`。若对方返回的是 HTML 错误页但 `Content-Type` 不规范（不是 `text/html`），会把页面字节数当成视频大小写库。建议对 `200` 分支增加 `Content-Type` 为 `text/*` 或 `application/json` 的排除。

### C4 [低] `#nullable disable` 全文件关闭可空检查

`Playback/MediaSourceInterceptor.cs:1`：这是项目里唯一关闭 nullable 的文件，而其中 `LogPlaybackCall` 已经在用 `sources?.Count` 防御，说明确实存在"实际可能为 null 但签名非 null"的情况（`_inner` 的返回值）。建议改回 `enable` 并显式标注 `?`。

### C5 [低] `.strm` 首行解析逻辑重复两份

`Probing/RemoteSizeService.cs:727-769`（`ReadRemoteUrl`）与 `Common/StrmItem.cs:52-95`（`TryReadRemoteUrl`）几乎逐行相同，只是前者额外返回文件长度。后续改解析规则（比如支持多行 fallback）容易只改一处。建议合并成 `StrmItem.TryReadRemoteUrl(path, out url, out length)`。

---

## 四、建议的修复顺序

| 优先级 | 项 | 理由 |
|---|---|---|
| 1 | S1 关闭重定向自动跟随 / fail-closed / 默认不放行内网 | 唯一的可利用安全问题 |
| 2 | P1 + P2 缓存 `.strm` 解析结果、缩短播放路径等待 | 用户可直接感知，改动局部 |
| 3 | P3 `GetSummary` 异步化 | 大库上会导致配置页 500 / 超时 |
| 4 | P4 `ConcurrencyGate` 换实现 | 高并发播放时的 CPU 空转 |
| 5 | C1 确认播放进度策略 | 行为与文案不符，需产品决策 |
| 6 | P5 / P6 / P9 / P10 / P12 缓存与日志治理 | 规模化后的稳定性 |
| 7 | S2 / S3 / S4 脱敏与 CI 加固 | 长期加固 |

---

## 五、做得好的地方

- 并发状态管理普遍用了 `Interlocked` / `ConcurrentDictionary` / `volatile`，边界看得很细（`_probeGeneration` 防陈旧任务收尾、`TryRemove(KeyValuePair)` 防误删新任务）。
- 所有字典类缓存都有容量上限，没有明显的无界增长。
- 取消链路（`_shutdownCts` → 播放探测 CTS → 各 `Task.Delay`）串得完整，`OperationCanceledException` 与真实异常区分处理。
- `IPluginServiceRegistrator` 装饰器模式用得规范，`ownsInner` 的所有权语义正确。
- 单元测试覆盖面不错（369 + 639 行的 RemoteSize 测试），`InternalsVisibleTo` 的做法比把 internals 公开出去干净。
