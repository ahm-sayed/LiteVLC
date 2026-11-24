# REA Analysis of VLC Media Player & LiteVLC Architecture

This document summarizes the static artifact analysis performed with **REA (Reverse Engineer Anything)** against the installed VLC Media Player (`C:\Program Files\VideoLAN\VLC`) and explains how every architectural choice in **LiteVLC** directly addresses VLC's heaviness and +10s seek audio/video lag.

---

## 1. Observed Shipped-Artifact Facts (REA Evidence)

All findings below are static observations from the installed PE binaries and directory layout. Runtime execution was not profiled by REA in this session, and no native decompiler provider (Ghidra/IDA/Kuna) was configured.

| Target | REA Operation | Evidence ID | Observed Facts |
| :--- | :--- | :--- | :--- |
| `C:\Program Files\VideoLAN\VLC` | `inspect_desktop_application` | `ev_5a3632b5335a3b4e3dd26447e8d3a1e7444242562f345f5bc3659df5e3250ad2` (summary `ev_3cdac9778aa9348161bee02bc8f523b93850644b747c5ec1e6f86b3b461552337354733a`) | **843 files** total, **366** probed binaries/plugins across 29 plugin subdirectories (`codec` 56 DLLs / 42.58 MB, `access` 37 DLLs / 22.67 MB, `gui` 2 DLLs / 18.90 MB, `demux` 47 DLLs / 10.63 MB, `video_filter` 41 DLLs, `audio_filter` 25 DLLs, `stream_out` 20 DLLs, etc.). |
| `vlc.exe` | `inspect_pe_imports` | `ev_266428b84e2a8d2649e37718db004f4cbb80ae5d18d1e30ced41e27f090a633b` | 64-bit PE32+ (`1,050,048` bytes). Thin bootstrapper importing only **7** functions from `libvlc.dll` (`libvlc_new`, `libvlc_add_intf`, `libvlc_playlist_play`, `libvlc_wait`, `libvlc_release`, `libvlc_set_app_id`, `libvlc_set_user_agent`) plus `WININET.dll` (11 HTTP functions). |
| `vlc.exe` | `inspect_pe_resources` | `ev_c3e67fbd806a635892c06b9955f3e9ccae3b0d66f0ea1bbf71cd00670745218f` | 9 embedded resources across 22 directory entries (icons/manifest/version metadata). |
| `libvlc.dll` | `inspect_native_exports` | `ev_6a66e06b31ec4bd1685cd3adf5a2ffb44b1a4f96d3d82a58d4edd16ab094c1a8` (summary `ev_fd460c26febd560ccfd3e02046d6aac26e07b02429c73fb658de27fcac528c63`) | 64-bit PE (`192,448` bytes) exporting **316** public C API functions (`libvlc_media_player_set_time`, `libvlc_media_player_set_position`, `libvlc_media_player_set_rate`, `libvlc_media_player_set_hwnd`, `libvlc_audio_set_track`, `libvlc_video_set_spu`, etc.). |
| `libvlccore.dll` | `inspect_native_exports` & `inspect_pe_imports` | `ev_b265f1d7bd381568be2310e2730d2480a084c8d397b214a759a3379d66265373` / `ev_9e4b6ca6aba8c97de9c481e7b07ac130a54437a3140a34a44243c0ed19cb3159` | 64-bit PE (`3,019,712` bytes) exporting **764** internal core symbols and importing 309 OS APIs (including `WS2_32.dll` 37, `KERNEL32.dll` 102). |
| `plugins\gui\libqt_plugin.dll` | `inspect_pe_imports` | Step 22 retained evidence | **18.9 MB** monolithic Qt GUI plugin importing **661** functions across 13 DLLs (`libvlccore.dll` 172, `USER32.dll` 120, `GDI32.dll` 40, `WINMM.dll` 3, etc.). |
| `plugins\codec\libavcodec_plugin.dll` | `inspect_pe_imports` | `ev_579cc2469a5ac0bc830bc0feb9dc2507da784287089649af07a395b7c43a8d32` | FFmpeg-based decoder plugin (298 imports across 7 DLLs). |
| `plugins\video_output\libdirect3d11_plugin.dll` | `inspect_pe_imports` | Step 28 retained evidence | Direct3D 11 video renderer (194 imports across 8 DLLs). |
| `plugins\audio_output\libmmdevice_plugin.dll` | `inspect_pe_imports` | `ev_fcfefd6371c15d4bfb662de9e7fe3673ebc0cdbe08bdecb8ff8cae0044869b80` | Windows WASAPI/MMDevice audio output plugin (64 imports across 5 DLLs). |

---

## 2. Root Cause Diagnosis (Inferences & Engineering Design)

1. **Why VLC feels heavy:**
   - Loads a 18.9 MB statically-linked Qt plugin (`libqt_plugin.dll`), `libvlccore.dll`, Lua scripting, Skins, Services Discovery, and scans/indexes hundreds of plugin DLLs across 29 directories.
2. **Why VLC lags on +10s seek (video freeze + audio stutter):**
   - VLC's default seek mode performs frame-accurate seeking (jumping to a prior keyframe and decoding every intermediate frame before displaying), flushes decoder FIFOs, and drains/re-primes the WASAPI audio clock.
   - Rapid repeated +10s keypresses trigger back-to-back pipeline flushes instead of coalescing seeks.

---

## 3. How LiteVLC Solves Both Problems

- **Native .NET 10 WPF + Win32 `HwndHost` + single-DLL `libmpv-2.dll`**: Zero Qt bloat, zero 366-plugin directory scanning, instant startup, low RAM footprint.
- **RAM Demuxer Cache (`cache=yes`, `demuxer-max-bytes=48MiB`, `demuxer-max-back-bytes=20MiB`, `demuxer-readahead-secs=15`)**: ±10s seeks hit buffered RAM packets directly instead of stalling on disk I/O while keeping memory lean.
- **Instant Keyframe Seeking (`hr-seek=no` + `relative+keyframes`)**: Eliminates intermediate-frame decode stalls on +10s/-10s skips (with an optional Exact Seek toggle in the UI).
- **Persistent WASAPI Stream (`ao=wasapi`, `audio-stream-silence=yes`, `video-sync=audio`)**: Prevents audio device teardown/re-initialization crackles and A/V desync after seeking.
- **D3D11 Hardware Decoding & Rendering (`vo=gpu`, `gpu-api=d3d11`, `hwdec=auto-safe`)**: Zero-copy GPU decode and presentation.
- **Rapid-Seek Coalescing (55ms debounce accumulator)**: Pressing Right Arrow 3× quickly updates the OSD immediately (`+10s → +20s → +30s`) and executes one clean seek instead of thrashing the decoder.
