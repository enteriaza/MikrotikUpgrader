#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes MikrotikWorker as Native AOT binaries for win-x64 and linux-x64.

.DESCRIPTION
    Resolves the repository from this script's location. Publishes the deployable
    application (src/MikrotikWorker) for Release Native AOT into:

      release/win-x64/
      release/linux-x64/

    Each platform directory contains the Native AOT executable, published config,
    and a complete FirmwareFiles/ tree copied from the repository source.

    Windows AOT uses the host MSVC toolchain.
    Linux AOT from Windows prefers WSL (official same-OS Native AOT). When WSL is
    unavailable, the project Sdk StuDev.AotAnywhere enables Zig-based cross-OS
    linking (community package; see report).

.PARAMETER Platform
    win-x64, linux-x64, or all (default: all).
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('win-x64', 'linux-x64', 'all')]
    [string] $Platform = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Solution = Join-Path $RepoRoot 'MikrotikUpgrader.sln'
$Project = Join-Path $RepoRoot 'src\MikrotikWorker\MikrotikWorker.csproj'
$FirmwareSource = Join-Path $RepoRoot 'FirmwareFiles'
$ReleaseRoot = Join-Path $RepoRoot 'release'
$ExeBaseName = 'MikrotikWorker'

function Fail {
    param([string] $Message)
    throw $Message
}

function Assert-LastExitCode {
    param(
        [string] $CommandDescription,
        [int] $ExitCode = $LASTEXITCODE
    )

    if ($null -eq $ExitCode) {
        return
    }

    if ($ExitCode -ne 0) {
        Fail "$CommandDescription failed with exit code $ExitCode."
    }
}

function Get-PlatformMap {
    param([string] $Name)

    switch ($Name) {
        'win-x64' {
            return @{
                Folder    = 'win-x64'
                Rid       = 'win-x64'
                ExeSuffix = '.exe'
            }
        }
        'linux-x64' {
            return @{
                Folder    = 'linux-x64'
                Rid       = 'linux-x64'
                ExeSuffix = ''
            }
        }
        default { return $null }
    }
}

function Test-PathUnderRelease {
    param([string] $CandidatePath)

    $fullCandidate = [System.IO.Path]::GetFullPath($CandidatePath)
    $fullRelease = [System.IO.Path]::GetFullPath($ReleaseRoot)
    $prefix = $fullRelease.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not ($fullCandidate.Equals($fullRelease, [System.StringComparison]::OrdinalIgnoreCase) -or
            $fullCandidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        Fail "Refusing to modify path outside release/: $fullCandidate"
    }
}

function Test-NativeAotBinary {
    param(
        [string] $Path,
        [string] $Rid
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) {
        Fail "Native executable is empty: $Path"
    }

    if ($Rid -eq 'win-x64') {
        if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            Fail "Expected a Windows PE executable: $Path"
        }

        $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or ($peOffset + 256) -ge $bytes.Length) {
            Fail "PE header is outside the file: $Path"
        }

        if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
            Fail "Missing PE signature: $Path"
        }

        $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x8664) {
            Fail ("Expected x64 (0x8664) and found machine 0x{0:X4}: {1}" -f $machine, $Path)
        }

        $optional = $peOffset + 24
        $magic = [System.BitConverter]::ToUInt16($bytes, $optional)
        if ($magic -ne 0x20B) {
            Fail ("Expected PE32+ and found magic 0x{0:X4}: {1}" -f $magic, $Path)
        }

        # CLR runtime directory is data directory index 14 in the PE32+ optional header.
        $clrSizeOffset = $optional + 112 + (14 * 8) + 4
        $clrSize = [System.BitConverter]::ToUInt32($bytes, $clrSizeOffset)
        if ($clrSize -ne 0) {
            Fail "Executable still has a CLR runtime header, so it is not Native AOT: $Path"
        }

        return
    }

    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        Fail "Expected a Linux ELF executable: $Path"
    }

    if ($bytes[4] -ne 2) {
        Fail "Expected a 64-bit ELF executable: $Path"
    }

    if ($bytes[5] -ne 1) {
        Fail "Expected a little-endian ELF executable: $Path"
    }

    $elfType = [System.BitConverter]::ToUInt16($bytes, 16)
    if ($elfType -ne 2 -and $elfType -ne 3) {
        Fail "Expected an ELF executable (ET_EXEC or ET_DYN) and found type ${elfType}: $Path"
    }

    $elfMachine = [System.BitConverter]::ToUInt16($bytes, 18)
    if ($elfMachine -ne 62) {
        Fail "Expected ELF x86-64 (62) and found machine ${elfMachine}: $Path"
    }
}

function Copy-FirmwareFiles {
    param([string] $DestinationRoot)

    if (-not (Test-Path -LiteralPath $FirmwareSource)) {
        Fail "Firmware source directory was not found: $FirmwareSource"
    }

    $files = @(Get-ChildItem -LiteralPath $FirmwareSource -Recurse -File -Force)
    if ($files.Count -eq 0) {
        Fail "Firmware source directory contains no files: $FirmwareSource"
    }

    $dest = Join-Path $DestinationRoot 'FirmwareFiles'
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    # -Path (not -LiteralPath): '*' must expand as a wildcard.
    Copy-Item -Path (Join-Path $FirmwareSource '*') -Destination $dest -Recurse -Force

    $copied = @(Get-ChildItem -LiteralPath $dest -Recurse -File -Force)
    if ($copied.Count -lt $files.Count) {
        Fail "Firmware copy incomplete. Expected at least $($files.Count) files under $dest; found $($copied.Count)."
    }
}

function Copy-PublishOutputs {
    param(
        [string] $PublishDir,
        [string] $PackageDir,
        [string] $ExeSuffix
    )

    New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

    $exeName = $ExeBaseName + $ExeSuffix
    $exeSource = Join-Path $PublishDir $exeName
    if (-not (Test-Path -LiteralPath $exeSource)) {
        Fail "Publish did not produce executable: $exeSource"
    }

    Copy-Item -LiteralPath $exeSource -Destination (Join-Path $PackageDir $exeName) -Force

    # Include runtime JSON configuration; exclude local secrets, Development overlays, and Web SDK noise.
    Get-ChildItem -LiteralPath $PublishDir -File -Force | Where-Object {
        $_.Extension -eq '.json' `
            -and $_.Name -notlike '*.local.json' `
            -and $_.Name -ne 'appsettings.Development.json' `
            -and $_.Name -notlike '*.staticwebassets.endpoints.json'
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $PackageDir $_.Name) -Force
    }
}

function Test-ReleasePackage {
    param(
        [string] $PackageDir,
        [string] $Rid,
        [string] $ExeSuffix
    )

    if (-not (Test-Path -LiteralPath $PackageDir)) {
        Fail "Release package directory was not created: $PackageDir"
    }

    $exePath = Join-Path $PackageDir ($ExeBaseName + $ExeSuffix)
    if (-not (Test-Path -LiteralPath $exePath)) {
        Fail "Missing release executable: $exePath"
    }

    $exe = Get-Item -LiteralPath $exePath
    if ($exe.Length -le 0) {
        Fail "Native executable is empty: $exePath"
    }

    $managedDll = Join-Path $PackageDir ($ExeBaseName + '.dll')
    if (Test-Path -LiteralPath $managedDll) {
        Fail "Managed assembly '$ExeBaseName.dll' is present. Native AOT output must not include it."
    }

    Test-NativeAotBinary -Path $exePath -Rid $Rid

    foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'libhostfxr.so', 'libcoreclr.so')) {
        if (Test-Path -LiteralPath (Join-Path $PackageDir $runtimeFile)) {
            Fail "Shared runtime file '$runtimeFile' is present. The release is not self-contained Native AOT."
        }
    }

    $appsettings = Join-Path $PackageDir 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appsettings)) {
        Fail "Missing appsettings.json in release package: $appsettings"
    }

    if (Test-Path -LiteralPath (Join-Path $PackageDir 'appsettings.Development.local.json')) {
        Fail "Local secrets file must not be packaged."
    }

    $firmware = Join-Path $PackageDir 'FirmwareFiles'
    if (-not (Test-Path -LiteralPath $firmware)) {
        Fail "Missing FirmwareFiles directory: $firmware"
    }

    $firmwareFiles = @(Get-ChildItem -LiteralPath $firmware -Recurse -File -Force)
    if ($firmwareFiles.Count -eq 0) {
        Fail "FirmwareFiles directory is empty: $firmware"
    }
}

function Test-WslLinuxPublishAvailable {
    param([switch] $Explain)

    try {
        & wsl -d Ubuntu -- bash -lc 'uname -m | grep -q x86_64' | Out-Null
        if ($LASTEXITCODE -ne 0) {
            if ($Explain) { Write-Host 'WSL Ubuntu unavailable or not x86_64.' }
            return $false
        }

        & wsl -d Ubuntu -- bash -lc 'command -v clang >/dev/null'
        if ($LASTEXITCODE -ne 0) {
            if ($Explain) { Write-Host 'WSL Ubuntu is present but clang is missing (required for official Linux Native AOT).' }
            return $false
        }

        & wsl -d Ubuntu -- bash -lc 'command -v dotnet >/dev/null'
        if ($LASTEXITCODE -ne 0) {
            if ($Explain) { Write-Host 'WSL Ubuntu is present but dotnet is missing.' }
            return $false
        }

        return $true
    }
    catch {
        if ($Explain) { Write-Host "WSL probe failed: $($_.Exception.Message)" }
        return $false
    }
}

function Invoke-DotNetPublishWindowsHost {
    param(
        [string] $Rid,
        [string] $OutputDir
    )

    Write-Host "dotnet publish MikrotikWorker -c Release -r $Rid --self-contained true -p:PublishAot=true"
    & dotnet publish $Project `
        -c Release `
        -r $Rid `
        --self-contained true `
        -p:PublishAot=true `
        -p:SelfContained=true `
        -p:NoWarn=NU1507 `
        -o $OutputDir `
        --nologo
    Assert-LastExitCode "dotnet publish ($Rid)"
}

function Invoke-DotNetPublishViaWsl {
    param([string] $OutputDir)

    # Map Windows path into WSL and publish with the Linux SDK/clang toolchain (official same-OS AOT).
    $wslProject = & wsl -d Ubuntu -- wslpath -a $Project
    Assert-LastExitCode 'wslpath project'
    $wslOutput = & wsl -d Ubuntu -- wslpath -a $OutputDir
    Assert-LastExitCode 'wslpath output'

    Write-Host "WSL: dotnet publish MikrotikWorker -c Release -r linux-x64 --self-contained true -p:PublishAot=true"
    & wsl -d Ubuntu -- bash -lc "dotnet publish '$wslProject' -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:SelfContained=true -p:NoWarn=NU1507 -o '$wslOutput' --nologo"
    Assert-LastExitCode 'WSL dotnet publish (linux-x64)'
}

function Publish-Platform {
    param(
        [string] $Folder,
        [string] $Rid,
        [string] $ExeSuffix
    )

    $releaseDir = Join-Path $ReleaseRoot $Folder
    Test-PathUnderRelease -CandidatePath $releaseDir

    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('mikrotikupgrader-release-' + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    $publishStage = Join-Path $stagingRoot 'publish'
    $packageStage = Join-Path $stagingRoot 'package'
    $previousBackup = $null
    $swapStarted = $false

    try {
        New-Item -ItemType Directory -Path $publishStage | Out-Null

        if ($Rid -eq 'linux-x64' -and (Test-WslLinuxPublishAvailable -Explain)) {
            Write-Host 'Linux Native AOT: using WSL Ubuntu (official same-OS Native AOT toolchain).'
            Invoke-DotNetPublishViaWsl -OutputDir $publishStage
        }
        else {
            if ($Rid -eq 'linux-x64') {
                Write-Host 'Linux Native AOT: falling back to host publish with StuDev.AotAnywhere (Zig cross-link; not an official Microsoft cross-OS AOT path).'
            }
            else {
                Write-Host 'Windows Native AOT: using host MSVC toolchain.'
            }

            Invoke-DotNetPublishWindowsHost -Rid $Rid -OutputDir $publishStage
        }

        Copy-PublishOutputs -PublishDir $publishStage -PackageDir $packageStage -ExeSuffix $ExeSuffix
        Copy-FirmwareFiles -DestinationRoot $packageStage
        Test-ReleasePackage -PackageDir $packageStage -Rid $Rid -ExeSuffix $ExeSuffix

        $swapStarted = $true
        if (Test-Path -LiteralPath $releaseDir) {
            $previousBackup = $releaseDir + '.previous'
            Test-PathUnderRelease -CandidatePath $previousBackup
            if (Test-Path -LiteralPath $previousBackup) {
                Remove-Item -LiteralPath $previousBackup -Recurse -Force
            }

            Move-Item -LiteralPath $releaseDir -Destination $previousBackup
        }

        New-Item -ItemType Directory -Path $releaseDir | Out-Null
        # -Path (not -LiteralPath): '*' must expand as a wildcard.
        Copy-Item -Path (Join-Path $packageStage '*') -Destination $releaseDir -Recurse -Force
        Test-ReleasePackage -PackageDir $releaseDir -Rid $Rid -ExeSuffix $ExeSuffix

        if ($null -ne $previousBackup -and (Test-Path -LiteralPath $previousBackup)) {
            Remove-Item -LiteralPath $previousBackup -Recurse -Force
            $previousBackup = $null
        }
    }
    catch {
        if ($swapStarted) {
            if (Test-Path -LiteralPath $releaseDir) {
                Remove-Item -LiteralPath $releaseDir -Recurse -Force
            }

            if ($null -ne $previousBackup -and (Test-Path -LiteralPath $previousBackup)) {
                Move-Item -LiteralPath $previousBackup -Destination $releaseDir
                $previousBackup = $null
            }
        }

        throw
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }

    Write-Host ""
    Write-Host "Release directory: $releaseDir"
    Get-ChildItem -LiteralPath $releaseDir -Force | Sort-Object Name | ForEach-Object {
        if ($_.PSIsContainer) {
            $count = @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Force).Count
            Write-Host ("{0,12}  {1}/  ({2} files)" -f '<dir>', $_.Name, $count)
        }
        else {
            Write-Host ("{0,12}  {1}" -f $_.Length, $_.Name)
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $Solution)) {
        Fail "Solution was not found at $Solution."
    }

    if (-not (Test-Path -LiteralPath $Project)) {
        Fail "Project was not found at $Project."
    }

    if (-not (Test-Path -LiteralPath $FirmwareSource)) {
        Fail "FirmwareFiles source was not found at $FirmwareSource."
    }

    $selected = @()
    if ($Platform -eq 'all') {
        $selected = @('win-x64', 'linux-x64')
    }
    else {
        $selected = @($Platform)
    }

    New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null

    foreach ($name in $selected) {
        $mapped = Get-PlatformMap -Name $name
        if ($null -eq $mapped) {
            Fail "Unsupported platform '$name'."
        }

        Write-Host "Restoring $($mapped.Rid) assets for MikrotikWorker"
        & dotnet restore $Project -r $mapped.Rid -p:NoWarn=NU1507 --nologo
        Assert-LastExitCode "dotnet restore ($($mapped.Rid))"

        Publish-Platform -Folder $mapped.Folder -Rid $mapped.Rid -ExeSuffix $mapped.ExeSuffix
    }

    Write-Host ''
    Write-Host 'Publish-Release completed successfully.'
}
catch {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    exit 1
}
