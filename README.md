# LiteVLC

**LiteVLC** is an ultra-lightweight, zero-seek-lag Windows media player built with **C# (.NET 10 WPF)** and **libmpv (Direct3D 11 + WASAPI)**, designed from an REA analysis of VLC to eliminate startup bloat and +10s seek audio/video stuttering.

مشغّل وسائط خفيف جدًا لنظام ويندوز بدون تقل أو لاج في الصوت والصورة عند التقديم 10 ثواني، مع واجهة داكنة احترافية، بحث ذكي عن الترجمات العربية والأجنبية، ورفع صوت بقوة VLC حتى 200%.

---

## Download (Windows 10/11 64-bit)

Download the latest standalone Windows Installer (**`LiteVLC-Setup-1.2.0.exe`** — includes .NET runtime & `libmpv-2.dll`, no extra setup needed) from the **[Releases](https://github.com/ahm-sayed/LiteVLC/releases/latest)** page.

---

## Key Features

- **Zero-Lag ±10s Seeking**: 48 MiB forward / 20 MiB backward RAM demuxer cache, fast keyframe seeking (`hr-seek=no` with Exact Seek toggle), persistent WASAPI stream, and 55ms rapid-tap coalescing.
- **Direct3D 11 Hardware Decoding**: Zero-copy GPU rendering (`vo=gpu`, `gpu-api=d3d11`, `hwdec=auto-safe`) with flicker-free DirectFlip playback.
- **True Borderless Fullscreen**: Covers 100% of the monitor (including the Windows Taskbar) with locked video edges and a slim VLC-style floating bottom controller that stays below subtitles.
- **Accurate Online Subtitle Search (`D` / `Ctrl+Shift+S`)**: 64-bit OpenSubtitles file-hash matching (`EXACT SYNC`), Cinemeta IMDB movie/series resolution, release-tag ranking, automatic Arabic `Windows-1256 → UTF-8` conversion, and live sync adjustment (`G` / `H`).
- **VLC-Parity Loud Audio**: Up to **200% volume**, 5.1/7.1 downmix dialogue normalization, and a 1-click **Dynamic Audio Boost (`dynaudnorm`)** toggle.
- **Auto-Resume Last Scene**: Automatically saves playback timestamps every 5 seconds and resumes right where you left off (with a top-bar `Resume: ON/OFF` toggle and `0` to restart).
- **Performance HUD (`I`)**: Live telemetry for Video/Audio codec, HWDEC, FPS, RAM cache, A/V sync, and actual measured seek latency in milliseconds.

---

## Keyboard Shortcuts

| Key | Action |
| :--- | :--- |
| `Space` | Play / Pause |
| `→` / `←` or `L` / `J` | Seek +10s / -10s (zero-lag coalesced) |
| `Shift + → / ←` | Seek +3s / -3s |
| `Ctrl + → / ←` | Seek +60s / -60s |
| `0` / `Home` | Restart from `00:00` |
| `↑` / `↓` or Mouse Wheel | Volume ±5% (0–200%) |
| `M` | Mute / Unmute |
| `F` / `F11` / Double-Click | True Fullscreen toggle (`Esc` exits) |
| `D` / `Ctrl+Shift+S` | Search Subtitles Online |
| `G` / `H` | Subtitle Timing Sync -0.5s / +0.5s |
| `V` / `B` / `A` | Cycle Subtitles / Audio Track / Aspect & Fill Mode |
| `[` / `]` / `Backspace` | Speed -0.1x / +0.1x / Reset `1.0x` |
| `I` | Toggle Performance HUD |
| `S` | Save Screenshot (`Pictures\LiteVLC`) |
| `Ctrl+O` / `Ctrl+U` / `Ctrl+L` | Open File / Open Stream URL / Toggle Playlist |

---

## Building from Source

1. Place 64-bit `libmpv-2.dll` (from [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases)) into `native\libmpv-2.dll`.
2. Build or publish with .NET 10 SDK:
   ```powershell
   dotnet publish LiteVLC.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
   ```
3. Compile the Windows installer with Inno Setup 6:
   ```powershell
   ISCC.exe installer\LiteVLC.iss
   ```
