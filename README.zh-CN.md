<p align="center">
  <img src="images/faststrm-banner.png" alt="FastSTRM" width="520">
</p>

<h1 align="center">jellyfin-plugin-faststrm</h1>

<p align="center">让 Jellyfin 的 <code>.strm</code> 媒体跳过远程 <code>ffprobe</code>，点击即播。</p>

<p align="center">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/badge/Release-v3.5.0-blue" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.0%2B-purple" alt="Jellyfin 12.0+">
  <img src="https://img.shields.io/badge/.NET-10.0-blueviolet" alt=".NET 10.0">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/github/downloads/zian-yan/jellyfin-plugin-faststrm/total?label=Downloads&color=success" alt="Downloads"></a>
</p>

<p align="center">
  <b>简体中文</b> · <a href="README.md">English</a>
</p>

---

## 简介

FastSTRM 以装饰器方式包裹 Jellyfin 的 `IMediaSourceManager` 与 `IUserDataManager`，在**播放取源**这一步决定是否发起远程 `ffprobe`：

- **有流 `.strm`**：直接使用库中已有的流信息，永不探测，立即起播。
- **无流 `.strm`**：默认也不探测，立即起播；是否在起播后补探测由管理员决定。
- **非 `.strm`**：100% 透传原生管线，零副作用。

插件同时提供后台 `ProbeService`，通过**全库预探测**、**入库自动探测**、**起播后补探测**把「无流」转成「有流」，在秒开与完整媒体信息之间取得平衡。

---

## 项目结构

源码按模块分目录，**目录名与命名空间一一对应**（`Playback/` → `FastSTRM.Playback`）：

```
jellyfin-plugin-faststrm/
├── Jellyfin.Plugin.FastSTRM.sln          解决方案（插件 + 测试）
├── Jellyfin.Plugin.FastSTRM/
│   ├── Jellyfin.Plugin.FastSTRM.csproj   插件项目（程序集名仍为 `FastSTRM`）
│   ├── Plugin.cs                     插件入口（BasePlugin + 配置页注册），命名空间 FastSTRM
│   ├── PluginServiceRegistrator.cs   DI 组合根：装饰器与服务注册
│   ├── Api/                          管理接口（/FastStrm/*）
│   ├── Configuration/                配置契约（XML 持久化）与配置页静态资源
│   ├── Playback/                     播放链路：策略解析 + 两个装饰器
│   ├── Probing/                      后台探测服务（全库预探测 / 入库探测 / 起播补探测）
│   └── Common/                       跨模块共享工具（日志脱敏）
├── Jellyfin.Plugin.FastSTRM.Test/        xUnit 测试（GUID / 版本一致性守卫）
├── build.yaml · meta.json · manifest.json
└── images/ · scripts/ · wiki/
```

> 配置页 `configPage.html` / `configPage.js` 以**嵌入资源**打包，资源名由「根命名空间 + 目录路径」决定。
> 移动 `Configuration/` 目录或改变 `Plugin` 所在命名空间时，必须同步 `Plugin.GetPages()` 里的 `EmbeddedResourcePath`。

---

## 核心概念：有流与无流

插件不根据文件本身判断，而是根据**数据库里是否已有该条目的流信息**分流。判定依据是 `RunTimeTicks`：

| 类型 | 判定条件 | 库中状态 |
| :--- | :--- | :--- |
| **有流 `.strm`** | `RunTimeTicks > 0` | 已探测过，时长、码率、音视频与字幕轨（`MediaStreams`）均已入库 |
| **无流 `.strm`** | `RunTimeTicks` 为空或 `0` | 新入库未扫描、预探测未覆盖，或上次探测失败 / 超时 |

> 时长未知的极端条目（如部分直播型 `.strm`）即使探测成功也会被判为无流，属预期行为。

---

## 播放决策流程

```
PlaybackInfo 请求
    │
    ├── 非 .strm ─────────────────────────────────▶ 原生透传
    │
    └── .strm
         ├── 有流 ──▶ allowMediaProbe = false ──▶ 秒开（无条件，不受任何配置影响）
         │
         └── 无流
              ├── 跳过探测 = 开（默认）
              │     ├── 起播后策略 = 不补探测（默认）──▶ 秒开，不再请求流地址
              │     └── 起播后策略 = 后台补探测 ──────▶ 秒开 + N 秒后补探测
              └── 跳过探测 = 关 ────────────────────▶ 官方 ffprobe（首次等待，成功后转为有流）
```

分流发生在 `MediaSourceInterceptor.GetPlaybackMediaSources`；`allowMediaProbe = false` 即阻止 Jellyfin 对远程地址发起探测，客户端拿到媒体源后直接直连播放。

---

## 播放模式一览

| 模式 | 远程探测 | 起播 | 时长 / 续播 / 轨道 | 依赖配置 |
| :--- | :--- | :--- | :--- | :--- |
| **有流** | 从不 | 毫秒级 | 完整、原生逻辑 | 无 |
| **无流 · 不补探测** | 从不 | 毫秒级 | 缺时长；保留位置，不误标已看完 | 跳过探测 **开** |
| **无流 · 后台补探测** | 起播后 1 次 | 毫秒级 | 补齐后完整 | 跳过探测 **开** + 后台补探测 |
| **无流 · 官方兜底** | 播放前 | 数秒～超时 | 首次播放后完整 | 跳过探测 **关** |

**默认组合**是「无流 · 不补探测」，适合绝大多数部署；需要进度条、续播或轨道切换时，开启后台补探测即可。

各模式的完整触发条件、使用差异、适用场景、对比矩阵与选型建议见 Wiki：
[**播放模式：有流与无流**](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/Playback-Modes)

---

## 按用户播放策略

上面的配置是**全局策略**，也就是所有用户的基线。同一个部署里往往需要区别对待：一部分用户走「跳过探测 + 不补探测」的极速路径，另一部分（TV 端、需要完整轨道与进度的用户）只关掉「跳过探测」回退官方 ffprobe。
在 **控制台 → 插件 → FastSTRM → 按用户播放策略** 中可以为单个用户覆盖：

| 模式 | 跳过探测 | 后台补探测 |
| :--- | :---: | :---: |
| **继承全局**（默认，等于未配置） | 取全局 | 取全局 |
| **跳过探测 · 不补探测** | 是 | 否 |
| **跳过探测 · 后台补探测** | 是 | 是（可按用户单独设延迟） |
| **不跳过探测（官方 ffprobe）** | 否 | 否 |

- **优先级**：用户级 > 全局 > 内置默认值。未单独配置的用户继承全局策略。
- **默认值来源**：全局策略的默认值只有一处来源（`PluginConfiguration.Default*` 常量）。新安装、旧配置文件缺字段、配置对象不可用时都归到同一套值：跳过探测 **开**、后台补探测 **关**、延迟 **5s**、停止取消 **开**。
- **回退**：用户级模式非法、为空或重复配置时回退全局策略，只记一条 Warning，**不会阻断播放**；坏配置只影响它自己的用户。
- **生效时机**：每次 PlaybackInfo 请求时现取策略，保存后**下一次播放**即生效，无需重启，也不影响正在播放的会话；播放过程中的进度上报复用取源时的这份策略，同一次播放内不会改判，播放停止后的进度补发也不会再发起补探测。
- **有流条目**（`RunTimeTicks > 0`）不受任何策略影响，依然无条件秒开。

细节、回退矩阵与接口示例见 Wiki：[**按用户播放策略**](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/User-Playback-Policy)

---

## 远端文件大小

Jellyfin 探测 `.strm` 时，探测对象是本地那个**文本文件**本身，所以写进条目的「文件大小」只是这个几百字节的 `.strm` 文件大小，媒体信息页显示的也是它。远端真实大小 ffprobe 也给不出来（HTTP 流通常返回 `size=N/A`）。

开启 **控制台 → 插件 → FastSTRM → 补全远端文件大小**（默认开启）后：

1. 插件读出 `.strm` 里的流地址，先发一个 `HEAD` 请求取 `Content-Length`；
2. 源站不支持 `HEAD`（405/501）或没给长度时，退回 `Range: bytes=0-0` 的 `GET`，从 `Content-Range` 里取总长度；
3. 拿到的大小写回条目（只覆盖 `< 1 MiB` 的记录，绝不覆盖已有的真实大小）。

补全时机：

| 场景 | 说明 |
| :--- | :--- |
| 增量 / 全量 / 入库自动 / 起播补探测 | 每次探测结束后顺带写入，不再额外请求内容 |
| 浏览条目详情、开始播放 | 命中缓存直接替换显示，未命中则后台补一次（不阻塞响应） |
| 手动补全 | 控制台选择 **仅补全文件大小** 启动，或 `POST /FastStrm/Probe/Start?mode=SizeOnly`（完全不走 ffprobe） |

控制台统计新增 **缺少文件大小**，显示仍是 `.strm` 文件大小的条目数；请求超时默认 10 秒（1–60）。

> 只做一次轻量请求，不下载内容；解析失败（超时、鉴权、源站不给长度）会保留原值，并在 10 分钟后才重试同一部影片。

---

## 安装

**方式一：下载 Release**

1. 下载 [最新版本](https://github.com/zian-yan/jellyfin-plugin-faststrm/releases) 的 `FastSTRM.dll`。
2. 放入 Jellyfin 插件目录下的 `FastSTRM` 文件夹：

| 运行环境 | 插件路径 |
| :--- | :--- |
| Docker / NAS | `/config/plugins/FastSTRM/FastSTRM.dll` |
| Linux | `/var/lib/jellyfin/plugins/FastSTRM/FastSTRM.dll` |
| Windows 服务版 | `C:\ProgramData\Jellyfin\Server\plugins\FastSTRM\FastSTRM.dll` |
| Windows 便携版 | `%LOCALAPPDATA%\jellyfin\plugins\FastSTRM\FastSTRM.dll` |

3. 重启 Jellyfin，在 **控制台 → 插件** 中确认 FastSTRM 已启用。

**方式二：源码编译**（需 [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)）

```bash
git clone https://github.com/zian-yan/jellyfin-plugin-faststrm.git
cd jellyfin-plugin-faststrm
dotnet build Jellyfin.Plugin.FastSTRM/Jellyfin.Plugin.FastSTRM.csproj -c Release
```

将 `Jellyfin.Plugin.FastSTRM/bin/Release/net10.0/FastSTRM.dll` 复制到上述插件目录。

---

## API

需管理员权限 Token 或已登录会话。

| 方法 | 路径 | 说明 |
| :--- | :--- | :--- |
| `GET` | `/FastStrm/Summary` | 统计概览与当前全局策略 |
| `GET` | `/FastStrm/PlaybackPolicy` | 全局策略 + 每个用户的生效策略与来源（`?userId=` 只查一个用户） |
| `GET` | `/FastStrm/PlaybackPolicy/Effective` | 单个用户的生效策略；不带 `userId` 返回全局策略 |
| `POST` | `/FastStrm/Probe/Start?mode=Incremental` | 启动探测（`Incremental` / `Full` / `SizeOnly`） |
| `POST` | `/FastStrm/Probe/Stop` | 停止探测 |
| `GET` | `/FastStrm/Probe/Status` | 任务状态、进度与当前文件 |

---

## 插件仓库（自动更新）

1. 打开 **控制台 → 插件 → 目录**，点击 **添加**；
2. 名称填 `FastSTRM`，地址填：

   ```text
   https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-faststrm/main/manifest.json
   ```

3. 保存后在「插件 → 目录」搜索 `FastSTRM` 安装。

   若 `raw.githubusercontent.com` 访问不稳定，可改用 jsDelivr 镜像（缓存约 12 小时）：

   ```text
   https://cdn.jsdelivr.net/gh/zian-yan/jellyfin-plugin-faststrm@main/manifest.json
   ```
---

## 文档

| 文档 | 内容 |
| :--- | :--- |
| [Wiki 首页](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki) | 文档索引 |
| [播放模式：有流与无流](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/Playback-Modes) | 触发条件、使用差异、适用场景、对比矩阵、选型建议 |
| [排障](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/Troubleshooting) | 起播慢、无时长、进度丢失、补探测失败、任务无法启动 |
| [CHANGELOG](https://github.com/zian-yan/jellyfin-plugin-faststrm/blob/main/CHANGELOG.md) | 版本变更 |

Wiki 源文件位于仓库 `wiki/` 目录，修改后执行 `bash scripts/publish_wiki.sh` 发布。

---

## 兼容性与限制

- 需要 **Jellyfin 12.0+** 与 **.NET 10.0**；Jellyfin 10.11（.NET 9）请使用 3.1.0。
- 仅作用于 `.strm`，其余媒体类型完全透传。
- 无流时能否切换字幕/音轨取决于客户端实现，Jellyfin Web 端通常仍可直连播放。

---

<p align="center">
  <b>简体中文</b> · <a href="README.md">English</a>
</p>
