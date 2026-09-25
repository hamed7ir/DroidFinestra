# Builds DroidFinestra (Release, AnyCPU MSIL, .NET Framework 4.0) with VS 2022 MSBuild, checks what came out, and
# makes the release files. The app exe is the same for every package; only the scrcpy files beside it differ.
#
#   .\scripts\build.ps1                                                  # app only -> dist\DroidFinestra\
#   .\scripts\build.ps1 -Flavor arm32 -ScrcpyFolder <scrcpy-rt release> # Windows RT / ARM32
#   .\scripts\build.ps1 -Flavor x64   -ScrcpyZip scrcpy-win64-v4.1.zip  # 64-bit Windows
#
# Per flavor, in dist\:
#   DroidFinestra-Setup-<ver>-<flavor>.exe    the installer (AnyCPU MSIL, .NET 4.0, payload embedded)
#   DroidFinestra-<ver>-<flavor>-portable.zip extract and run; data stays in that folder
#   SHA256SUMS.txt                            of every release file in dist\
#
# arm32: the scrcpy-rt release folder (scrcpy-rt + adb-rt + ffmpeg-rt, ARM32).
# x64:   the OFFICIAL scrcpy release zip from Genymobile, which ships Google's adb. It is refused unless its SHA-256
#        is the one scrcpy publishes for that release (doc/windows.md at tag v4.1).
#
# Everything is built and checked in obj\ first; dist\ is only written once every check has passed, so a failed
# run never destroys the previous good output.
#
# MSBuild: $env:DROIDFINESTRA_MSBUILD if set, otherwise found with vswhere. csc: the Roslyn beside that MSBuild.
param(
    [ValidateSet('', 'arm32', 'x64')][string]$Flavor = '',
    [string]$ScrcpyFolder,
    [string]$ScrcpyZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# The official scrcpy release the x64 package is pinned to.
$OfficialZipName = 'scrcpy-win64-v4.1.zip'
$OfficialZipSha256 = '5b12172b3264b2889f4583ee64752ce832e29bc8b1089dca81093459697165db'

# PE machine types a flavor's scrcpy.exe / adb.exe must have. Google's Windows adb (in the official scrcpy
# "win64" zip too) is a 32-bit x86 binary, which x64 Windows runs; scrcpy.exe itself must be x64.
$Machines = @{
    'arm32' = @{ 'scrcpy.exe' = @(0x1c4, 0x1c0); 'adb.exe' = @(0x1c4, 0x1c0) }
    'x64'   = @{ 'scrcpy.exe' = @(0x8664);       'adb.exe' = @(0x8664, 0x14c) }
}

function PeMachine([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Seek(0x3C, 'Begin') | Out-Null; $off = $br.ReadInt32()
        $fs.Seek($off + 4, 'Begin') | Out-Null; return [int]$br.ReadUInt16()
    } finally { $fs.Dispose() }
}

function Sha256([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ── inputs first: nothing is built or written until they are right ──────────────────────────────────────────
$tmp = $null
if ($Flavor) {
    if ($Flavor -eq 'x64') {
        if (-not $ScrcpyZip) { throw "-Flavor x64 needs -ScrcpyZip <path to $OfficialZipName>" }
        $sha = Sha256 $ScrcpyZip
        if ($sha -ne $OfficialZipSha256) { throw "$ScrcpyZip has SHA-256 $sha; the official $OfficialZipName is $OfficialZipSha256" }
        "official scrcpy zip verified: $sha"
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('droidfinestra-' + [guid]::NewGuid().ToString('N'))
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ScrcpyZip, $tmp)
        $ScrcpyFolder = (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName
    }
    if (-not $ScrcpyFolder) { throw "-Flavor $Flavor needs -ScrcpyFolder" }
    foreach ($f in 'scrcpy.exe', 'adb.exe', 'scrcpy-server') {
        if (-not (Test-Path (Join-Path $ScrcpyFolder $f))) { throw "$f not found in $ScrcpyFolder" }
    }
    foreach ($f in 'scrcpy.exe', 'adb.exe') {
        $m = PeMachine (Join-Path $ScrcpyFolder $f)
        if ($Machines[$Flavor][$f] -notcontains $m) { throw ("{0} is machine 0x{1:X}, not right for the {2} package" -f $f, $m, $Flavor) }
    }
}

# ── the app ────────────────────────────────────────────────────────────────────────────────────────────────
$msbuild = $env:DROIDFINESTRA_MSBUILD
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { throw 'VS 2022 MSBuild not found. Set DROIDFINESTRA_MSBUILD to MSBuild.exe.' }
"MSBuild: $msbuild"

& $msbuild (Join-Path $root 'DroidFinestra.sln') -t:Rebuild -p:Configuration=Release '-p:Platform=Any CPU' -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$bin = Join-Path $root 'DroidFinestra\bin\Release'
$exe = Join-Path $bin 'DroidFinestra.exe'

# The two properties that make one exe run on RT 8.1 ARM32 and everywhere else.
$arch = [System.Reflection.AssemblyName]::GetAssemblyName($exe).ProcessorArchitecture
$asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($exe)
$tfm = ($asm.GetCustomAttributesData() | Where-Object { $_.AttributeType.Name -eq 'TargetFrameworkAttribute' }).ConstructorArguments[0].Value
$ver = ($asm.GetCustomAttributesData() | Where-Object { $_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute' }).ConstructorArguments[0].Value
"DroidFinestra.exe $ver  arch = $arch   target = $tfm"
if ("$arch" -ne 'MSIL') { throw "expected MSIL, got $arch" }
if ($tfm -ne '.NETFramework,Version=v4.0') { throw "expected .NET Framework 4.0, got $tfm" }

# ── stage the package folder in obj\ ────────────────────────────────────────────────────────────────────────
$name = if ($Flavor) { "DroidFinestra-$Flavor" } else { 'DroidFinestra' }
$obj = Join-Path $root "obj\package-$name"
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }
$pkg = Join-Path $obj 'files'
New-Item -ItemType Directory -Force $pkg | Out-Null
foreach ($f in 'DroidFinestra.exe', 'DroidFinestra.exe.config', 'Newtonsoft.Json.dll') { Copy-Item (Join-Path $bin $f) $pkg }
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.md') { Copy-Item (Join-Path $root $f) $pkg }
if ($Flavor) {
    # scrcpy's binaries, DLLs, server and images, unmodified; its own licence under a name that cannot clash with ours.
    Get-ChildItem $ScrcpyFolder -File |
        Where-Object { $_.Extension -in '.exe', '.dll' -or $_.Name -in 'scrcpy-server', 'scrcpy.png', 'disconnected.png' } |
        Where-Object { $_.Name -notlike 'DroidFinestra*' } | Copy-Item -Destination $pkg
    if (Test-Path (Join-Path $ScrcpyFolder 'LICENSE.txt')) { Copy-Item (Join-Path $ScrcpyFolder 'LICENSE.txt') (Join-Path $pkg 'scrcpy-LICENSE.txt') }
    # the bundle's own notices (scrcpy-rt/adb-rt/ffmpeg-rt notices, LGPL text) travel with its binaries
    Get-ChildItem $ScrcpyFolder -File | Where-Object { $_.Name -like '*NOTICE*' -or $_.Name -like 'COPYING*' } | Copy-Item -Destination $pkg
}
if ($tmp) { Remove-Item $tmp -Recurse -Force }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
if (-not $Flavor) {
    $out = Join-Path $dist $name
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Copy-Item $pkg $out -Recurse
    "staged: $out"
    return
}

# ── the installer: Setup.cs + payload embedded as gzip resources, compiled against .NET 4.0 CLIENT profile ────
$setupDir = Join-Path $obj 'setup'
New-Item -ItemType Directory -Force (Join-Path $setupDir 'payload') | Out-Null
$list = New-Object System.Collections.Generic.List[string]
$rsp = New-Object System.Collections.Generic.List[string]
foreach ($f in Get-ChildItem $pkg -File | Sort-Object Name) {
    $gz = Join-Path $setupDir ('payload\' + $f.Name + '.gz')
    $in = [System.IO.File]::OpenRead($f.FullName)
    $outS = [System.IO.File]::Create($gz)
    $z = New-Object System.IO.Compression.GZipStream($outS, [System.IO.Compression.CompressionMode]::Compress)
    $in.CopyTo($z); $z.Dispose(); $outS.Dispose(); $in.Dispose()
    $list.Add((Sha256 $f.FullName) + "`t" + $f.Length + "`t" + $f.Name)
    $rsp.Add('/resource:"' + $gz + '",payload/' + $f.Name + '.gz')
}
[System.IO.File]::WriteAllLines((Join-Path $setupDir 'payload.list'), $list, (New-Object System.Text.UTF8Encoding($false)))

$info = @"
using System.Reflection;
[assembly: AssemblyTitle("DroidFinestra Setup")]
[assembly: AssemblyProduct("DroidFinestra")]
[assembly: AssemblyCompany("Hamed Ghorbani")]
[assembly: AssemblyCopyright("(c) 2026 Hamed Ghorbani")]
[assembly: AssemblyVersion("$ver.0")]
[assembly: AssemblyFileVersion("$ver.0")]
[assembly: AssemblyInformationalVersion("$ver $Flavor")]
namespace DroidFinestraSetup
{
    internal static class Info
    {
        internal const string AppVersion = "$ver";
        internal const string Flavor = "$Flavor";
    }
}
"@
[System.IO.File]::WriteAllText((Join-Path $setupDir 'SetupInfo.cs'), $info, (New-Object System.Text.UTF8Encoding($false)))

$csc = Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe'
if (-not (Test-Path $csc)) { throw "csc not found beside MSBuild: $csc" }
$refs = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0\Profile\Client'
if (-not (Test-Path (Join-Path $refs 'mscorlib.dll'))) { throw ".NET Framework 4.0 Client Profile reference assemblies not found: $refs" }
$setupExe = Join-Path $setupDir ("DroidFinestra-Setup-$ver-$Flavor.exe")
$rsp.InsertRange(0, [string[]]@(
    '/nostdlib+', '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/langversion:7.3', '/codepage:65001',
    ('/out:"' + $setupExe + '"'),
    ('/win32icon:"' + (Join-Path $root 'DroidFinestra.ico') + '"'),
    ('/resource:"' + (Join-Path $root 'DroidFinestra.ico') + '",setup.icon.ico'),
    ('/resource:"' + (Join-Path $setupDir 'payload.list') + '",payload.list'),
    ('/r:"' + (Join-Path $refs 'mscorlib.dll') + '"'),
    ('/r:"' + (Join-Path $refs 'System.dll') + '"'),
    ('/r:"' + (Join-Path $refs 'System.Core.dll') + '"'),
    ('/r:"' + (Join-Path $refs 'System.Drawing.dll') + '"'),
    ('/r:"' + (Join-Path $refs 'System.Windows.Forms.dll') + '"')))
$rsp.Add('"' + (Join-Path $root 'installer\Setup.cs') + '"')
$rsp.Add('"' + (Join-Path $setupDir 'SetupInfo.cs') + '"')
$rspFile = Join-Path $setupDir 'csc.rsp'
[System.IO.File]::WriteAllLines($rspFile, $rsp, (New-Object System.Text.UTF8Encoding($false)))
# /noconfig only counts on the command line (csc ignores it inside a response file); without it csc adds its
# default references from the installed runtime instead of the 4.0 Client reference set above.
& $csc /noconfig "@$rspFile"
if ($LASTEXITCODE -ne 0) { throw "Setup build failed ($LASTEXITCODE)" }

$sarch = [System.Reflection.AssemblyName]::GetAssemblyName($setupExe).ProcessorArchitecture
$srefs = ([System.Reflection.Assembly]::ReflectionOnlyLoadFrom($setupExe).GetReferencedAssemblies() | ForEach-Object { $_.Name + ' ' + $_.Version }) -join ', '
"Setup arch = $sarch   refs = $srefs"
if ("$sarch" -ne 'MSIL') { throw "Setup: expected MSIL, got $sarch" }
if ($srefs -match ' [1-9][0-9]*\.[1-9]' -or $srefs -notmatch 'mscorlib 4\.0\.0\.0') { throw "Setup must reference only 4.0.0.0 assemblies: $srefs" }

# ── portable zip: the same files plus Finestra's portable marker ────────────────────────────────────────────
$portDir = Join-Path $obj 'portable'
Copy-Item $pkg $portDir -Recurse
New-Item -ItemType File (Join-Path $portDir 'DroidFinestra.portable') -Force | Out-Null
$portZip = Join-Path $obj ("DroidFinestra-$ver-$Flavor-portable.zip")
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($portDir, $portZip)

# ── every check passed: publish into dist\ ─────────────────────────────────────────────────────────────────
Copy-Item $setupExe $dist -Force
Copy-Item $portZip $dist -Force
$sums = Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.exe', '.zip' } | Sort-Object Name |
    ForEach-Object { (Sha256 $_.FullName) + '  ' + $_.Name }
[System.IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), [string[]]$sums, (New-Object System.Text.UTF8Encoding($false)))
"setup:    " + (Join-Path $dist (Split-Path $setupExe -Leaf))
"portable: " + (Join-Path $dist (Split-Path $portZip -Leaf))
"sums:     " + (Join-Path $dist 'SHA256SUMS.txt')
