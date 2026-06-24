# =============================================================================
#  Install-FromSource.ps1
#
#  Portable build + install for the JayoPhysBones / Jiggle Physics / Pose Studio
#  VNyan plugins. Run via INSTALL_BUILD.bat (it self-elevates and launches this
#  with -STA so the folder/file dialogs work).
#
#  Steps:
#    1. Ask the user to pick their VNyan install folder.
#    2. Find their Unity editor (prefer 2022.3.x), or let them browse to it.
#    3. Rebuild each plugin DLL with csc, referencing THEIR VNyan assemblies.
#    4. Rebuild each .vnobj asset bundle with THEIR Unity.
#    5. Copy the DLLs + .vnobj + starter .json into the chosen VNyan folders.
# =============================================================================

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$Root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Root)) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }

function Info ($m)  { Write-Host $m -ForegroundColor Cyan }
function Good ($m)  { Write-Host $m -ForegroundColor Green }
function Warn ($m)  { Write-Host $m -ForegroundColor Yellow }
function Err  ($m)  { Write-Host $m -ForegroundColor Red }

function Fail ($m) {
    Err ""
    Err "*** $m"
    Err "*** Installation aborted. Nothing was changed in VNyan."
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor White
Write-Host "  JayoPhysBones + Jiggle Physics + Pose Studio" -ForegroundColor White
Write-Host "  Build-from-source installer (your Unity, your VNyan)" -ForegroundColor White
Write-Host "============================================================" -ForegroundColor White
Write-Host ""

# ----- plugin definitions -----------------------------------------------------

$managedRefs = @(
    'netstandard.dll',
    'System.dll',
    'System.Core.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.AnimationModule.dll',
    'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'VNyanInterface.dll',
    'Newtonsoft.Json.dll'
)

$plugins = @(
    [pscustomobject]@{
        Name    = 'VRChat PhysBones'
        Dll     = 'JayoPhysBones.dll'
        Vnobj   = 'JayoPhysBones.vnobj'
        Method  = 'PhysBoneBuild.Build'
        Dest    = 'Items\Assemblies\JayoPhysBones'
        Json    = 'physbones.json'
        Sources = @(
            'Assets\JayoPhysBones\Scripts\PhysBoneConfig.cs',
            'Assets\JayoPhysBones\Scripts\BoneUtil.cs',
            'Assets\JayoPhysBones\Scripts\PhysBoneCollider.cs',
            'Assets\JayoPhysBones\Scripts\PhysBoneChain.cs',
            'Assets\JayoPhysBones\Scripts\PhysBonePlugin.cs',
            'Assets\JayoPhysBones\Scripts\WindowDrag.cs'
        )
    },
    [pscustomobject]@{
        Name    = 'Jiggle Physics'
        Dll     = 'JayoJigglePhysics.dll'
        Vnobj   = 'JayoJigglePhysics.vnobj'
        Method  = 'JiggleBuild.Build'
        Dest    = 'Items\Assemblies\JigglePhysics'
        Json    = 'jigglephysics.json'
        Sources = @(
            'Assets\JayoJigglePhysics\Scripts\JiggleConfig.cs',
            'Assets\JayoJigglePhysics\Scripts\JiggleUtil.cs',
            'Assets\JayoJigglePhysics\Scripts\JiggleCollider.cs',
            'Assets\JayoJigglePhysics\Scripts\JiggleBone.cs',
            'Assets\JayoJigglePhysics\Scripts\JiggleWindowDrag.cs',
            'Assets\JayoJigglePhysics\Scripts\JigglePhysicsPlugin.cs'
        )
    },
    [pscustomobject]@{
        Name    = 'Pose Studio'
        Dll     = 'JayoPoseStudio.dll'
        Vnobj   = 'JayoPoseStudio.vnobj'
        Method  = 'PoseStudioBuild.Build'
        Dest    = 'Items\Assemblies\PoseStudio'
        Json    = 'posestudio.json'
        Sources = @(
            'Assets\JayoPoseStudio\Scripts\PoseConfig.cs',
            'Assets\JayoPoseStudio\Scripts\PoseUtil.cs',
            'Assets\JayoPoseStudio\Scripts\PoseApplier.cs',
            'Assets\JayoPoseStudio\Scripts\PoseWindowDrag.cs',
            'Assets\JayoPoseStudio\Scripts\PoseStudioPlugin.cs'
        )
    }
)

# ----- 0) sanity check the source tree ---------------------------------------

$unityProj = Join-Path $Root '_unitybuild'
if (-not (Test-Path (Join-Path $unityProj 'Assets\Editor\PoseStudioBuild.cs'))) {
    Fail "This doesn't look like the full source folder (missing _unitybuild\Assets\Editor). Keep the whole repo together."
}
$pluginsDir = Join-Path $unityProj 'Assets\Plugins'
if (-not (Test-Path $pluginsDir)) { Fail "Missing $pluginsDir." }

foreach ($p in $plugins) {
    foreach ($s in $p.Sources) {
        if (-not (Test-Path (Join-Path $Root $s))) { Fail "Missing source file: $s" }
    }
}

# ----- 1) choose VNyan folder ------------------------------------------------

Info "Step 1: choose your VNyan install folder (the folder that contains VNyan.exe)..."

$fb = New-Object System.Windows.Forms.FolderBrowserDialog
$fb.Description = 'Select your VNyan install folder (the folder containing VNyan.exe)'
$fb.ShowNewFolderButton = $false
# Sensible starting point if the default install exists.
if (Test-Path 'C:\Program Files\VNyan') { $fb.SelectedPath = 'C:\Program Files\VNyan' }

if ($fb.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    Fail "No VNyan folder was selected."
}
$vnyan = $fb.SelectedPath
Good "  VNyan: $vnyan"

$managed = Join-Path $vnyan 'VNyan_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'VNyanInterface.dll'))) {
    Fail "That folder is not a VNyan install (no VNyan_Data\Managed\VNyanInterface.dll). Pick the folder that contains VNyan.exe."
}

# Confirm every reference assembly the build needs is present.
foreach ($r in $managedRefs) {
    if (-not (Test-Path (Join-Path $managed $r))) {
        Fail "Your VNyan is missing $r in VNyan_Data\Managed. Cannot build against this install."
    }
}

# ----- 2) find csc (C# compiler, ships with .NET Framework) ------------------

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    Fail "Could not find the .NET Framework C# compiler (csc.exe). Install the .NET Framework 4.x developer/runtime."
}

# ----- 3) find the user's Unity editor ---------------------------------------

Info ""
Info "Step 2: locating your Unity editor..."

function Get-UnityCandidates {
    $roots = @(
        'C:\Program Files\Unity\Hub\Editor',
        'C:\Program Files\Unity Hub\Editor',
        "${env:ProgramFiles}\Unity\Hub\Editor",
        "${env:LOCALAPPDATA}\Programs\Unity\Hub\Editor"
    ) | Select-Object -Unique

    $list = New-Object System.Collections.Generic.List[object]
    foreach ($r in $roots) {
        if (Test-Path $r) {
            Get-ChildItem -Path $r -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $exe = Join-Path $_.FullName 'Editor\Unity.exe'
                if (Test-Path $exe) {
                    $list.Add([pscustomobject]@{ Version = $_.Name; Exe = $exe })
                }
            }
        }
    }
    # Also the classic single-install location.
    $single = 'C:\Program Files\Unity\Editor\Unity.exe'
    if (Test-Path $single) { $list.Add([pscustomobject]@{ Version = 'unknown'; Exe = $single }) }
    return $list
}

$cands = Get-UnityCandidates
$unity = $null
$unityVer = ''

if ($cands.Count -gt 0) {
    # Prefer 2022.3.*, then any 2022.*, then the highest version string.
    $pref = $cands | Where-Object { $_.Version -like '2022.3.*' } | Sort-Object Version -Descending
    if (-not $pref) { $pref = $cands | Where-Object { $_.Version -like '2022.*' } | Sort-Object Version -Descending }
    if (-not $pref) { $pref = $cands | Sort-Object Version -Descending }
    $chosen = $pref | Select-Object -First 1
    $unity = $chosen.Exe
    $unityVer = $chosen.Version
    Good "  Found Unity $unityVer"
    Info "    $unity"
}

if (-not $unity) {
    Warn "  No Unity editor was found automatically."
    Info "  Please browse to your Unity.exe (e.g. ...\Unity\Hub\Editor\2022.3.x\Editor\Unity.exe)"
    $of = New-Object System.Windows.Forms.OpenFileDialog
    $of.Title = 'Select your Unity.exe'
    $of.Filter = 'Unity editor (Unity.exe)|Unity.exe|All executables (*.exe)|*.exe'
    if ($of.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $unity = $of.FileName
        if ($unity -match '\\Editor\\([^\\]+)\\Editor\\Unity\.exe$') { $unityVer = $Matches[1] }
    }
}

if (-not $unity -or -not (Test-Path $unity)) {
    Fail "No Unity editor selected. Pose Studio's bundles need Unity to rebuild."
}

if ($unityVer -notlike '2022.3.*') {
    Warn ""
    Warn "  The selected Unity ($unityVer) is not 2022.3.x. VNyan runs on Unity 2022.3,"
    Warn "  and asset bundles built with a very different major version may FAIL to load."
    $ans = Read-Host "  Continue anyway? (y/N)"
    if ($ans -notmatch '^(y|yes)$') { Fail "Cancelled by user (Unity version mismatch)." }
}

# ----- 4) build the DLLs against THIS VNyan's assemblies ---------------------

Info ""
Info "Step 3: compiling plugin DLLs against your VNyan assemblies..."

$buildDir = Join-Path $Root 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

foreach ($p in $plugins) {
    $outDll = Join-Path $buildDir $p.Dll

    # -noconfig stops csc from auto-referencing its own framework assemblies
    # (System.dll, System.Core.dll, ...) via the default csc.rsp; without it those
    # collide with VNyan's copies that we reference explicitly (error CS1703).
    $cscArgs = @('-noconfig', '-target:library', '-nologo', '-optimize+', "-out:$outDll")
    foreach ($r in $managedRefs) { $cscArgs += "-reference:$(Join-Path $managed $r)" }
    foreach ($s in $p.Sources)   { $cscArgs += (Join-Path $Root $s) }

    Info "  - $($p.Name)  ->  $($p.Dll)"
    $cscOut = & $csc @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $cscOut | ForEach-Object { Err "      $_" }
        Fail "Compilation of $($p.Dll) failed."
    }
    # The only expected warning is a benign netstandard version note (CS1701) plus
    # its follow-on "(Location of symbol ...)" line; ignore both.
    $realWarnings = $cscOut | Where-Object {
        $_ -match 'warning' -and $_ -notmatch 'CS1701' -and $_ -notmatch 'Location of symbol'
    }
    if ($realWarnings) { $realWarnings | ForEach-Object { Warn "      $_" } }

    if (-not (Test-Path $outDll)) { Fail "csc reported success but $($p.Dll) was not produced." }

    # Stage the fresh DLL into the Unity project so the bundle build picks it up.
    Copy-Item -Force -Path $outDll -Destination (Join-Path $pluginsDir $p.Dll)
}
Good "  All DLLs compiled."

# ----- 5) rebuild the .vnobj bundles with the user's Unity -------------------

Info ""
Info "Step 4: rebuilding asset bundles with Unity $unityVer (this can take a few minutes)..."

$abDir = Join-Path $unityProj 'AssetBundles'

foreach ($p in $plugins) {
    $log = Join-Path $unityProj ("build_" + [IO.Path]::GetFileNameWithoutExtension($p.Vnobj) + ".log")
    Info "  - $($p.Name)  ->  $($p.Vnobj)"

    & $unity -batchmode -quit -projectPath $unityProj -executeMethod $p.Method -logFile $log
    $code = $LASTEXITCODE

    $built = Join-Path $abDir $p.Vnobj
    if ($code -ne 0 -or -not (Test-Path $built)) {
        Err "      Unity build failed (exit $code). Last lines of the log:"
        if (Test-Path $log) { Get-Content $log -Tail 25 | ForEach-Object { Err "      $_" } }
        Fail "Could not build $($p.Vnobj)."
    }
}
Good "  All bundles built."

# ----- 6) install into the chosen VNyan folder -------------------------------

Info ""
Info "Step 5: installing into $vnyan ..."

$installed = New-Object System.Collections.Generic.List[string]

foreach ($p in $plugins) {
    $destFolder = Join-Path $vnyan $p.Dest
    New-Item -ItemType Directory -Force -Path $destFolder | Out-Null

    Copy-Item -Force -Path (Join-Path $buildDir $p.Dll)  -Destination $destFolder
    Copy-Item -Force -Path (Join-Path $abDir $p.Vnobj)   -Destination $destFolder
    $installed.Add("  $($p.Dest)\$($p.Dll) + $($p.Vnobj)")

    # Starter config -> VNyan root, but never overwrite an existing one.
    $jsonSrc = Join-Path $Root ("dist\" + $p.Json)
    $jsonDst = Join-Path $vnyan $p.Json
    if (Test-Path $jsonSrc) {
        if (Test-Path $jsonDst) {
            $installed.Add("  $($p.Json) (kept your existing one)")
        } else {
            Copy-Item -Force -Path $jsonSrc -Destination $jsonDst
            $installed.Add("  $($p.Json) (starter)")
        }
    }
}

Write-Host ""
Good "============================================================"
Good "  Done! Installed:"
$installed | ForEach-Object { Good $_ }
Good "============================================================"
Write-Host ""
Info "Now (re)start VNyan, open the Plugins window, and click"
Info "  'VRChat PhysBones', 'Jiggle Physics', and/or 'Pose Studio'."
Write-Host ""
exit 0
