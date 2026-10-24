# VNyan Pose Studio installer
# - Looks for your VNyan folder (a running VNyan, VNyan's own log, Steam, itch), then lets you
#   confirm it or open your VNyan folder yourself
# - Retires v1.x "Jayo"-named files so old and new versions never load together
# - Copies starter configs only when you don't already have one
# - Only asks for admin rights if your VNyan folder is actually write-protected
# Usage: double-click install.bat (or: powershell -File install.ps1)

param(
    [string]$Target = "",     # VNyan folder (skips the search and the folder picker)
    [switch]$Yes,             # answer yes to every question (unattended install)
    [switch]$ListFound,       # only print the VNyan folders the search finds, then exit
    [string]$AppData = "",    # internal: the settings folder of the person who started this
    [switch]$Elevated         # internal: set when relaunched with admin rights
)

$ErrorActionPreference = "Stop"
# Anything unexpected still gets a readable message and a pause; a double-clicked window
# would otherwise just disappear.
trap {
    Write-Host ""
    Write-Host ("Sorry, something went wrong: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "Nothing else was changed."
    if (-not $Yes) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
$plugins = @("PoseStudio")
$legacy  = @{ "PoseStudio" = "JayoPoseStudio" }

function Ask([string]$question, [bool]$defaultYes) {
    if ($Yes) { return $true }
    $a = Read-Host $question
    if ($a -eq "") { return $defaultYes }
    return ($a -match "^[Yy]")
}

function Stop-Here([int]$code) {
    if (-not $Yes) { Read-Host "Press Enter to close" | Out-Null }
    exit $code
}

function Test-VNyanDir([string]$dir) {
    if (-not $dir) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir "VNyan.exe")) -or
           (Test-Path -LiteralPath (Join-Path $dir "VNyan_Data"))
}

# ---- find VNyan by itself (it has no installer, so there is no fixed place to look) ----
function Find-VNyan {
    $found = New-Object System.Collections.Generic.List[string]
    $add = {
        param($dir)
        if (-not $dir) { return }
        try { $dir = [IO.Path]::GetFullPath(([string]$dir -replace '/', '\').TrimEnd('\')) } catch { return }
        if ((Test-VNyanDir $dir) -and -not ($found -contains $dir)) { $found.Add($dir) }   # -contains ignores capitals
    }

    # 1. a VNyan that is open right now
    try { Get-Process -Name "VNyan" -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Path) { & $add (Split-Path $_.Path) } } } catch {}

    # 2. VNyan's own log remembers where it ran from last time
    $logDir = Join-Path $env:USERPROFILE "AppData\LocalLow\Suvidriel\VNyan"
    foreach ($log in @("Player.log", "Player-prev.log")) {
        try {
            $p = Join-Path $logDir $log
            if (Test-Path -LiteralPath $p) {
                foreach ($line in (Get-Content -LiteralPath $p -TotalCount 80)) {
                    if ($line -match "Mono path\[0\] = '(.+?)[\\/]VNyan_Data[\\/]Managed'") { & $add $Matches[1]; break }
                }
            }
        } catch {}
    }

    # 3. Steam libraries
    try {
        $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
        if ($steam) {
            $libs = @($steam)
            $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                    $libs += ($m.Groups[1].Value -replace '\\\\', '\')
                }
            }
            foreach ($lib in $libs) { & $add (Join-Path $lib "steamapps\common\VNyan") }
        }
    } catch {}

    # 4. anything Windows lists under installed apps
    foreach ($root in @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
                        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
                        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")) {
        try {
            Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
                $k = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                if ($k -and $k.DisplayName -like "*VNyan*" -and $k.InstallLocation) { & $add $k.InstallLocation }
            }
        } catch {}
    }

    # 5. the itch.io app
    try {
        $itch = Join-Path $env:APPDATA "itch\apps"
        if (Test-Path -LiteralPath $itch) {
            Get-ChildItem -LiteralPath $itch -Directory -Filter "*vnyan*" -ErrorAction SilentlyContinue | ForEach-Object {
                & $add $_.FullName
                Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object { & $add $_.FullName }
            }
        }
    } catch {}

    return $found.ToArray()
}

# A folder near the right one still counts: VNyan_Data or Items picked by mistake (walk up),
# or the folder that holds the VNyan folder (look one level down).
function Resolve-VNyanFolder([string]$dir) {
    if (-not $dir) { return $null }
    $d = $dir
    for ($i = 0; $i -lt 4 -and $d; $i++) {
        if (Test-VNyanDir $d) { return $d }
        $d = Split-Path $d -Parent
    }
    $child = Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-VNyanDir $_.FullName } | Select-Object -First 1
    if ($child) { return $child.FullName }
    return $null
}

if ($ListFound) {
    $f = @(Find-VNyan)
    if ($f.Count -eq 0) { Write-Host "No VNyan folder found." } else { $f | ForEach-Object { Write-Host $_ } }
    exit 0
}

# ---- locate the plugin files next to this script (release zip layout OR repo layout) ----
$here = Split-Path -Parent $PSCommandPath
$payload = $null
foreach ($base in @($here, (Join-Path $here "dist"), (Join-Path $here "plugins"))) {
    if (Test-Path -LiteralPath (Join-Path $base ($plugins[0] + "\" + $plugins[0] + ".dll"))) { $payload = $base; break }
}
if (-not $payload) {
    Write-Host "ERROR: the plugin folders weren't found next to this script." -ForegroundColor Red
    Write-Host "Unzip the whole download first, then run install.bat from inside the unzipped folder."
    Stop-Here 1
}

# ---- pick the VNyan folder ----
if (-not $Target) {
    Write-Host ""
    Write-Host "VNyan Pose Studio installer" -ForegroundColor Cyan
    Write-Host "---------------------------"
    Write-Host "Looking for VNyan..."
    $found = @(Find-VNyan)
    if ($found.Count -eq 1) {
        Write-Host ("Found VNyan at: " + $found[0])
        if (Ask "Install there? [Y] yes / [n] open a different folder" $true) { $Target = $found[0] }
    } elseif ($found.Count -gt 1) {
        Write-Host "Found VNyan in more than one place:"
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $found[$i]) }
        if ($Yes) { $Target = $found[0] }
        else {
            $a = Read-Host "Type a number to install there, or just press Enter to open a different folder"
            $n = 0
            if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $found.Count) { $Target = $found[$n - 1] }
        }
    } else {
        Write-Host "Couldn't find VNyan by myself. That's okay."
    }

    if (-not $Target) {
        if ($Yes) { Write-Host "ERROR: no VNyan folder found. Run again with -Target <your VNyan folder>." -ForegroundColor Red; exit 1 }
        Write-Host ""
        Write-Host "Please open your VNyan folder (the one with VNyan.exe inside it)."
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Open your VNyan folder (the one with VNyan.exe inside it)"
        $dlg.ShowNewFolderButton = $false
        if ($found.Count -gt 0) { $dlg.SelectedPath = $found[0] }
        $owner = New-Object System.Windows.Forms.Form
        $owner.TopMost = $true   # otherwise the picker can open hidden behind this window
        $ok = $dlg.ShowDialog($owner)
        $owner.Dispose()
        if ($ok -ne [System.Windows.Forms.DialogResult]::OK) { Write-Host "Cancelled."; Stop-Here 1 }
        $Target = $dlg.SelectedPath
    }
}

# ---- sanity-check the chosen folder ----
$resolved = Resolve-VNyanFolder $Target
if ($resolved) { $Target = $resolved }
else {
    Write-Host ""
    Write-Host "Hmm, there's no VNyan.exe in '$Target'." -ForegroundColor Yellow
    if (-not (Ask "Install there anyway? [y/N]" $false)) { Write-Host "Cancelled."; Stop-Here 1 }
}
$assemblies = Join-Path $Target "Items\Assemblies"

# ---- VNyan locks plugin files while it's open ----
# Only a VNyan running from THIS folder is in the way; another copy elsewhere holds no lock on it.
function Test-VNyanInTheWay([string]$root) {
    foreach ($p in @(Get-Process -Name "VNyan" -ErrorAction SilentlyContinue)) {
        $dir = $null
        try { if ($p.Path) { $dir = Split-Path $p.Path } } catch {}
        if (-not $dir) { return $true }   # can't tell where it runs from: assume it is this one
        if ($dir.TrimEnd('\') -ieq $root.TrimEnd('\')) { return $true }
    }
    return $false
}
while (Test-VNyanInTheWay $Target) {
    Write-Host ""
    Write-Host "VNyan is open, and it locks its plugin files. Please close it first." -ForegroundColor Yellow
    if ($Yes) { exit 1 }
    Read-Host "Press Enter once VNyan is closed" | Out-Null
}

# ---- admin rights only if the folder really needs them ----
function Test-Writable([string]$dir) {
    try {
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $probe = Join-Path $dir ("_probe_" + [Guid]::NewGuid().ToString("N") + ".tmp")
        [IO.File]::WriteAllText($probe, "x")
        Remove-Item -LiteralPath $probe -Force
        return $true
    } catch { return $false }
}

if (-not (Test-Writable $assemblies)) {
    if ($Elevated) {
        Write-Host "ERROR: still can't write to '$assemblies', even with admin rights." -ForegroundColor Red
        Stop-Here 1
    }
    Write-Host ""
    Write-Host "Your VNyan folder is write-protected, so asking Windows for admin rights..." -ForegroundColor Yellow
    # Admin windows can't always see the drive this was unzipped on (mapped, network or cloud
    # drives), so hand them a copy in a folder every account can read.
    $stage = Join-Path $env:ProgramData ("VNyanPoseStudioInstall_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $stageDist = Join-Path $stage "dist"
    try {
        New-Item -ItemType Directory -Force -Path $stageDist | Out-Null
        foreach ($p in $plugins) {
            $src = Join-Path $payload $p
            if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $stageDist -Recurse -Force }
        }
        $srcDefaults = Join-Path $payload "defaults"
        if (Test-Path -LiteralPath $srcDefaults) { Copy-Item -LiteralPath $srcDefaults -Destination $stageDist -Recurse -Force }
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $stage "install.ps1") -Force
        if (-not (Test-Path -LiteralPath (Join-Path $stageDist ($plugins[0] + "\" + $plugins[0] + ".dll")))) {
            throw "couldn't copy the plugin files to $stage"
        }

        # A trailing backslash would turn the closing quote into an escaped one and eat the
        # switches after it, so double it up; Windows folds it back to one.
        $targetArg = $Target -replace '(\\+)$', '$1$1'
        $appDataArg = Join-Path $env:USERPROFILE "AppData\LocalLow\Suvidriel\VNyan"
        $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", "`"$(Join-Path $stage 'install.ps1')`"",
            "-Target", "`"$targetArg`"", "-AppData", "`"$appDataArg`"", "-Elevated")
        if ($Yes) { $argList += "-Yes" }
        Start-Process powershell -Verb RunAs -WorkingDirectory $stage -ArgumentList $argList
    }
    catch {
        try { Remove-Item -LiteralPath $stage -Recurse -Force } catch {}
        Write-Host ("Nothing was installed: " + $_.Exception.Message) -ForegroundColor Yellow
        Write-Host "(If Windows asked for admin rights and you said no, that's all this means.)"
        Stop-Here 1
    }
    exit 0
}

# ---- unblock the payload (Mark-of-the-Web: downloaded-zip DLLs won't load while blocked) ----
Get-ChildItem -Recurse -File -LiteralPath $payload -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# ---- install ----
Write-Host ""
$installed = @()
foreach ($p in $plugins) {
    $src = Join-Path $payload $p
    if (-not (Test-Path -LiteralPath (Join-Path $src "$p.dll"))) { Write-Host "  skipping $p (not in this package)"; continue }
    $dst = Join-Path $assemblies $p
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    $ok = $true
    foreach ($f in @("$p.dll", "$p.vnobj")) {
        $from = Join-Path $src $f
        $to = Join-Path $dst $f
        Copy-Item -LiteralPath $from -Destination $to -Force
        try { Unblock-File -LiteralPath $to } catch {}
        if ((Get-FileHash -LiteralPath $from).Hash -ne (Get-FileHash -LiteralPath $to).Hash) {
            Write-Host "ERROR: $f didn't copy correctly." -ForegroundColor Red
            $ok = $false
        }
    }
    # Animations that ship with a plugin (its "builtin" folder, used by Pose Studio). The plugin adds any
    # it has not offered before, and never touches one you have edited or deleted.
    $srcBuiltin = Join-Path $src "builtin"
    if (Test-Path -LiteralPath $srcBuiltin) {
        $dstBuiltin = Join-Path $dst "builtin"
        New-Item -ItemType Directory -Force -Path $dstBuiltin | Out-Null
        foreach ($b in (Get-ChildItem -LiteralPath $srcBuiltin -File -Filter "*.json" -ErrorAction SilentlyContinue)) {
            $bt = Join-Path $dstBuiltin $b.Name
            Copy-Item -LiteralPath $b.FullName -Destination $bt -Force
            try { Unblock-File -LiteralPath $bt } catch {}
            if ((Get-FileHash -LiteralPath $b.FullName).Hash -ne (Get-FileHash -LiteralPath $bt).Hash) {
                Write-Host ("ERROR: " + $b.Name + " didn't copy correctly.") -ForegroundColor Red
                $ok = $false
            }
            else { Write-Host ("  bundled animation: " + $b.BaseName) -ForegroundColor DarkGray }
        }
    }

    if (-not $ok) { Stop-Here 1 }
    $installed += $p
    Write-Host ("  installed " + $p) -ForegroundColor Green
}

# ---- v1.x cleanup: old Jayo-named files must not load alongside v2 ----
# Done AFTER the copy, so a failure half-way can never leave you with no working plugin.
foreach ($p in $plugins) {
    foreach ($dir in @((Join-Path $assemblies $p), (Join-Path $assemblies $legacy[$p]))) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($f in (Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -like "Jayo*.dll" -or $_.Name -like "Jayo*.vnobj" })) {
            try {
                $bak = $f.Name + ".bak"
                if (Test-Path -LiteralPath (Join-Path $dir $bak)) { $bak = $bak + "." + (Get-Date -Format "yyyyMMdd-HHmmss") }
                Rename-Item -LiteralPath $f.FullName -NewName $bak -Force
                Write-Host ("  retired v1 file: " + $f.Name) -ForegroundColor DarkYellow
            } catch { Write-Host ("  couldn't retire " + $f.Name + " (please rename it yourself)") -ForegroundColor Yellow }
        }
    }
}

# ---- starter configs: only when you don't already have one (never overwrite your settings) ----
# The plugins save to AppData\LocalLow\Suvidriel\VNyan; a copy in the VNyan folder is only
# read when no saved settings exist yet.
$defaults = Join-Path $payload "defaults"
if (Test-Path -LiteralPath $defaults) {
    # The elevated window belongs to whoever typed the admin password, so the settings folder is
    # handed down from the first window instead of being worked out again here.
    $appData = $AppData
    if (-not $appData) { $appData = Join-Path $env:USERPROFILE "AppData\LocalLow\Suvidriel\VNyan" }
    foreach ($j in (Get-ChildItem -LiteralPath $defaults -Filter "*.json" -ErrorAction SilentlyContinue)) {
        try {
            # every place the plugins look for settings; if any of them has a file, yours wins
            $mine = @(
                (Join-Path $appData $j.Name),
                (Join-Path $Target $j.Name),
                (Join-Path $assemblies (Join-Path ([IO.Path]::GetFileNameWithoutExtension($j.Name)) $j.Name)),
                (Join-Path (Join-Path $Target "Items") $j.Name)
            )
            $have = $false
            foreach ($m in $mine) { if (Test-Path -LiteralPath $m) { $have = $true; break } }
            if ($have) { continue }
            Copy-Item -LiteralPath $j.FullName -Destination (Join-Path $Target $j.Name) -Force
            Write-Host ("  starter config: " + $j.Name) -ForegroundColor DarkGray
        } catch { Write-Host ("  (couldn't write " + $j.Name + ": skipped, your settings are safe)") -ForegroundColor DarkGray }
    }
}

# the admin copy made above is no longer needed
if ($Elevated -and $here -like (Join-Path $env:ProgramData "VNyanPoseStudioInstall_*")) {
    try { Remove-Item -LiteralPath $here -Recurse -Force } catch {}
}

Write-Host ""
if ($installed.Count -gt 0) {
    Write-Host ("Done! Installed: " + ($installed -join ", ")) -ForegroundColor Cyan
    Write-Host "Start VNyan and open the Plugins window."
    Write-Host "(Settings > Misc > Allow Third Party Plugins must be on, or the list stays empty.)"
    Write-Host "Docs: https://github.com/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio"
} else {
    Write-Host "Nothing installed." -ForegroundColor Yellow
}
Stop-Here 0
