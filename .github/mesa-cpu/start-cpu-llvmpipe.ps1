param()

$ErrorActionPreference = "Stop"
$bundle = Split-Path -Parent $MyInvocation.MyCommand.Path
$launcherRoot = Join-Path $env:APPDATA ".tlauncher\legacy\Minecraft"
$launcher = Join-Path $launcherRoot "TL.exe"

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Legacy Launcher not found: $launcher"
}

foreach ($name in @("opengl32.dll","libgallium_wgl.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundle $name))) {
        throw "Missing bundle file: $name"
    }
}

$javaBins = @(
    Get-ChildItem -LiteralPath $launcherRoot -Recurse -File -Filter javaw.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -ieq "bin" } |
    ForEach-Object { $_.Directory.FullName } |
    Sort-Object -Unique
)
if ($javaBins.Count -eq 0) { throw "No Legacy Launcher Java bin directory was found." }

foreach ($javaBin in $javaBins) {
    $cpuBackup = Join-Path $javaBin "_CPU_llvmpipe_original_backup"
    $mobileBackup = Join-Path $javaBin "_MobileGL_original_backup"
    New-Item -ItemType Directory -Force $cpuBackup | Out-Null

    foreach ($name in @("opengl32.dll","opengl32sw.dll","libgallium_wgl.dll","libglapi.dll",
                        "libEGL.dll","libGLESv2.dll","d3dcompiler_47.dll","vulkan-1.dll")) {
        $cpuDst = Join-Path $cpuBackup $name
        if (-not (Test-Path -LiteralPath $cpuDst)) {
            $mobileOriginal = Join-Path $mobileBackup $name
            $current = Join-Path $javaBin $name
            if (Test-Path -LiteralPath $mobileOriginal) {
                Copy-Item -LiteralPath $mobileOriginal -Destination $cpuDst -Force
            } elseif (Test-Path -LiteralPath $current) {
                Copy-Item -LiteralPath $current -Destination $cpuDst -Force
            }
        }
    }

    foreach ($name in @("opengl32.dll","opengl32sw.dll","libgallium_wgl.dll","libglapi.dll",
                        "libEGL.dll","libGLESv2.dll","d3dcompiler_47.dll","vulkan-1.dll")) {
        $dst = Join-Path $javaBin $name
        if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Force }
    }

    foreach ($name in @("opengl32.dll","opengl32sw.dll","libgallium_wgl.dll","libglapi.dll")) {
        $src = Join-Path $bundle $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $javaBin $name) -Force
        }
    }
}

$env:GALLIUM_DRIVER = "llvmpipe"
$env:LIBGL_ALWAYS_SOFTWARE = "true"
$env:MESA_LOADER_DRIVER_OVERRIDE = "llvmpipe"
$env:LP_NUM_THREADS = "4"
$env:PATH = "$bundle;$env:PATH"

$optionCandidates = @()
$common = Join-Path $env:APPDATA ".minecraft\options.txt"
if (Test-Path -LiteralPath $common) { $optionCandidates += Get-Item -LiteralPath $common }
$optionCandidates += Get-ChildItem -LiteralPath $launcherRoot -Recurse -File -Filter options.txt -ErrorAction SilentlyContinue
$optionCandidates = $optionCandidates | Sort-Object FullName -Unique

foreach ($file in $optionCandidates) {
    try {
        $lines = Get-Content -LiteralPath $file.FullName
        $seenW = $false
        $seenH = $false
        $seenF = $false
        for ($i=0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^overrideWidth:') { $lines[$i] = 'overrideWidth:640'; $seenW=$true }
            elseif ($lines[$i] -match '^overrideHeight:') { $lines[$i] = 'overrideHeight:360'; $seenH=$true }
            elseif ($lines[$i] -match '^fullscreen:') { $lines[$i] = 'fullscreen:false'; $seenF=$true }
        }
        if (-not $seenW) { $lines += 'overrideWidth:640' }
        if (-not $seenH) { $lines += 'overrideHeight:360' }
        if (-not $seenF) { $lines += 'fullscreen:false' }
        Set-Content -LiteralPath $file.FullName -Value $lines -Encoding UTF8
        Write-Host "Set 640x360 in $($file.FullName)"
    } catch {
        Write-Warning "Could not update $($file.FullName): $($_.Exception.Message)"
    }
}

Write-Host "CPU renderer forced: Mesa llvmpipe"
Write-Host "Window target: 640x360"
Start-Process -FilePath $launcher -WorkingDirectory $launcherRoot
