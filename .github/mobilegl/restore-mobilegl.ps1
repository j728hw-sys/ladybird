$ErrorActionPreference = "Stop"

$launcherRoot = Join-Path $env:APPDATA ".tlauncher\legacy\Minecraft"
$javaBin = Join-Path $launcherRoot "jre\java-runtime-epsilon\windows-x64\java-runtime-epsilon\bin"
$backupDir = Join-Path $javaBin "_MobileGL_original_backup"
$required = @("opengl32.dll", "libEGL.dll", "libGLESv2.dll", "d3dcompiler_47.dll", "vulkan-1.dll")

Write-Host "=== Restore Minecraft Java OpenGL files ===" -ForegroundColor Cyan

Get-Process -Name "TL" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
try {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and
            $_.ExecutablePath.StartsWith($javaBin, [System.StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch {}
Start-Sleep -Milliseconds 500

foreach ($name in $required) {
    $dst = Join-Path $javaBin $name
    $bak = Join-Path $backupDir $name

    if (Test-Path -LiteralPath $bak) {
        Copy-Item -LiteralPath $bak -Destination $dst -Force
        Remove-Item -LiteralPath $bak -Force
        Write-Host "Restored original $name"
    } elseif (Test-Path -LiteralPath $dst) {
        Remove-Item -LiteralPath $dst -Force
        Write-Host "Removed MobileGL $name"
    }
}

Remove-Item -LiteralPath (Join-Path $javaBin "_MobileGL_installed.txt") -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $backupDir) {
    $left = Get-ChildItem -LiteralPath $backupDir -Force -ErrorAction SilentlyContinue
    if (-not $left) { Remove-Item -LiteralPath $backupDir -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "Restored. System32 and the Intel driver were never changed." -ForegroundColor Green
