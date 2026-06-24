#requires -version 3
<#
  Portable installer for the VNyan plugins:
    * VRChat PhysBones   (JayoPhysBones)
    * Jiggle Physics     (JayoJigglePhysics)
    * Pose Studio        (JayoPoseStudio)

  Run it by double-clicking Install.bat (which launches this script). It:
    1. Looks next to itself for the prebuilt .dll / .vnobj / .json files
    2. Tries to auto-detect your VNyan install (Steam libraries, registry, common paths)
    3. Lets you confirm or pick a different folder with a browse dialog
    4. Verifies the folder really is a VNyan install (VNyan_Data\Managed\VNyanInterface.dll)
    5. Strips the "Mark of the Web" so VNyan will load the assemblies after a download/copy
    6. Warns / offers to close VNyan if it is running (it locks the old files)
    7. Self-elevates only if the chosen folder needs Administrator (e.g. Program Files)
    8. Copies each plugin into <VNyan>\Items\Assemblies\<folder> and places starter
       .json configs in the VNyan root (existing configs are kept, never overwritten)

  No Unity, compiler or build step is needed - the bundles in this folder already
  run on VNyan's Unity 2022.3 runtime.
#>
[CmdletBinding()]
param(
    # When set, skip detection/dialog (used by the elevated relaunch).
    [string]$VNyanPath = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type -AssemblyName System.Drawing | Out-Null

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Each plugin: display name, install subfolder, the two binaries, and its config json.
$Plugins = @(
    [pscustomobject]@{ Name = "VRChat PhysBones"; Folder = "JayoPhysBones";  Dll = "JayoPhysBones.dll";     Vnobj = "JayoPhysBones.vnobj";     Json = "physbones.json" }
    [pscustomobject]@{ Name = "Jiggle Physics";   Folder = "JigglePhysics";  Dll = "JayoJigglePhysics.dll"; Vnobj = "JayoJigglePhysics.vnobj"; Json = "jigglephysics.json" }
    [pscustomobject]@{ Name = "Pose Studio";      Folder = "PoseStudio";     Dll = "JayoPoseStudio.dll";    Vnobj = "JayoPoseStudio.vnobj";    Json = "posestudio.json" }
)

function Show-Info ($msg, $title = "VNyan Plugins Installer") {
    [System.Windows.Forms.MessageBox]::Show($msg, $title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}
function Show-Error ($msg, $title = "VNyan Plugins Installer") {
    [System.Windows.Forms.MessageBox]::Show($msg, $title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
}
function Ask-YesNo ($msg, $title = "VNyan Plugins Installer") {
    return [System.Windows.Forms.MessageBox]::Show($msg, $title,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question) -eq [System.Windows.Forms.DialogResult]::Yes
}

# A folder is a VNyan install if VNyan's managed interface DLL is present.
function Test-VNyanFolder ($path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $path "VNyan_Data\Managed\VNyanInterface.dll")) -or
           (Test-Path -LiteralPath (Join-Path $path "VNyan.exe"))
}

# --- best-effort auto-detection of the VNyan folder ---
function Find-VNyan {
    $cands = New-Object System.Collections.Generic.List[string]

    $cands.Add("C:\Program Files\VNyan")
    $cands.Add("C:\Program Files (x86)\VNyan")
    $cands.Add("C:\VNyan")

    # Steam: find Steam, read libraryfolders.vdf, look in each library's steamapps\common\VNyan.
    $steamPaths = @()
    try {
        $sp = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
        if ($sp) { $steamPaths += $sp }
    } catch {}
    try {
        $sp = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -ErrorAction SilentlyContinue).InstallPath
        if ($sp) { $steamPaths += $sp }
    } catch {}
    $steamPaths += "C:\Program Files (x86)\Steam"

    foreach ($steam in ($steamPaths | Select-Object -Unique)) {
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        $libs = New-Object System.Collections.Generic.List[string]
        $libs.Add($steam)
        if (Test-Path -LiteralPath $vdf) {
            try {
                foreach ($line in Get-Content -LiteralPath $vdf) {
                    $m = [regex]::Match($line, '"path"\s*"(.+?)"')
                    if ($m.Success) { $libs.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
                }
            } catch {}
        }
        foreach ($lib in $libs) {
            $cands.Add((Join-Path $lib "steamapps\common\VNyan"))
        }
    }

    # Uninstall registry entries with InstallLocation.
    $regRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($root in $regRoots) {
        try {
            Get-ItemProperty $root -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like "*VNyan*" -and $_.InstallLocation } |
                ForEach-Object { $cands.Add($_.InstallLocation) }
        } catch {}
    }

    foreach ($c in $cands) {
        if (Test-VNyanFolder $c) { return (Resolve-Path -LiteralPath $c).Path }
    }
    return $null
}

# Folder browse dialog seeded with a starting path; loops until valid or cancelled.
function Pick-VNyanFolder ($seed) {
    while ($true) {
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Select your VNyan install folder (the one containing VNyan.exe)"
        $dlg.ShowNewFolderButton = $false
        if ($seed -and (Test-Path -LiteralPath $seed)) { $dlg.SelectedPath = $seed }
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
        $chosen = $dlg.SelectedPath
        if (Test-VNyanFolder $chosen) { return $chosen }
        if (-not (Ask-YesNo "That folder doesn't look like a VNyan install (no VNyan.exe found).`n`n$chosen`n`nPick a different folder?")) {
            return $null
        }
        $seed = $chosen
    }
}

function Test-IsAdmin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------------------

# Verify all plugin payloads are present next to the script.
$missing = @()
foreach ($p in $Plugins) {
    foreach ($file in @($p.Dll, $p.Vnobj, $p.Json)) {
        if (-not (Test-Path -LiteralPath (Join-Path $ScriptDir $file))) { $missing += $file }
    }
}
if ($missing.Count) {
    Show-Error ("Missing plugin files next to this installer:`n`n  {0}`n`nKeep install.ps1, Install.bat and all of the dist files together in the same folder." -f ($missing -join "`n  "))
    exit 1
}

# Strip Mark-of-the-Web from the source files so VNyan/.NET will load them
# (downloads and cross-PC copies tag binaries as blocked, leaving Plugins empty).
try {
    Get-ChildItem -LiteralPath $ScriptDir -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
} catch {}

# Resolve the VNyan folder: use the one passed in (elevated relaunch) or detect + ask.
$vnyan = $VNyanPath
if (-not (Test-VNyanFolder $vnyan)) {
    $detected = Find-VNyan
    if ($detected) {
        if (Ask-YesNo "Found VNyan here:`n`n$detected`n`nInstall the 3 plugins (VRChat PhysBones, Jiggle Physics, Pose Studio) into this folder?") {
            $vnyan = $detected
        } else {
            $vnyan = Pick-VNyanFolder $detected
        }
    } else {
        Show-Info "Couldn't auto-detect your VNyan install. Please pick the folder that contains VNyan.exe."
        $vnyan = Pick-VNyanFolder "C:\Program Files"
    }
}

if (-not (Test-VNyanFolder $vnyan)) {
    Show-Error "No valid VNyan folder selected. Installation cancelled."
    exit 1
}

# If the target needs admin and we aren't elevated, relaunch elevated with the path baked in.
$needsAdmin = $false
try {
    $probeDir = Join-Path $vnyan "Items"
    if (-not (Test-Path -LiteralPath $probeDir)) { $probeDir = $vnyan }
    $probe = Join-Path $probeDir (".writetest_{0}.tmp" -f ([guid]::NewGuid().ToString("N")))
    [System.IO.File]::WriteAllText($probe, "x")
    Remove-Item -LiteralPath $probe -Force
} catch {
    $needsAdmin = $true
}

if ($needsAdmin -and -not (Test-IsAdmin)) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = (Get-Process -Id $PID).Path  # the powershell.exe running us
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -VNyanPath "{1}"' -f $MyInvocation.MyCommand.Path, $vnyan
    $psi.Verb = "runas"
    try {
        [System.Diagnostics.Process]::Start($psi) | Out-Null
    } catch {
        Show-Error ("Administrator permission is required to write into:`n`n{0}`n`nInstallation cancelled." -f $vnyan)
    }
    exit 0
}

# VNyan running? It memory-maps the old files, so the copy will fail. Offer to close it.
$proc = Get-Process -Name "VNyan" -ErrorAction SilentlyContinue
if ($proc) {
    if (Ask-YesNo "VNyan is currently running and will lock the plugin files.`n`nClose VNyan now and continue?") {
        try {
            $proc | Stop-Process -Force
            Start-Sleep -Seconds 2
        } catch {
            Show-Error "Couldn't close VNyan automatically. Please close it manually, then run the installer again."
            exit 1
        }
    } else {
        Show-Error "Please close VNyan, then run the installer again."
        exit 1
    }
}

# Copy every plugin.
$installed = @()
try {
    foreach ($p in $Plugins) {
        $target = Join-Path $vnyan ("Items\Assemblies\{0}" -f $p.Folder)
        if (-not (Test-Path -LiteralPath $target)) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
        }
        Copy-Item -LiteralPath (Join-Path $ScriptDir $p.Dll)   -Destination $target -Force
        Copy-Item -LiteralPath (Join-Path $ScriptDir $p.Vnobj) -Destination $target -Force

        # Starter config: only place it if the user doesn't already have one.
        $jsonDst = Join-Path $vnyan $p.Json
        if (-not (Test-Path -LiteralPath $jsonDst)) {
            Copy-Item -LiteralPath (Join-Path $ScriptDir $p.Json) -Destination $jsonDst -Force
        }
        $installed += ("  {0}  ->  {1}" -f $p.Name, $target)
    }

    # Unblock again at the destination in case the copy carried the MotW tag.
    Get-ChildItem -LiteralPath (Join-Path $vnyan "Items\Assemblies") -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
} catch {
    Show-Error ("Install failed while copying into:`n`n{0}`n`n{1}" -f $vnyan, $_.Exception.Message)
    exit 1
}

Show-Info ("Done! Installed 3 plugins:`n`n{0}`n`nStarter .json configs were placed in`n{1}`n(existing configs were kept).`n`nFully close VNyan and start it again, then open the Plugins window and click `"VRChat PhysBones`", `"Jiggle Physics`", and/or `"Pose Studio`"." -f ($installed -join "`n"), $vnyan)
exit 0
