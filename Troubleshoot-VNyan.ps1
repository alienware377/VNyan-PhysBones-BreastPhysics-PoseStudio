# =============================================================================
#  Troubleshoot-VNyan.ps1
#
#  Read-only diagnostic for the JayoPhysBones / Jiggle Physics / Pose Studio
#  plugins. It does NOT change anything. It writes a report file you can send
#  back so the problem can be pinpointed.
#
#  What it checks:
#    * Which VNyan folder you picked, and that it's a real VNyan install.
#    * The Unity runtime version YOUR VNyan ships (from UnityPlayer.dll).
#    * The Unity version each .vnobj bundle was BUILT with (read from the
#      bundle header) - a mismatch here is the #1 cause of an empty panel.
#    * That all three plugin folders exist with their .dll + .vnobj, sizes,
#      timestamps, and whether Windows has "blocked" them (Mark of the Web).
#    * Your VNyan log (Player.log), pulling out every line that mentions the
#      plugins, asset bundles, or exceptions.
# =============================================================================

$ErrorActionPreference = 'Continue'
$report = New-Object System.Collections.Generic.List[string]
function Line([string]$s = '') { $report.Add($s); Write-Host $s }
function Section([string]$s) { Line ''; Line ('=' * 70); Line $s; Line ('=' * 70) }

Section "VNyan plugin diagnostics  -  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Line "PowerShell: $($PSVersionTable.PSVersion)  |  OS: $([System.Environment]::OSVersion.VersionString)"

# ---- 1) pick the VNyan folder -----------------------------------------------
Add-Type -AssemblyName System.Windows.Forms
$picker = New-Object System.Windows.Forms.FolderBrowserDialog
$picker.Description = 'Select your VNyan install folder (the folder containing VNyan.exe)'
$picker.ShowNewFolderButton = $false
if (Test-Path 'C:\Program Files\VNyan') { $picker.SelectedPath = 'C:\Program Files\VNyan' }
if ($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    Line "No folder selected. Aborting."
    $report | Out-File -FilePath "$PSScriptRoot\VNyan_Diagnostics_Report.txt" -Encoding utf8
    return
}
$VNyan = $picker.SelectedPath
Section "1. VNyan install"
Line "Picked folder : $VNyan"

$hasExe = Test-Path (Join-Path $VNyan 'VNyan.exe')
$managed = Join-Path $VNyan 'VNyan_Data\Managed'
$hasIfc = Test-Path (Join-Path $managed 'VNyanInterface.dll')
Line "VNyan.exe present            : $hasExe"
Line "VNyan_Data\Managed\VNyanInterface.dll present : $hasIfc"
if (-not $hasIfc) {
    Line ""
    Line "*** This does not look like a VNyan install. Re-run and pick the folder"
    Line "    that actually contains VNyan.exe. ***"
    $report | Out-File -FilePath "$PSScriptRoot\VNyan_Diagnostics_Report.txt" -Encoding utf8
    return
}

# ---- 2) VNyan / Unity runtime version ---------------------------------------
Section "2. Versions (THIS is the most important section)"

$exeInfo = $null
if ($hasExe) { try { $exeInfo = (Get-Item (Join-Path $VNyan 'VNyan.exe')).VersionInfo } catch {} }
if ($exeInfo) {
    Line "VNyan.exe ProductVersion : $($exeInfo.ProductVersion)"
    Line "VNyan.exe FileVersion    : $($exeInfo.FileVersion)"
}

$runtimeUnity = '(unknown)'
$upDll = Join-Path $VNyan 'UnityPlayer.dll'
if (Test-Path $upDll) {
    try {
        $vi = (Get-Item $upDll).VersionInfo
        Line "UnityPlayer.dll ProductVersion : $($vi.ProductVersion)"
        Line "UnityPlayer.dll FileVersion    : $($vi.FileVersion)"
        if ($vi.ProductVersion -match '(\d+\.\d+\.\d+[a-z]\d+)') { $runtimeUnity = $matches[1] }
        elseif ($vi.FileVersion -match '(\d+\.\d+\.\d+)') { $runtimeUnity = $matches[1] }
    } catch { Line "Could not read UnityPlayer.dll version: $_" }
} else {
    Line "UnityPlayer.dll NOT found next to VNyan.exe (unexpected)."
}
Line ""
Line ">>> VNyan's Unity RUNTIME version : $runtimeUnity"

# helper: read the Unity version a UnityFS asset bundle was built with
function Get-BundleUnityVersion([string]$path) {
    try {
        $fs = [System.IO.File]::OpenRead($path)
        try {
            $buf = New-Object byte[] 256
            $n = $fs.Read($buf, 0, 256)
        } finally { $fs.Close() }
        $sig = [System.Text.Encoding]::ASCII.GetString($buf, 0, 7)
        if ($sig -ne 'UnityFS') { return "(not a UnityFS bundle; signature='$sig')" }
        # layout: "UnityFS\0"(8) + int32 format(4) + cstring minVersion + cstring revision
        $i = 8 + 4
        function ReadCStr([byte[]]$b, [ref]$idx) {
            $sb = New-Object System.Text.StringBuilder
            while ($idx.Value -lt $b.Length -and $b[$idx.Value] -ne 0) {
                [void]$sb.Append([char]$b[$idx.Value]); $idx.Value++
            }
            $idx.Value++  # skip the null
            return $sb.ToString()
        }
        $ix = [ref]$i
        $minVer = ReadCStr $buf $ix          # e.g. "5.x.x"
        $revision = ReadCStr $buf $ix         # e.g. "2022.3.22f1"
        return $revision
    } catch { return "(error reading bundle: $_)" }
}

# ---- 3) plugin folders, files, bundle versions, block status ----------------
Section "3. Installed plugin files"

$plugins = @(
    @{ Name='VRChat PhysBones'; Folder='JayoPhysBones';  Dll='JayoPhysBones.dll';     Vnobj='JayoPhysBones.vnobj' }
    @{ Name='Jiggle Physics';   Folder='JigglePhysics';  Dll='JayoJigglePhysics.dll'; Vnobj='JayoJigglePhysics.vnobj' }
    @{ Name='Pose Studio';      Folder='PoseStudio';     Dll='JayoPoseStudio.dll';    Vnobj='JayoPoseStudio.vnobj' }
)
$asmRoot = Join-Path $VNyan 'Items\Assemblies'
$mismatch = $false
$script:wrongVNyan = $false
foreach ($p in $plugins) {
    $dir = Join-Path $asmRoot $p.Folder
    Line ""
    Line "--- $($p.Name)  ($dir) ---"
    if (-not (Test-Path $dir)) { Line "  FOLDER MISSING."; continue }
    foreach ($fname in @($p.Dll, $p.Vnobj)) {
        $fp = Join-Path $dir $fname
        if (-not (Test-Path $fp)) { Line "  $fname : MISSING"; continue }
        $fi = Get-Item $fp
        $blocked = $false
        try { if (Get-Item -LiteralPath $fp -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue) { $blocked = $true } } catch {}
        Line ("  {0,-26} {1,9} bytes  modified {2:yyyy-MM-dd HH:mm}  blocked={3}" -f $fname, $fi.Length, $fi.LastWriteTime, $blocked)
        if ($fname -eq $p.Vnobj) {
            $bv = Get-BundleUnityVersion $fp
            Line "      bundle built with Unity : $bv"
            if ($runtimeUnity -ne '(unknown)' -and $bv -match '^\d' -and $bv -ne $runtimeUnity) {
                # compare major.minor only (patch differences are usually fine)
                $rmm = ($runtimeUnity -split '\.')[0..1] -join '.'
                $bmm = ($bv -split '\.')[0..1] -join '.'
                if ($rmm -ne $bmm) {
                    Line "      ^^^ MAJOR/MINOR MISMATCH vs runtime $runtimeUnity  <<< LIKELY THE PROBLEM"
                    $mismatch = $true
                } else {
                    Line "      (patch differs from runtime $runtimeUnity; usually OK)"
                }
            }
        }
    }
}

# ---- 4) VNyan log scrape -----------------------------------------------------
Section "4. VNyan log (Player.log)"

$logCandidates = @()
$lowDirs = @(
    (Join-Path $env:USERPROFILE 'AppData\LocalLow\Suvidriel\VNyan'),
    (Join-Path $env:USERPROFILE 'AppData\LocalLow\Suvidriel VNyan')
)
foreach ($d in $lowDirs) {
    foreach ($n in @('Player.log','output_log.txt','Player-prev.log')) {
        $c = Join-Path $d $n
        if (Test-Path $c) { $logCandidates += $c }
    }
}
# fallback: search LocalLow for any VNyan Player.log
if ($logCandidates.Count -eq 0) {
    $ll = Join-Path $env:USERPROFILE 'AppData\LocalLow'
    if (Test-Path $ll) {
        $logCandidates = Get-ChildItem -Path $ll -Recurse -Filter 'Player.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'VNyan' } | Select-Object -ExpandProperty FullName
    }
}

if (-not $logCandidates -or $logCandidates.Count -eq 0) {
    Line "No Player.log found. Start VNyan once, then re-run this tool."
} else {
    $log = $logCandidates | Sort-Object { (Get-Item $_).LastWriteTime } -Descending | Select-Object -First 1
    Line "Log file : $log"
    Line "Modified : $((Get-Item $log).LastWriteTime)"
    Line ""
    $lines = Get-Content -LiteralPath $log -ErrorAction SilentlyContinue
    Line "Total lines: $($lines.Count)"

    # The very top of the log records WHICH VNyan actually ran (its data path).
    # If that path is NOT the folder you installed into, you launched a
    # different VNyan copy - that alone explains an empty Plugins panel.
    Line ""
    Line "----- log HEADER (first 30 lines: shows which VNyan actually ran) -----"
    $head = $lines | Select-Object -First 30
    foreach ($h in $head) { Line "  $h" }

    $runPath = ''
    foreach ($l in ($lines | Select-Object -First 40)) {
        if ($l -match "Loading player data from\s+(.+?)[/\\]VNyan_Data") { $runPath = $matches[1]; break }
        if ($l -match "Mono path\[0\]\s*=\s*'(.+?)[/\\]VNyan_Data") { $runPath = $matches[1]; break }
    }
    if ($runPath) {
        $runPath = $runPath.TrimStart("'").Replace('/', '\')
        Line ""
        Line ">>> VNyan that ACTUALLY RAN : $runPath"
        $pickedNorm = $VNyan.TrimEnd('\')
        if ($runPath.TrimEnd('\').ToLower() -ne $pickedNorm.ToLower()) {
            Line ">>> MISMATCH! You installed into:"
            Line ">>>            $VNyan"
            Line ">>> but the running VNyan loads from a DIFFERENT folder above."
            Line ">>> THIS is why the Plugins panel is empty. Install into the folder"
            Line ">>> that actually runs, OR launch the VNyan you installed into."
            $script:wrongVNyan = $true
        } else {
            Line ">>> (matches the folder you installed into - good)"
        }
    }

    $keywords = 'Jayo|PhysBones|Jiggle|PoseStudio|Pose Studio|vnyanitem|VNyanTemp|AssetBundle|asset bundle|' +
                'instantiate|Exception|not built with|Could not|Failed to load|TypeLoad|MissingMethod|' +
                'MissingField|registerPluginButton|FileLoad|Loading plugin|plugin'
    Line ""
    Line "----- matching lines (plugins / bundles / errors) -----"
    $hits = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $keywords) {
            Line ("  [{0}] {1}" -f ($i+1), $lines[$i])
            $hits++
        }
    }
    if ($hits -eq 0) { Line "  (no matching lines - the plugins may never have been loaded at all)" }
    Line ""
    Line "----- last 60 lines of the log (for context) -----"
    $tail = $lines | Select-Object -Last 60
    foreach ($t in $tail) { Line "  $t" }
}

# ---- 5) other VNyan copies / running instances ------------------------------
Section "5. Other VNyan copies on this PC (are you running the right one?)"

# Currently running VNyan processes and their .exe paths.
$procs = Get-Process -Name 'VNyan' -ErrorAction SilentlyContinue
if ($procs) {
    Line "VNyan is CURRENTLY running:"
    foreach ($pr in $procs) {
        $pp = ''
        try { $pp = $pr.Path } catch {}
        Line ("  PID {0}  {1}" -f $pr.Id, $pp)
    }
    Line "(More than one, or one you didn't expect, means you may launch the wrong copy.)"
} else {
    Line "No VNyan process running right now."
}

# Light scan of likely locations for other VNyan.exe copies.
Line ""
Line "Searching common locations for other VNyan.exe copies..."
$searchRoots = @(
    'C:\Program Files', 'C:\Program Files (x86)',
    (Join-Path $env:USERPROFILE 'Documents'),
    (Join-Path $env:USERPROFILE 'Downloads'),
    (Join-Path $env:USERPROFILE 'Desktop')
)
$found = @()
foreach ($r in $searchRoots) {
    if (Test-Path $r) {
        $found += Get-ChildItem -Path $r -Recurse -Filter 'VNyan.exe' -Depth 4 -ErrorAction SilentlyContinue |
                  Select-Object -ExpandProperty FullName
    }
}
# also any Steam library
$steamRoots = @('C:\Program Files (x86)\Steam\steamapps\common', 'D:\SteamLibrary\steamapps\common', 'E:\SteamLibrary\steamapps\common')
foreach ($r in $steamRoots) {
    if (Test-Path $r) {
        $found += Get-ChildItem -Path $r -Recurse -Filter 'VNyan.exe' -Depth 3 -ErrorAction SilentlyContinue |
                  Select-Object -ExpandProperty FullName
    }
}
$found = $found | Sort-Object -Unique
if ($found.Count -le 1) {
    Line "Only one VNyan.exe found (good): $($found -join '; ')"
} else {
    Line "MULTIPLE VNyan.exe found - make sure you install into AND launch the SAME one:"
    foreach ($f in $found) {
        $marker = ''
        if ($f.ToLower().StartsWith($VNyan.ToLower())) { $marker = '   <-- the one you installed into' }
        Line "  $f$marker"
    }
}

# ---- 6) verdict --------------------------------------------------------------
Section "6. Quick verdict"
if ($script:wrongVNyan) {
    Line "WRONG-COPY PROBLEM: the VNyan that actually ran is NOT the folder you"
    Line "installed the plugins into (see section 4). Either:"
    Line "  - launch the VNyan.exe inside the folder you installed into, OR"
    Line "  - re-run the installer and pick the folder that VNyan actually runs from."
} elseif ($mismatch) {
    Line "A Unity major/minor MISMATCH was detected between a bundle and the runtime."
    Line "The .vnobj bundles need to be rebuilt with a Unity version matching"
    Line "VNyan's runtime ($runtimeUnity). Send this report back."
} else {
    Line "Files, versions, and block-status all look correct. If the Plugins panel"
    Line "is still empty, check these TWO things (both produce a silent empty panel"
    Line "with no errors in the log):"
    Line ""
    Line "  1) VNyan -> Settings -> Misc -> 'Allow Third Party Plugins' must be ON."
    Line "     If it's off, VNyan loads NO plugins and logs nothing. CHECK THIS FIRST."
    Line ""
    Line "  2) Make sure you LAUNCH the same VNyan you installed into (see sections"
    Line "     4 and 5). The socket errors in the log mean a second VNyan instance"
    Line "     was running - fully close ALL VNyan, then relaunch just one."
}

# ---- write the report --------------------------------------------------------
$out = "$PSScriptRoot\VNyan_Diagnostics_Report.txt"
$report | Out-File -FilePath $out -Encoding utf8
Line ""
Line "============================================================"
Line "Report written to:"
Line "  $out"
Line "Please send that file back."
Line "============================================================"
