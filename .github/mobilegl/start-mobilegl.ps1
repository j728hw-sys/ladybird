$ErrorActionPreference = "Stop"

$bundle = Split-Path -Parent $MyInvocation.MyCommand.Path
$launcherRoot = Join-Path $env:APPDATA ".tlauncher\legacy\Minecraft"
$javaBin = Join-Path $launcherRoot "jre\java-runtime-epsilon\windows-x64\java-runtime-epsilon\bin"
$launcher = Join-Path $launcherRoot "TL.exe"

$required = @(
    "opengl32.dll",
    "libEGL.dll",
    "libGLESv2.dll",
    "d3dcompiler_47.dll",
    "vulkan-1.dll"
)

Write-Host "=== Minecraft 26.3 + MobileGL / ANGLE ===" -ForegroundColor Cyan
Write-Host "Launcher: $launcher"
Write-Host "Java bin:  $javaBin"
Write-Host ""

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Legacy Launcher not found: $launcher"
}
if (-not (Test-Path -LiteralPath (Join-Path $javaBin "javaw.exe"))) {
    throw "Minecraft javaw.exe not found: $javaBin"
}

foreach ($name in $required) {
    $src = Join-Path $bundle $name
    if (-not (Test-Path -LiteralPath $src)) {
        throw "Bundle is incomplete. Missing: $src"
    }
}

Write-Host "[1/4] Closing an already running Legacy Launcher / its Minecraft Java..." -ForegroundColor Yellow
Get-Process -Name "TL" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
try {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and
            $_.ExecutablePath.StartsWith($javaBin, [System.StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
} catch {
    # Not fatal. Copying below will report a useful error if a DLL is still in use.
}
Start-Sleep -Milliseconds 500

Write-Host "[2/4] Installing MobileGL only into Minecraft's bundled Java..." -ForegroundColor Yellow
$backupDir = Join-Path $javaBin "_MobileGL_original_backup"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

foreach ($name in $required) {
    $src = Join-Path $bundle $name
    $dst = Join-Path $javaBin $name
    $bak = Join-Path $backupDir $name

    if ((Test-Path -LiteralPath $dst) -and -not (Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $dst -Destination $bak -Force
        Write-Host "  Backed up existing $name"
    }

    Copy-Item -LiteralPath $src -Destination $dst -Force
    Write-Host "  Installed $name"
}

$marker = Join-Path $javaBin "_MobileGL_installed.txt"
@(
    "Installed: $(Get-Date -Format o)"
    "Bundle: $bundle"
    "MobileGL SHA256: $((Get-FileHash -Algorithm SHA256 (Join-Path $bundle 'opengl32.dll')).Hash)"
) | Set-Content -LiteralPath $marker -Encoding UTF8

Write-Host "[3/4] Selecting DirectGLES -> ANGLE -> D3D11..." -ForegroundColor Yellow
$env:MOBILEGL_BACKEND_TYPE = "DirectGLES"
$env:MOBILEGL_LOG_FILE_PATH = Join-Path $bundle "mobilegl.log"
$env:SDL_OPENGL_LIBRARY = Join-Path $javaBin "opengl32.dll"
$env:PATH = "$javaBin;$bundle;$env:PATH"

# vulkan-1.dll is included only because the current Windows MobileGL binary
# imports the Vulkan loader at DLL load time. DirectVulkan is NOT selected.
# Rendering is still DirectGLES -> ANGLE -> D3D11.
#
# Keep the Intel legacy-driver compatibility SDB already installed.
# This launcher does not modify System32, the Intel driver, or global environment variables.

Write-Host "[4/4] Starting Legacy Launcher..." -ForegroundColor Green
Write-Host ""
Write-Host "In Legacy Launcher start Minecraft 26.3 normally." -ForegroundColor Green
Write-Host "MobileGL log will be: $env:MOBILEGL_LOG_FILE_PATH"
Write-Host ""

Start-Process -FilePath $launcher -WorkingDirectory $launcherRoot
