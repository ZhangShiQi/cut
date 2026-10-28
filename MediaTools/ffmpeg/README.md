# Bundled recording tools

Cut invokes the unmodified FFmpeg and ffprobe executables as local child processes.
No system install or PATH search is used. `manifest.json` pins the upstream archive
and each executable by SHA-256. Run `Tools/download-recording-tools.ps1` before
building. Portable packaging runs that script and includes these files.

Build: FFmpeg 9.0.2 essentials, Windows x64, https://www.gyan.dev/ffmpeg/builds/ .
License: GNU GPL version 3; see `LICENSE.txt`. Upstream build configuration and
external library versions: `BUILD-README.txt` (unmodified upstream README).
FFmpeg source for this build: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b .
Upstream binaries and source/build references: https://www.gyan.dev/ffmpeg/builds/ .

Capture and export are offline. Recording cache is session-local and is deleted
on normal close; unlocked orphan caches are removed on next normal startup.
