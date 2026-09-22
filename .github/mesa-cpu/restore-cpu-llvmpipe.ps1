param()
$ErrorActionPreference = "Stop"
$launcherRoot = Join-Path $env:APPDATA ".tlauncher\legacy\Minecraft"

$javaBins = @(
    Get-ChildItem -LiteralPath $launcherRoot -Recurse -File -Filter javaw.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -ieq "bin" } |
    ForEach-Object { $_.Directory.FullName } |
    Sort-Object -Unique
)

foreach ($javaBin in $javaBins) {
    $backup = Join-Path $javaBin "_CPU_llvmpipe_original_backup"
    foreach ($name in @("opengl32.dll","opengl32sw.dll","libgallium_wgl.dll","libglapi.dll",
                        "libEGL.dll","libGLESv2.dll","d3dcompiler_47.dll","vulkan-1.dll")) {
        $dst = Join-Path $javaBin $name
        if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Force }
        $src = Join-Path $backup $name
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $dst -Force }
    }
}
Write-Host "Original Java OpenGL files restored where backups existed."
