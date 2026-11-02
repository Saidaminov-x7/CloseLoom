# CloseLoom portability and deployment

CloseLoom is a Windows 11 desktop application for observing user-approved applications in Dry Run only. It is not an antivirus, and forced process termination remains disabled.

## Requirements and limits

- Locally tested: one interactive Windows 11 x64 desktop, .NET 10.
- Independently verified on that interactive desktop: an extracted self-contained win-x64 ZIP launched its WPF window and exited cleanly with code 0. This does not verify compatibility with other PCs.
- Framework-dependent builds require .NET 10 Windows Desktop Runtime.
- This repository can be built with the .NET 10 SDK from an arbitrary path using CloseLoom.sln.
- A portable win-x64 ZIP can be built using scripts/publish-win-x64.ps1. It includes the runtime and needs no app installer or admin rights.
- NOT VERIFIED: Windows 10, Windows Server, Windows ARM64, x86, other DPI/scaling values, other user profiles, third-party security tools, RDP and suspend/resume. There is no guarantee it works on all Windows PCs.
- No file path, login name, drive letter, or development machine path is required at runtime.

## Privacy and upgrade

- The current Windows user owns settings and logs under %LOCALAPPDATA%/CloseLoom.
- The previous pre-release prototype used %LOCALAPPDATA%/AppGuard. Those files are not migrated or removed automatically.
- Do not blindly copy application allowlists between machines: each saved executable path must be verified on the destination PC.
- Local activity logs may contain private paths and PIDs and should not be committed or uploaded.

## Build from source

    dotnet restore CloseLoom.sln
    dotnet build CloseLoom.sln -c Release --no-restore -warnaserror
    dotnet test CloseLoom.sln -c Release --no-build --no-restore

## Portable distribution (Windows 11 x64 preview)

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1

The generated ZIP is saved to ignored artifacts/. Unzip it and run CloseLoom.Desktop.exe. It remains Dry Run only, it is not signed by a trusted publisher, and Windows SmartScreen might warn. Do not disable system security to run a preview. Use the source build if you can verify what you run.

## Safety

A closed window does not guarantee that an application has saved user data. Windows API window event delivery may be incomplete or reordered; always fail closed if observation is uncertain. The present software is not ready for Active process termination.
