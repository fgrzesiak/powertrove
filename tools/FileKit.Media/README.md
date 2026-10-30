# FileKit.Media

BCL-only .NET 10 helpers for local media tools. No PowerTrove or platform dependency.

`FFmpegLocator.Find()` returns the path of a local `ffmpeg` executable or `null`. It only
probes files and never starts a process. The order is:

1. the application folder,
2. every `PATH` entry (quoted entries are accepted),
3. winget's per-user `%LOCALAPPDATA%\Microsoft\WinGet\Links` folder,
4. winget's portable package folder `Packages\Gyan.FFmpeg_*\<build>\bin`, newest build first.

The winget locations matter because a running process keeps the `PATH` it started with, so a
fresh `winget install Gyan.FFmpeg` is invisible through `PATH` until the next sign-in. Unreadable
or malformed locations are treated as no match. The overload with delegates lets tests supply
paths and file checks without touching the disk.

Limitations: it does not read the registry `PATH`, does not verify that the file is a working
FFmpeg build and does not look for `ffprobe` (callers derive it from the same folder).
