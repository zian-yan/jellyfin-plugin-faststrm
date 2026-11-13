<p align="center">
  <img src="images/faststrm-banner.png" alt="FastSTRM" width="520">
</p>

<h1 align="center">jellyfin-plugin-faststrm</h1>

<p align="center">让 Jellyfin 的 <code>.strm</code> 媒体跳过远程 <code>ffprobe</code>，点击即播。</p>

<p align="center">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/badge/Release-v3.6.5-blue" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.0%2B-purple" alt="Jellyfin 12.0+">
  <img src="https://img.shields.io/badge/.NET-10.0-blueviolet" alt=".NET 10.0">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/github/downloads/zian-yan/jellyfin-plugin-faststrm/total?label=Downloads&color=success" alt="Downloads"></a>
</p>

<p align="center">
  <b>简体中文</b> · <a href="README.md">English</a>
</p>

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

## 简介

FastSTRM 以装饰器方式包裹 Jellyfin 的 `IMediaSourceManager` 与 `IUserDataManager`，在**播放取源**这一步决定是否发起远程 `ffprobe`：

- **有流 `.strm`**：直接使用库中已有的流信息，永不探测，立即起播。
- **无流 `.strm`**：默认也不探测，立即起播；是否在起播后补探测由管理员决定。
- **非 `.strm`**：100% 透传原生管线，零副作用。

插件同时提供后台 `ProbeService`，通过**全库预探测**、**入库自动探测**、**起播后补探测**把「无流」转成「有流」，在秒开与完整媒体信息之间取得平衡。

---

## 播放决策流程

```
PlaybackInfo 请求
    │
    ├── 非 .strm ─────────────────────────────────▶ 原生透传
    │
    └── .strm
         ├── 有流（RunTimeTicks > 0）
         │     ├── 且远端大小已补全 ──▶ allowMediaProbe = false，零探测零请求 ──▶ 秒开
         │     └── 但大小仍是 .strm 文本长度 ──▶ 秒开 + 按策略补一次大小（不跑 ffprobe）
         │
         └── 无流
              ├── 跳过探测 = 开（默认）
              │     ├── 起播后策略 = 不补探测（默认）──▶ 秒开，不再请求流地址
              │     └── 起播后策略 = 后台补探测 ──────▶ 秒开 + N 秒后补探测
              └── 跳过探测 = 关 ────────────────────▶ 官方 ffprobe（首次等待，成功后转为有流）
```

分流发生在 `MediaSourceInterceptor.GetPlaybackMediaSources`；`allowMediaProbe = false` 即阻止 Jellyfin 对远程地址发起探测，客户端拿到媒体源后直接直连播放。

「就绪」= `RunTimeTicks > 0` **且** 远端大小已补全。这类条目在拦截器里走单独的快速路径：不解析播放策略、不判定补探测条件、不校验大小，只做一次取源调用就返回，因此这条路径上不可能发生 ffprobe，也不可能对流地址多发一次请求。

命中这条路径时，服务端日志只输出一行 **Info** 级记录（无需开 Debug 就能看到）：

```
[INF] FastSTRM: 加速成功
```

---

<p align="center">
  <b>简体中文</b> · <a href="README.md">English</a>
</p>
