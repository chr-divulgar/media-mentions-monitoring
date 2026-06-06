Place FFmpeg shared native DLLs in this folder for deterministic deployment.

Required DLL families (versioned names are OK):
- avutil*.dll
- avcodec*.dll
- avformat*.dll
- swresample*.dll

At build/publish time, `Operations.Worker.csproj` copies these files to:
- runtimes/win-x64/native/

The worker bootstrap resolves FFmpeg only from that runtime folder.
This avoids machine-level dependencies and keeps deployment self-contained.
