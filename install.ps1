# VNyan Physics Plugins installer (PhysBones / JigglePhysics / PoseStudio)
# - Lets you pick your VNyan folder (defaults to C:\Program Files\VNyan)
# - Only asks for admin rights if the chosen folder is actually write-protected
# - Cleans up v1.x "Jayo"-named files so old and new versions never load together
# Usage: double-click install.bat (or: powershell -File install.ps1)

param(
    [string]$Target = "",     # VNyan root folder (skip the picker)
    [switch]$Elevated         # internal: set when relaunched with admin rights
)

$ErrorActionPreference = "Stop"
$plugins = @("PhysBones", "JigglePhysics", "PoseStudio")
$legacy  = @{ "PhysBones" = "JayoPhysBones"; "JigglePhysics" = "JayoJigglePhysics"; "PoseStudio" = "JayoPoseStudio" }

# ---- locate the plugin payload (release zip layout OR repo layout) ----
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = $null
foreach ($base in @($here, (Join-Path $here "plugins"), (Join-Path $here "dist"))) {
    if (Test-Path (Join-Path $base "PhysBones\PhysBones.dll")) { $payload = $base; break }
}
if (-not $payload) {
    Write-Host "ERROR: plugin folders not found next to this script." -ForegroundColor Red
    Write-Host "Keep install.ps1/install.bat alongside the PhysBones, JigglePhysics and PoseStudio folders (or a dist folder containing them)."
    Read-Host "Press Enter to exit"
    exit 1
}

# ---- pick the VNyan folder ----
function Test-VNyanDir([string]$dir) {
    (Test-Path (Join-Path $dir "VNyan.exe")) -or (Test-Path (Join-Path $dir "VNyanInterface.dll")) -or (Test-Path (Join-Path $dir "VNyan_Data"))
}
if (-not $Target) {
    $default = "C:\Program Files\VNyan"
    Write-Host ""
    Write-Host "VNyan Physics Plugins installer" -ForegroundColor Cyan
    Write-Host "-------------------------------"
    if (Test-VNyanDir $default) {
        Write-Host "Found VNyan at: $default"
        $ans = Read-Host "Install there? [Y] yes / [n] choose another folder"
        if ($ans -eq "" -or $ans -match "^[Yy]") { $Target = $default }
    }
    if (-not $Target) {
        Write-Host "Pick your VNyan folder (the one containing VNyan.exe)..."
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Select your VNyan folder (contains VNyan.exe)"
        if (Test-Path $default) { $dlg.SelectedPath = $default }
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Host "Cancelled."; exit 1
        }
        $Target = $dlg.SelectedPath
    }
}

# ---- sanity-check the chosen folder ----
if (-not (Test-VNyanDir $Target)) {
    Write-Host ""
    Write-Host "WARNING: '$Target' does not look like a VNyan folder (no VNyan.exe)." -ForegroundColor Yellow
    $ans = Read-Host "Install anyway? [y/N]"
    if ($ans -notmatch "^[Yy]") { Write-Host "Cancelled."; exit 1 }
}
$assemblies = Join-Path $Target "Items\Assemblies"

# ---- warn if VNyan is running ----
if (Get-Process -Name "VNyan" -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "VNyan is running - it locks plugin files. Please close it." -ForegroundColor Yellow
    Read-Host "Press Enter once VNyan is closed"
}

# ---- elevation only if the target is actually write-protected ----
function Test-Writable([string]$dir) {
    try {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $probe = Join-Path $dir ("_probe_" + [Guid]::NewGuid().ToString("N") + ".tmp")
        [IO.File]::WriteAllText($probe, "x")
        Remove-Item $probe -Force
        return $true
    } catch { return $false }
}

if (-not (Test-Writable $assemblies)) {
    if ($Elevated) {
        Write-Host "ERROR: still cannot write to '$assemblies' even with admin rights." -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
    Write-Host ""
    Write-Host "'$assemblies' is write-protected - requesting admin rights..." -ForegroundColor Yellow
    $script = $MyInvocation.MyCommand.Path
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$script`"", "-Target", "`"$Target`"", "-Elevated"
    )
    exit 0
}

# ---- unblock the payload (Mark-of-the-Web: downloaded-zip DLLs won't load while blocked) ----
Get-ChildItem -Recurse -File $payload -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# ---- v1.x cleanup: old Jayo-named files must not load alongside v2 ----
foreach ($p in $plugins) {
    $old = $legacy[$p]
    foreach ($dir in @((Join-Path $assemblies $p), (Join-Path $assemblies $old))) {
        if (-not (Test-Path $dir)) { continue }
        foreach ($f in (Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "Jayo*.dll" -or $_.Name -like "Jayo*.vnobj" })) {
            Rename-Item $f.FullName ($f.Name + ".bak") -Force
            Write-Host ("  retired v1 file: " + $f.Name) -ForegroundColor DarkYellow
        }
    }
}

# ---- install ----
Write-Host ""
$installed = @()
foreach ($p in $plugins) {
    $src = Join-Path $payload $p
    if (-not (Test-Path "$src\$p.dll")) { Write-Host "  skipping $p (not in this package)"; continue }
    $dst = Join-Path $assemblies $p
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item "$src\$p.dll" $dst -Force
    Copy-Item "$src\$p.vnobj" $dst -Force
    Get-ChildItem $dst -File | Unblock-File -ErrorAction SilentlyContinue
    $installed += $p
    Write-Host ("  installed " + $p) -ForegroundColor Green
}

# ---- default configs: only if not already present (never overwrite user settings) ----
$defaults = Join-Path $payload "defaults"
if (Test-Path $defaults) {
    foreach ($j in (Get-ChildItem $defaults -Filter "*.json" -ErrorAction SilentlyContinue)) {
        $dstJson = Join-Path $Target $j.Name
        if (-not (Test-Path $dstJson)) {
            Copy-Item $j.FullName $dstJson -Force
            Write-Host ("  default config: " + $j.Name) -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
if ($installed.Count -gt 0) {
    Write-Host ("Done! Installed: " + ($installed -join ", ")) -ForegroundColor Cyan
    Write-Host "Start VNyan and check the Plugins window (Settings > Misc > Allow 3rd-party plugins must be on)."
    Write-Host "Docs: https://github.com/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio"
} else {
    Write-Host "Nothing installed." -ForegroundColor Yellow
}
Read-Host "Press Enter to close"
