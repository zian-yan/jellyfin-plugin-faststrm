<p align="center">
  <img src="images/faststrm-banner.png" alt="FastSTRM" width="520">
</p>

<h1 align="center">jellyfin-plugin-faststrm</h1>

<p align="center">Skip remote <code>ffprobe</code> for Jellyfin <code>.strm</code> media — click and play.</p>

<p align="center">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/badge/Release-v3.6.3-blue" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.0%2B-purple" alt="Jellyfin 12.0+">
  <img src="https://img.shields.io/badge/.NET-10.0-blueviolet" alt=".NET 10.0">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/github/downloads/zian-yan/jellyfin-plugin-faststrm/total?label=Downloads&color=success" alt="Downloads"></a>
</p>

<p align="center">
  <b>English</b> · <a href="README.zh-CN.md">简体中文</a>
</p>

---

## Installation

**Option 1: download a release**

1. Download `FastSTRM.dll` from the [latest release](https://github.com/zian-yan/jellyfin-plugin-faststrm/releases).
2. Place it in a `FastSTRM` folder inside the Jellyfin plugin directory:

| Environment | Plugin path |
| :--- | :--- |
| Docker / NAS | `/config/plugins/FastSTRM/FastSTRM.dll` |
| Linux | `/var/lib/jellyfin/plugins/FastSTRM/FastSTRM.dll` |
| Windows (service) | `C:\ProgramData\Jellyfin\Server\plugins\FastSTRM\FastSTRM.dll` |
| Windows (portable) | `%LOCALAPPDATA%\jellyfin\plugins\FastSTRM\FastSTRM.dll` |

3. Restart Jellyfin and confirm FastSTRM is enabled under **Dashboard → Plugins**.

**Option 2: build from source** (requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0))

```bash
git clone https://github.com/zian-yan/jellyfin-plugin-faststrm.git
cd jellyfin-plugin-faststrm
dotnet build Jellyfin.Plugin.FastSTRM/Jellyfin.Plugin.FastSTRM.csproj -c Release
```

Copy `Jellyfin.Plugin.FastSTRM/bin/Release/net10.0/FastSTRM.dll` into the plugin directory above.

---

## Overview

FastSTRM decorates Jellyfin's `IMediaSourceManager` and `IUserDataManager` to decide, at the **playback source resolution** step, whether a remote `ffprobe` request should be made at all:

- **`.strm` with stream info**: uses the stream metadata already in the library, never probes, starts instantly.
- **`.strm` without stream info**: no probe by default either — playback starts instantly; the administrator decides whether to probe in the background afterwards.
- **Non-`.strm`**: 100% pass-through to the native pipeline, zero side effects.

The plugin also ships a background `ProbeService` that turns "without stream info" into "with stream info" through **library-wide pre-probing**, **auto-probe on item added**, and **post-start background probing** — balancing instant start against complete media metadata.

---

## Playback decision flow

```
PlaybackInfo request
    │
    ├── non-.strm ─────────────────────────────────▶ native pass-through
    │
    └── .strm
         ├── with stream info (RunTimeTicks > 0)
         │     ├── and a resolved remote size ──▶ allowMediaProbe = false, zero requests ──▶ instant start
         │     └── but the size is still the .strm text length ──▶ instant start + one size fill after start (no ffprobe)
         │
         └── without stream info
              ├── skip probe = on (default)
              │     ├── post-start policy = no probe (default) ──▶ instant start, stream URL never requested again
              │     └── post-start policy = background probe ────▶ instant start + probe N seconds later
              └── skip probe = off ─────────────────────────────▶ official ffprobe (wait the first time, converts to "with stream info" afterwards)
```

Routing happens in `MediaSourceInterceptor.GetPlaybackMediaSources`; `allowMediaProbe = false` stops Jellyfin from probing the remote URL, so the client receives the media source and connects directly.

"Ready" means `RunTimeTicks > 0` **and** a resolved remote size. Those items take a dedicated fast path in the interceptor: no playback-policy resolution, no background-probe check, one inner call and return — so this path can neither run ffprobe nor hit the stream URL a second time.

---

<p align="center">
  <b>English</b> · <a href="README.zh-CN.md">简体中文</a>
</p>
