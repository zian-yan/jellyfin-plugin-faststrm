<p align="center">
  <img src="images/faststrm-banner.png" alt="FastSTRM" width="520">
</p>

<h1 align="center">jellyfin-plugin-faststrm</h1>

<p align="center">Skip remote <code>ffprobe</code> for Jellyfin <code>.strm</code> media — click and play.</p>

<p align="center">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/badge/Release-v3.5.1-blue" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.0%2B-purple" alt="Jellyfin 12.0+">
  <img src="https://img.shields.io/badge/.NET-10.0-blueviolet" alt=".NET 10.0">
  <a href="https://github.com/zian-yan/jellyfin-plugin-faststrm/releases"><img src="https://img.shields.io/github/downloads/zian-yan/jellyfin-plugin-faststrm/total?label=Downloads&color=success" alt="Downloads"></a>
</p>

<p align="center">
  <b>English</b> · <a href="README.zh-CN.md">简体中文</a>
</p>

---

## Overview

FastSTRM decorates Jellyfin's `IMediaSourceManager` and `IUserDataManager` to decide, at the **playback source resolution** step, whether a remote `ffprobe` request should be made at all:

- **`.strm` with stream info**: uses the stream metadata already in the library, never probes, starts instantly.
- **`.strm` without stream info**: no probe by default either — playback starts instantly; the administrator decides whether to probe in the background afterwards.
- **Non-`.strm`**: 100% pass-through to the native pipeline, zero side effects.

The plugin also ships a background `ProbeService` that turns "without stream info" into "with stream info" through **library-wide pre-probing**, **auto-probe on item added**, and **post-start background probing** — balancing instant start against complete media metadata.

---

## Project structure

Source is organized by module, and **directory names map 1:1 to namespaces** (`Playback/` → `FastSTRM.Playback`):

```
jellyfin-plugin-faststrm/
├── Jellyfin.Plugin.FastSTRM.sln          Solution (plugin project + test project)
├── Jellyfin.Plugin.FastSTRM/
│   ├── Jellyfin.Plugin.FastSTRM.csproj   Plugin project (assembly name stays `FastSTRM`)
│   ├── Plugin.cs                     Plugin entry point (BasePlugin + config page registration), namespace FastSTRM
│   ├── PluginServiceRegistrator.cs   DI composition root: decorators and service registration
│   ├── Api/                          Admin endpoints (/FastStrm/*)
│   ├── Configuration/                Configuration contract (XML persistence) and config page assets
│   ├── Playback/                     Playback path: policy resolution + the two decorators
│   ├── Probing/                      Background probe service (pre-probe / probe on add / post-start probe)
│   └── Common/                       Cross-module utilities (log redaction)
├── Jellyfin.Plugin.FastSTRM.Test/        xUnit tests (GUID / version consistency guards)
├── build.yaml · meta.json · manifest.json
└── images/ · scripts/ · wiki/
```

> The config page `configPage.html` / `configPage.js` are packaged as **embedded resources**; the resource name is derived from "root namespace + directory path".
> If you move the `Configuration/` directory or change the namespace of `Plugin`, you must update `EmbeddedResourcePath` in `Plugin.GetPages()` accordingly.

---

## Core concept: with and without stream info

The plugin does not look at the file itself; it routes based on **whether the library already holds stream info for that item**. The decision key is `RunTimeTicks`:

| Type | Condition | State in library |
| :--- | :--- | :--- |
| **`.strm` with stream info** | `RunTimeTicks > 0` | Already probed: duration, bitrate, audio/video and subtitle tracks (`MediaStreams`) are all stored |
| **`.strm` without stream info** | `RunTimeTicks` null or `0` | Newly added and not scanned, not covered by pre-probing, or last probe failed / timed out |

> Extreme items with unknown duration (e.g. some live-style `.strm`) are treated as "without stream info" even after a successful probe. This is expected behavior.

---

## Playback decision flow

```
PlaybackInfo request
    │
    ├── non-.strm ─────────────────────────────────▶ native pass-through
    │
    └── .strm
         ├── with stream info ──▶ allowMediaProbe = false ──▶ instant start (unconditional, no config can change it)
         │
         └── without stream info
              ├── skip probe = on (default)
              │     ├── post-start policy = no probe (default) ──▶ instant start, stream URL never requested again
              │     └── post-start policy = background probe ────▶ instant start + probe N seconds later
              └── skip probe = off ─────────────────────────────▶ official ffprobe (wait the first time, converts to "with stream info" afterwards)
```

Routing happens in `MediaSourceInterceptor.GetPlaybackMediaSources`; `allowMediaProbe = false` stops Jellyfin from probing the remote URL, so the client receives the media source and connects directly.

---

## Playback modes at a glance

| Mode | Remote probe | Start | Duration / resume / tracks | Required config |
| :--- | :--- | :--- | :--- | :--- |
| **With stream info** | Never | Milliseconds | Complete, native logic | None |
| **Without · no probe** | Never | Milliseconds | No duration; position kept, not wrongly marked as watched | Skip probe **on** |
| **Without · background probe** | Once after start | Milliseconds | Complete once filled in | Skip probe **on** + background probe |
| **Without · official fallback** | Before playback | Seconds to timeout | Complete after first playback | Skip probe **off** |

The **default combination** is "without stream info · no probe", which fits most deployments. Turn on the background probe when you need a progress bar, resume, or track switching.

Full trigger conditions, behavioral differences, use cases, comparison matrix and selection guidance: see the Wiki —
[**Playback Modes: With and Without Stream Info**](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/Playback-Modes)

---

## Per-user playback policy

The settings above form the **global policy**, i.e. the baseline for every user. A single deployment often needs to treat users differently: some should take the fast path ("skip probe + no probe"), while others (TV clients, users who need complete tracks and progress) only need "skip probe" turned off to fall back to the official ffprobe.
Override the policy per user in **Dashboard → Plugins → FastSTRM → Per-user playback policy**:

| Mode | Skip probe | Background probe |
| :--- | :---: | :---: |
| **Inherit global** (default, equals unconfigured) | From global | From global |
| **Skip probe · no probe** | Yes | No |
| **Skip probe · background probe** | Yes | Yes (delay configurable per user) |
| **Do not skip probe (official ffprobe)** | No | No |

- **Priority**: user-level > global > built-in defaults. Users without an override inherit the global policy.
- **Default source**: global defaults have exactly one source (the `PluginConfiguration.Default*` constants). Fresh installs, old config files missing fields, and unavailable config objects all resolve to the same values: skip probe **on**, background probe **off**, delay **5 s**, cancel on stop **on**.
- **Fallback**: if a user-level mode is invalid, empty, or duplicated, the global policy is used instead with a single Warning — **playback is never blocked**; a bad entry only affects its own user.
- **When it applies**: the policy is resolved fresh on every PlaybackInfo request, so it takes effect on the **next playback** after saving — no restart needed, and running sessions are unaffected. Progress reports during playback reuse the policy captured at source resolution, so the decision cannot flip mid-playback, and progress reports sent after playback stops will not trigger another probe.
- **Items with stream info** (`RunTimeTicks > 0`) ignore all policies and still start unconditionally in milliseconds.

Details, fallback matrix and API examples: see the Wiki —
[**Per-User Playback Policy**](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/User-Playback-Policy)

---

## Remote file size

When Jellyfin probes a `.strm` item it probes the **local text file**, so the stored "file size" — and therefore the size shown on the media info page — is the size of that few-hundred-byte `.strm` file. ffprobe cannot supply the real size either (HTTP streams usually report `size=N/A`).

Remote size filling has **no toggle** (since 3.6.1 the dashboard only keeps the request timeout), but it is triggered by exactly three probe flows. Nothing else ever sends a request to the stream endpoint.

1. The plugin reads the stream URL out of the `.strm` file and sends a `HEAD` request for `Content-Length`;
2. If the endpoint rejects `HEAD` (405/501) or omits the length, it falls back to a one-byte ranged `GET` and reads the total from `Content-Range`;
3. The resolved size is written back onto the item (only records below `< 1 MiB` are replaced — a plausible size is never overwritten).

| Triggers | Behaviour |
| :--- | :--- |
| Auto-probe on import (when enabled) | Unprobed new items are filled in after their ffprobe; items the scan already probed only get the size resolved |
| Post-start background probe ("skip probe + background probe" policy) | Unprobed items are filled in after ffprobe; items with stream info but a missing size only get the size resolved, no ffprobe. This header request is independent of the playback session, so stopping playback does not cancel it |
| Manual incremental / full probe | Incremental covers items missing stream info or size; full re-resolves and overwrites every size |

| Does not trigger | Behaviour |
| :--- | :--- |
| Playback source lookup (PlaybackInfo) | Cache-only display patch: no queueing, no request |
| Opening item details | Same as above |
| "Official probe" / "skip probe, no background probe" | No post-start background probe is scheduled; run an incremental probe manually instead |

The dashboard gains a **Missing file size** counter (a genuinely small but resolved size no longer counts as missing); the request timeout defaults to 10 seconds (1–60).

> Only a lightweight header request is made — nothing is downloaded. Failures (timeout, auth, no length reported) keep the previous value and are retried at most every 10 minutes per item.

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

## API

Requires an admin token or an authenticated session.

| Method | Path | Description |
| :--- | :--- | :--- |
| `GET` | `/FastStrm/Summary` | Summary statistics and the current global policy |
| `GET` | `/FastStrm/PlaybackPolicy` | Global policy plus every user's effective policy and its source (`?userId=` limits to one user) |
| `GET` | `/FastStrm/PlaybackPolicy/Effective` | Effective policy for a single user; without `userId` returns the global policy |
| `POST` | `/FastStrm/Probe/Start?mode=Incremental` | Start probing (`Incremental` / `Full`; the old `SizeOnly` value was removed and is rejected) |
| `POST` | `/FastStrm/Probe/Stop` | Stop probing |
| `GET` | `/FastStrm/Probe/Status` | Task state, progress and current file |

---

## Plugin repository (auto-update)

1. Open **Dashboard → Plugins → Repositories** and click **Add**;
2. Name it `FastSTRM` and use this URL:

   ```text
   https://raw.githubusercontent.com/zian-yan/jellyfin-plugin-faststrm/main/manifest.json
   ```

3. Save, then search for `FastSTRM` under **Plugins → Catalogue** and install it.

   If `raw.githubusercontent.com` is unreliable in your network, use the jsDelivr mirror instead (cached for ~12 hours):

   ```text
   https://cdn.jsdelivr.net/gh/zian-yan/jellyfin-plugin-faststrm@main/manifest.json
   ```
---

## Documentation

| Document | Contents |
| :--- | :--- |
| [Wiki home](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki) | Documentation index |
| [Playback modes: with and without stream info](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/Playback-Modes) | Trigger conditions, behavioral differences, use cases, comparison matrix, selection guidance |
| [Troubleshooting](https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki/Troubleshooting) | Slow start, missing duration, lost progress, failed probes, task won't start |
| [CHANGELOG](https://github.com/zian-yan/jellyfin-plugin-faststrm/blob/main/CHANGELOG.md) | Version history |

Wiki sources live in the repository's `wiki/` directory; run `bash scripts/publish_wiki.sh` to publish after editing.

---

## Compatibility and limitations

- Requires **Jellyfin 12.0+** and **.NET 10.0**; use 3.1.0 for Jellyfin 10.11 (.NET 9).
- Applies to `.strm` only; all other media types pass through untouched.
- Without stream info, whether subtitle/audio track switching works depends on the client; Jellyfin Web can usually still play directly.

---

<p align="center">
  <b>English</b> · <a href="README.zh-CN.md">简体中文</a>
</p>
