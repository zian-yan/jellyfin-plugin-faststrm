# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

---

## [3.5.0] - 2026-11-09

### ✨ 新增

- **远端文件大小补全**：Jellyfin 探测 `.strm` 时只能拿到本地文本文件本身的大小，插件现在会读取 `.strm` 内的流地址，用 `HEAD`（失败退回 1 字节 `Range` 请求）取真实长度并写回条目，媒体信息页显示真实文件大小。
- 新增配置项 **补全远端文件大小**（默认开启）与 **大小请求超时**（默认 10 秒，1–60）。
- 新增探测模式 **`SizeOnly`（仅补全文件大小）**：完全不走 ffprobe，批量补全仍缺真实大小的条目；配置页下拉框与 `POST /FastStrm/Probe/Start?mode=SizeOnly` 均可用。
- `GET /FastStrm/Summary` 新增 `SizeMissing`、`EnableRemoteSize`、`RemoteSizeTimeoutSeconds`；配置页统计新增「缺少文件大小」指标。

### 💬 优化

- 增量、全量、入库自动与起播补探测结束后自动补写远端大小，无需再单独跑一轮任务。
- 浏览条目详情或开始播放时，若已有缓存直接替换 `MediaSourceInfo.Size`；未命中则后台补一次请求，不阻塞响应、不增加起播等待。
- 只覆盖 `< 1 MiB` 的大小记录，已有真实大小不会被覆盖；同一 URL 共用一次请求，失败条目 10 分钟内不重复请求。

---

## [3.4.2] - 2026-11-06

### ✨ 新增

- 配置页支持多语言：文案跟随 Jellyfin 界面语言自动切换中文（简体 / 繁体）与英文，非中文环境一律显示英文；语言取 `<html lang>` 与当前区域设置，无需手动选择。

### 🐛 修复

- 修复配置页下拉框与输入框内文字被垂直裁掉约四分之一的问题（如「增量探测」「继承全局 (5s)」显示不全）：控件不再写死 `height`，改为 `min-height` + 主题 padding 自适应撑高，换主题或调大字号都不会再裁字。

### 📖 文档

- README 拆分为英文（`README.md`，默认展示）与中文（`README.zh-CN.md`）两个版本，顶部语言切换可互相跳转。

---

## [3.4.1] - 2026-11-04

### 🐛 修复

- 修复「用户级设为『不补探测』、全局开着『后台补探测』时，播放停止后仍会发起补探测」的问题：播放进度上报改为复用取源时记录的策略，不再重新解析后落到全局策略。
- 播放停止后补发的那次进度上报只保存进度，不再调度新的补探测（此前会出现「已经停止播放，后台还在下载探流」）。

---

## [3.4.0] - 2026-11-04

### ✨ 新增

- 新增 **按用户播放策略**：为单个用户覆盖全局策略（继承全局 / 跳过探测·不补探测 / 跳过探测·后台补探测 / 不跳过探测·官方 ffprobe），可在「跳过探测 · 后台补探测」模式下单独设置补探测延迟。
- 新增 `GET /FastStrm/PlaybackPolicy`：返回全局策略、每个用户的生效策略与来源（`User` / `Global` / `Inherit` / `Fallback`），并列出已删除用户的孤立条目。
- 新增 `GET /FastStrm/PlaybackPolicy/Effective`：查询单个用户（或全局）的生效策略。
- `PlaybackPolicyResolver` 成为播放决策的唯一入口：`MediaSourceInterceptor` 与 `PlaybackStateInterceptor` 不再直接读取配置字段。

### 💬 优化

- 全局策略默认值收敛到 `PluginConfiguration.Default*` 常量：新安装、旧配置缺字段、配置对象不可用三条路径得到完全相同的策略。
- 策略解析按配置内容指纹缓存，配置未变时不重建；与播放无关的配置（如探测并发数）不会触发重建。
- 用户级配置非法（拼写错误、空值、重复条目）时回退全局策略并只记一条 Warning，不影响播放。

### 📖 文档

- 新增 Wiki：[按用户播放策略](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/User-Playback-Policy)（优先级、默认值来源、回退矩阵、读取与生效时机）。

---

## [3.3.3] - 2026-11-04

### ✨ 新增

- 新增 **探测并发数** 配置项（1–16，默认 **1**）：增量、全量、入库自动与起播补探测共用同一并发闸门，批量探测按并发数并行处理。
- `GET /FastStrm/Summary` 支持 `refresh=true` 强制刷新，库概览结果默认缓存 30 秒。

### 💬 优化

- 启动探测不再持锁扫描全库：锁内只占位，扫描移入探测任务，停止探测不再被阻塞。
- 日志中的条目名称统一替换控制字符并截断（日志 120 字符 / 界面 200 字符），避免异常文件名伪造日志行或撑大日志。
- 全量探测进度日志改为按时间节流，播放标记缓存的清理最多每分钟一次。

### 🐛 修复

- `POST /FastStrm/Probe/Start` 传入非法 `mode` 现在返回 400，不再静默退化为全库探测。
- 释放残留的单条探测取消源，避免重复入库事件堆积。
- 配置页请求补齐异常处理，探测结束后强制刷新计数。

---

## [3.3.2] - 2026-11-02

### 🐛 修复

- 修复播放标记缓存只增不减导致的长期内存增长（探测成功后回收并加上限）。
- 修复批量探测在「停止中」可重复启动、关闭时未等待任务，以及入库自动探测重复/不可取消的问题。
- 修复多会话停止误取消补探测、装饰器误释放容器共享实例，以及配置页离开后轮询被在途请求重新拉起的问题。

---

## [3.3.1] - 2026-11-02

### ✨ 新增

- 控制台菜单新增 **FastSTRM 设置** 入口。

### 📖 文档

- README 顶部展示 FastSTRM 横幅图片。

---

## [3.3.0] - 2026-11-02

### ✨ 新增

- 未探测 `.strm` 起播后可选 **不补探测**（默认）或 **后台补探测**。
- 后台补探测延迟可配置，默认 **5 秒**（0–3600）。
- 新增「播放停止后取消补探测」开关，默认开启。
- `GET /FastStrm/Summary` 新增后台补探测相关字段。

### 💬 优化

- 关闭后台补探测时保留播放位置，不再补全时长。

### ⚠️ 变更

- 未探测 `.strm` 默认不再后台补探测；需要时长/轨道信息时请在配置页开启「后台补探测」。

---

## [3.2.3] - 2026-11-01

### 🐛 修复

- 后台探测以 `RunTimeTicks` 是否写入判定成败，失败不再打印 `finished`。
- 起播 10 秒后再探测，失败按 `10s → 30s` 重试，最多 3 次。
- 批量预探测运行时不再叠加单集重试。

---

## [3.2.2] - 2026-11-01

### 💬 优化

- 精简控制台「未探测的 STRM 文件也跳过探测」的说明文字。

---

## [3.2.1] - 2026-11-01

### 🐛 修复

- 修复未探测 `.strm` 播放进度丢失、退出时被误标为已观看的问题。
- 起播后自动在后台补探测时长与流信息，探测期间进度仍正常保存。

---

## [3.2.0] - 2026-10-31

### ⬆️ 跟随 Jellyfin 12 / .NET 10 升级

- **目标框架升级**：`net9.0` → `net10.0`，插件需使用 .NET 10 SDK 构建。
- **Jellyfin 依赖升级**：`Jellyfin.Controller` / `Jellyfin.Model` 由 `10.11.3` 升级到 `12.0.0`，`targetAbi` 更新为 `12.0.0.0`。
- **兼容性**：本版本起需要 **Jellyfin 12.0+（.NET 10）**；Jellyfin 10.11（.NET 9）请继续使用 3.1.0。
- **无需改动的部分**：Jellyfin 12 的 `IMediaSourceManager` 接口与 10.11 保持一致，播放拦截、预探测引擎、控制台面板与 API 逻辑均未变动。
- **CI**：GitHub Actions 的 .NET SDK 更新为 `10.0.x`，产物路径改为 `bin/Release/net10.0/FastSTRM.dll`。

---

## [3.1.0] - 2026-10-31

### ⚡ 未探测 STRM 直接跳过探测（极速起播）

- **播放拦截管线**：
  - 未探测（缺少 `RunTimeTicks` / 流信息）的 `.strm` 文件在播放时同样以 `allowMediaProbe = false` 调用底层 `IMediaSourceManager`，不再触发远程 `ffprobe`，消除首次播放的探测等待。
  - 非 `.strm` 文件依旧 100% 原始透传；已有流信息的 `.strm` 行为不变。
  - 新增配置项 `SkipProbeForUnprobedStrm`（默认 `true`）。关闭后，未探测的 `.strm` 会安全回退至官方媒体探测并写入数据库，保留兼容性兜底。
- **控制台面板**：
  - 新增「未探测的 STRM 文件也跳过探测」开关，可随时在极速模式与官方探测兜底模式间切换。
- **API**：
  - `GET /FastStrm/Summary` 新增 `SkipProbeForUnprobedStrm` 字段，便于外部脚本确认当前起播策略。
- **诊断日志**：
  - 未探测 `.strm` 跳过探测且无视频流信息时，每个条目仅记录一条 Warning，提示可通过增量探测补全时长与轨道信息。

### 升级说明
- 插件配置文件新增字段，旧配置按「跳过探测」处理，无需手动修改。

---

## [3.0.0] - 2026-08-27

### 🚀 Major Architectural Rewrite & Features (重大重构与全新特性)

- **核心播放拦截管线重构 (IMediaSourceManager Decorator)**:
  - 彻底摒弃旧版侵入式的 HTTP ActionFilter / PlaybackInfoFilter 机制，改为在 Jellyfin 依赖注入容器中以装饰器模式包装 `IMediaSourceManager`。
  - 实现高效智能的三路分流策略：
    1. **非 `.strm` 文件**：100% 原始透传，不产生任何副作用；
    2. **已有流信息的 `.strm` 文件**（`RunTimeTicks > 0`）：直接禁用 `allowMediaProbe`，跳过远程 `ffprobe` 网络探测，真正实现秒开起播；
    3. **缺少流信息的 `.strm` 文件**（首次入库或未探测）：放行官方媒体探测，保证流信息正确写入数据库并维持兼容性。
  - 除核心拦截点 `GetPlaybackMediaSources` 外，其余所有接口方法均 100% 委托底层原生实现，保证稳定性。

- **新增后台预探测服务 (`ProbeService`)**:
  - 新增 `ProbeService` 作为 Hosted Singleton 运行在 Jellyfin 生命周期中。
  - 支持全媒体库 `.strm` 文件的后台异步探测：
    - **增量探测 (Incremental)**：仅扫描并探测数据库中缺失流信息的 `.strm` 媒体；
    - **全量探测 (Full)**：对全库所有 `.strm` 媒体执行元数据与流信息深度刷新。
  - 支持 **入库自动探测 (Auto-Probe on Item Added)**：监听 `ILibraryManager.ItemAdded` 事件，当新 `.strm` 文件入库时自动在后台触发媒体探测，无需用户手动干预。
  - 提供探测任务的取消与中断控制机制（支持优雅停止与异常跳过）。

- **全新控制台 Web 管理面板 (Admin Dashboard)**:
  - 新增专用的 Jellyfin 控制台插件配置页面（`configPage.html` & `configPage.js`）。
  - **媒体统计仪表盘**：实时展示 `.strm 媒体总数`、`已有流信息数` 以及 `缺少流信息数`。
  - **任务控制中心**：支持一键选择“增量探测”或“全量探测”模式并启动/停止任务。
  - **实时进度监控**：可视化进度条与状态显示（空闲、运行中、停止中），精确显示当前正在探测的媒体名称与索引百分比。
  - **自动化选项配置**：支持在界面中一键开启/关闭“Strm 入库自动探测”功能。

- **新增 RESTful 管理 API (`FastStrmController`)**:
  - 提供需管理员权限 (`RequiresElevation`) 的 API 接口：
    - `GET /FastStrm/Summary`: 获取媒体库 `.strm` 统计概览。
    - `POST /FastStrm/Probe/Start?mode={Incremental|Full}`: 启动预探测任务。
    - `POST /FastStrm/Probe/Stop`: 停止当前预探测任务。
    - `GET /FastStrm/Probe/Status`: 查询当前探测进度及状态。

- **规范化打包与清单支持**:
  - 新增 `meta.json` 与 `build.yaml` 清单文件，支持发布至自定义 Jellyfin 插件源仓库。
  - 升级适配 **.NET 9.0** 与 **Jellyfin 10.11+**。

---

## [2.0.0] - 2026-08-20

### Added
- 引入 `FastStrmPlaybackInfoFilter` 尝试在播放请求阶段拦截并修改媒体源信息。
- 完善针对直链流媒体播放探测超时的基础处理。

### Changed
- 重构部分服务注册逻辑。

---

## [1.0.0] - 2026-08-10

### Added
- 初始版本发布。
- 提供基础的 `.strm` 播放探测拦截原型，尝试消除 Web 端起播等待延迟。
