# Building DroidFinestra

## Toolchain

- **VS 2022 MSBuild** (Build Tools are enough).
- **.NET Framework 4.0 reference assemblies** (`Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0`).
- **Newtonsoft.Json 13.0.4** in the NuGet global cache — the `net40` build is referenced by `HintPath`; there is
  no restore step (Finestra's arrangement).

The project is Finestra's csproj retargeted: **.NET Framework 4.0, C# 7.3, AnyCPU**. One MSIL exe runs native ARM32
on Windows RT 8.1 (whose in-box 4.5.1 runs it) and natively on x86/x64. Because the compiler builds against the v4.0
reference assemblies, anything newer than 4.0 is a build error rather than a crash on the device. There is no
separate ARM build.

## Build

```powershell
.\scripts\build.ps1                                                   # app only -> dist\DroidFinestra\
.\scripts\build.ps1 -Flavor arm32 -ScrcpyFolder <scrcpy-rt release>   # Windows RT / ARM32
.\scripts\build.ps1 -Flavor x64 -ScrcpyZip scrcpy-win64-v4.1.zip      # 64-bit Windows
```

Each flavor gives, in `dist\`: `DroidFinestra-Setup-<ver>-<flavor>.exe`, `DroidFinestra-<ver>-<flavor>-portable.zip`,
and an updated `SHA256SUMS.txt`.

The script stops unless the exe reports `MSIL` and `.NETFramework,Version=v4.0`, and unless `scrcpy.exe` and
`adb.exe` are the package's architecture (Google's Windows adb is 32-bit x86, which the x64 package accepts). For x64
it also stops unless the zip's SHA-256 is the one scrcpy publishes for v4.1 (`5b12172b…65db`, in scrcpy's
`doc/windows.md`); the official files are copied unmodified. Everything is built in `obj\` first, so a refused run
leaves `dist\` as it was.

## Installer

`installer\Setup.cs` is Finestra's AnyCPU installer adapted to .NET 4.0: compiled with csc against the **4.0 Client
Profile** reference assemblies (so it starts even where only the Client Profile is installed), with the package's files
embedded as gzip resources and a SHA-256 per file, checked on extraction. `Setup.exe --check [file]` writes the
requirements report without installing; `--silent [dir] [--no-desktop]` installs without UI; `--uninstall <dir>` removes.
Upgrade and uninstall first stop an adb server running from the install folder, which would otherwise lock `adb.exe`.

Or directly:

```powershell
& "<VS2022>\MSBuild\Current\Bin\MSBuild.exe" DroidFinestra.sln -t:Rebuild -p:Configuration=Release "-p:Platform=Any CPU"
```

## Icon

`DroidFinestra.ico` (repo root, 16–256 px) is the exe's `ApplicationIcon` and is also embedded as `app.ico`, which
every window and the tray icon load (Finestra's pattern).
