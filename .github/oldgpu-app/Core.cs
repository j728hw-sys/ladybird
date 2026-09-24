using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftOnOldGPU;

public sealed record LaunchConfig(
    int SourceWidth,
    int SourceHeight,
    int? TargetWidth,
    int? TargetHeight,
    int LlvmThreads,
    bool FullscreenUpscale,
    bool StretchImage,
    bool RestoreDisplayAfterExit = true,
    bool NativeIntelGpu = false);

public sealed record DisplayResolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

public static class OldGpuCore
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string LauncherRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ".tlauncher", "legacy", "Minecraft");
    public static string LauncherExe => Path.Combine(LauncherRoot, "TL.exe");
    public static string MesaDir => Path.Combine(AppDir, "runtime", "mesa");
    public static string IntegerScalerDir => Path.Combine(AppDir, "runtime", "integer-scaler");
    public static string IntegerScalerExe => Path.Combine(IntegerScalerDir, "IntegerScaler_64bit.exe");
    public static string NativeIntelAgentJar => Path.Combine(
        AppDir, "runtime", "native-intel", "HD2000NativeAgent.jar");
    public static string NativeIntelLogPath => Path.Combine(AppDir, "HD2000Native.log");

    private static readonly string[] ManagedFiles =
    {
        "opengl32.dll", "opengl32sw.dll", "libgallium_wgl.dll", "libglapi.dll",
        "libEGL.dll", "libGLESv2.dll", "d3dcompiler_47.dll", "vulkan-1.dll"
    };

    public static (int Width, int Height) GetDesktopResolution()
        => (GetSystemMetrics(0), GetSystemMetrics(1));

    public static bool TryParseResolution(string text, out int width, out int height)
    {
        width = height = 0;
        var m = Regex.Match(text.Trim(), @"^\s*(\d{1,5})\s*[xх×]\s*(\d{1,5})\s*$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        width = int.Parse(m.Groups[1].Value);
        height = int.Parse(m.Groups[2].Value);

        // No artificial 320x180 floor. Minecraft/Windows decides the real practical limit.
        return width >= 1 && height >= 1 && width <= 16384 && height <= 16384;
    }

    public static IReadOnlyList<DisplayResolution> GetSupportedDisplayResolutions()
        => BuildResolutionCatalog(includeEveryWindowsMode: true);

    public static IReadOnlyList<DisplayResolution> GetRenderResolutionPresets()
        => BuildResolutionCatalog(includeEveryWindowsMode: true);

    private static IReadOnlyList<DisplayResolution> BuildResolutionCatalog(
        bool includeEveryWindowsMode)
    {
        var set = new HashSet<(int W, int H)>();

        // Every mode the actual Windows display driver reports.
        if (includeEveryWindowsMode)
        {
            for (int mode = 0; ; mode++)
            {
                var dm = DEVMODE.Create();
                if (!EnumDisplaySettings(null, mode, ref dm))
                    break;

                if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                    set.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight));
            }
        }

        var current = GetDesktopResolution();
        set.Add((current.Width, current.Height));

        // Large catalogue of historical + modern PC/video modes. This explicitly
        // includes non-16:9 modes instead of pretending that only widescreen exists.
        foreach (var r in new (int W, int H)[]
        {
            // ultra-low / retro / handheld-like
            (1,1), (16,9), (32,18), (40,30), (48,27), (64,36), (64,48),
            (72,40), (80,45), (80,50), (80,60), (96,54), (96,60), (96,72),
            (112,63), (120,68), (120,90), (128,72), (128,80), (128,96),
            (144,81), (144,90), (144,108), (160,90), (160,100), (160,120),
            (176,99), (176,110), (176,132), (192,108), (192,120), (192,144),
            (200,112), (200,125), (200,150), (224,126), (224,140), (224,168),
            (240,135), (240,150), (240,180), (256,144), (256,160), (256,192),
            (288,162), (288,180), (288,216), (300,168), (300,188), (300,225),
            (320,180), (320,200), (320,240), (352,198), (352,220), (352,240),
            (360,202), (360,225), (360,240), (360,270), (384,216), (384,240),
            (384,288), (400,225), (400,240), (400,250), (400,300),
            (416,234), (416,260), (416,312), (426,240), (432,243), (432,270),
            (432,324), (448,252), (448,280), (448,336), (480,270), (480,300),
            (480,320), (480,360), (512,288), (512,320), (512,384),
            (540,304), (540,338), (540,405), (560,315), (560,350), (560,420),
            (576,324), (576,360), (576,432), (600,338), (600,375), (600,450),

            // classic PC / DOS / VGA / SVGA / XGA and widescreen variants
            (640,350), (640,360), (640,400), (640,480),
            (720,400), (720,405), (720,450), (720,480), (720,540), (720,576),
            (768,432), (768,480), (768,576), (768,600),
            (800,450), (800,480), (800,500), (800,600),
            (832,468), (832,520), (832,624),
            (848,480), (852,480), (854,480),
            (864,486), (864,540), (864,648),
            (896,504), (896,560), (896,672),
            (900,506), (900,562), (900,675),
            (960,540), (960,600), (960,640), (960,720),
            (1024,576), (1024,600), (1024,640), (1024,768),
            (1080,607), (1080,675), (1080,720), (1080,810),
            (1120,630), (1120,700), (1120,840),
            (1152,648), (1152,720), (1152,768), (1152,864), (1152,868),
            (1176,664), (1200,675), (1200,750), (1200,800), (1200,900),

            // HD-era desktop/notebook modes
            (1280,720), (1280,768), (1280,800), (1280,854), (1280,960), (1280,1024),
            (1296,729), (1296,810), (1296,972),
            (1360,768), (1366,768), (1368,768),
            (1400,788), (1400,875), (1400,900), (1400,1050),
            (1440,810), (1440,900), (1440,960), (1440,1080),
            (1536,864), (1536,960), (1536,1024), (1536,1152),
            (1600,900), (1600,1000), (1600,1024), (1600,1200),
            (1680,945), (1680,1050), (1680,1200),
            (1760,990), (1768,992),
            (1792,1008), (1792,1120), (1792,1344),
            (1800,1012), (1800,1125), (1800,1200), (1800,1350),
            (1856,1392),
            (1920,1080), (1920,1200), (1920,1280), (1920,1440),
            (2048,1080), (2048,1152), (2048,1280), (2048,1536),
            (2160,1215), (2160,1350), (2160,1440), (2160,1620),
            (2304,1296), (2304,1440), (2304,1728),
            (2560,1080), (2560,1440), (2560,1600), (2560,1700), (2560,1920),
            (2736,1824), (2880,1620), (2880,1800), (2880,1920), (2880,2160),
            (3000,2000), (3200,1800), (3200,2000), (3200,2400),
            (3440,1440), (3840,1080), (3840,1600), (3840,2160), (3840,2400),
            (4096,2160), (4096,2304), (4096,2560), (4096,3072),
            (5120,1440), (5120,2160), (5120,2880), (5120,3200),
            (6016,3384), (6144,3456), (7680,2160), (7680,4320)
        })
        {
            set.Add(r);
        }

        // Also generate dense choices for the major aspect ratios, so the list is
        // not limited to a hand-picked set. Widths are generated every 8 pixels.
        // The edit box still accepts any exact W×H, including odd/custom values.
        var ratios = new (int X, int Y)[]
        {
            (1,1), (5,4), (4,3), (3,2), (16,10), (15,9), (16,9),
            (17,9), (18,9), (19,9), (39,18), (20,9), (21,9), (32,9)
        };

        for (int w = 64; w <= 7680; w += 8)
        {
            foreach (var (x, y) in ratios)
            {
                int h = (int)Math.Round(w * (double)y / x);
                if (h >= 1 && h <= 4320)
                    set.Add((w, h));
            }
        }

        // Generate from heights too; this catches standard sizes whose width does
        // not fall on the 8-pixel grid used above.
        for (int h = 36; h <= 4320; h += 8)
        {
            foreach (var (x, y) in ratios)
            {
                int w = (int)Math.Round(h * (double)x / y);
                if (w >= 1 && w <= 7680)
                    set.Add((w, h));
            }
        }

        return set
            .Where(x => x.W >= 1 && x.H >= 1)
            .Select(x => new DisplayResolution(x.W, x.H))
            // Human-readable order: width first, then height.
            // This keeps all 640x... entries together, then 648x..., 656x..., etc.
            .OrderBy(x => x.Width)
            .ThenBy(x => x.Height)
            .ToArray();
    }

    public static async Task LaunchAsync(
        LaunchConfig cfg,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        log ??= _ => { };

        ValidateBundle(cfg.FullscreenUpscale, cfg.NativeIntelGpu);

        // Always terminate scalers left by an older launch first. This makes
        // "Fullscreen scaling = OFF" a real OFF state instead of leaving the
        // previous scaler running.
        StopFullscreenScaler(log);

        if (!File.Exists(LauncherExe))
            throw new FileNotFoundException($"Legacy Launcher не найден: {LauncherExe}");

        if (cfg.NativeIntelGpu)
        {
            log("Нативный режим Intel HD 2000: возвращаю оригинальный OpenGL драйвер Java...");
            RestoreOriginalOpenGl(log);
            log("Mesa llvmpipe отключён; рендер должен идти через родной Intel OpenGL.");
        }
        else
        {
            log("Подготавливаю Mesa llvmpipe...");
            InstallMesaToLegacyJava(log);
        }

        log($"Ставлю Minecraft {cfg.SourceWidth}×{cfg.SourceHeight}, оконный режим...");
        PatchMinecraftOptions(cfg.SourceWidth, cfg.SourceHeight, log);

        // IMPORTANT: never change the Windows display mode for fullscreen upscale.
        // Minecraft must keep rendering into its small window; the scaler only
        // magnifies that already rendered low-resolution image.
        if (cfg.FullscreenUpscale)
        {
            var desktop = GetDesktopResolution();
            int targetW = cfg.TargetWidth ?? desktop.Width;
            int targetH = cfg.TargetHeight ?? desktop.Height;
            log($"Windows остаётся {desktop.Width}×{desktop.Height}; low-res кадр будет масштабирован до области {targetW}×{targetH}.");
        }

            var psi = new ProcessStartInfo(LauncherExe)
            {
                WorkingDirectory = LauncherRoot,
                UseShellExecute = false
            };

            if (cfg.NativeIntelGpu)
            {
                // Do not let stale software-renderer variables leak into native mode.
                psi.Environment.Remove("GALLIUM_DRIVER");
                psi.Environment.Remove("LIBGL_ALWAYS_SOFTWARE");
                psi.Environment.Remove("MESA_LOADER_DRIVER_OVERRIDE");
                psi.Environment.Remove("LP_NUM_THREADS");

                try
                {
                    File.WriteAllText(
                        NativeIntelLogPath,
                        $"Minecraft On OLD GPU — native Intel diagnostic log{Environment.NewLine}" +
                        $"Started: {DateTime.Now:O}{Environment.NewLine}" +
                        $"EXE directory: {AppDir}{Environment.NewLine}{Environment.NewLine}",
                        new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    log($"Не удалось создать диагностический лог: {ex.Message}");
                }

                psi.Environment["OLDGPU_NATIVE_LOG"] = NativeIntelLogPath;

                string agentOption = $"-javaagent:\"{NativeIntelAgentJar}\"";
                string? existingJavaOptions = psi.Environment.TryGetValue("_JAVA_OPTIONS", out var javaOptions)
                    ? javaOptions
                    : null;
                psi.Environment["_JAVA_OPTIONS"] = string.IsNullOrWhiteSpace(existingJavaOptions)
                    ? agentOption
                    : existingJavaOptions + " " + agentOption;

                log("Запускаю Legacy Launcher: НАТИВНЫЙ Intel HD 2000 / OpenGL 3.1 compatibility agent.");
                log("CPU llvmpipe в этом режиме не используется.");
                log($"Native log: {NativeIntelLogPath}");
            }
            else
            {
                psi.Environment.Remove("OLDGPU_NATIVE_LOG");
                psi.Environment["GALLIUM_DRIVER"] = "llvmpipe";
                psi.Environment["LIBGL_ALWAYS_SOFTWARE"] = "true";
                psi.Environment["MESA_LOADER_DRIVER_OVERRIDE"] = "llvmpipe";
                psi.Environment["LP_NUM_THREADS"] = Math.Clamp(cfg.LlvmThreads, 1, 32).ToString();
                psi.Environment["PATH"] = $"{MesaDir};{psi.Environment["PATH"]}";

                log($"Запускаю Legacy Launcher. llvmpipe threads: {cfg.LlvmThreads}");
            }

            _ = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить Legacy Launcher.");

            log("Жду окно Minecraft 26.3...");
            IntPtr hwnd = await WaitForMinecraftWindowAsync(TimeSpan.FromMinutes(4), log, ct);

            log($"Нашёл Minecraft. Фиксирую клиентскую область {cfg.SourceWidth}×{cfg.SourceHeight}...");

            // Minecraft/GLFW may update its window once during startup. Re-apply a few times.
            for (int i = 0; i < 3; i++)
            {
                ResizeClient(hwnd, cfg.SourceWidth, cfg.SourceHeight);
                await Task.Delay(350, ct);
            }

            if (cfg.FullscreenUpscale)
            {
                log("Готовлю IntegerScaler fullscreen...");
                await StartIntegerScalerAsync(
                    hwnd,
                    cfg.SourceWidth,
                    cfg.SourceHeight,
                    cfg.TargetWidth,
                    cfg.TargetHeight,
                    cfg.StretchImage,
                    log,
                    ct);

                log(cfg.StretchImage
                    ? "IntegerScaler включён: низкое окно растягивается на экран."
                    : "IntegerScaler включён без растягивания: изображение 1:1 по центру.");
            }
            else
            {
                // This path deliberately leaves no scaler process alive.
                StopFullscreenScaler(log);
                log("Полноэкранное масштабирование отключено: обычное низкое окно Minecraft.");
            }

    }

    public static void RestoreOriginalOpenGl(Action<string>? log = null)
    {
        log ??= _ => { };

        if (!Directory.Exists(LauncherRoot))
            throw new DirectoryNotFoundException(LauncherRoot);

        foreach (var javaw in Directory.EnumerateFiles(
                     LauncherRoot, "javaw.exe", SearchOption.AllDirectories))
        {
            var javaBin = Path.GetDirectoryName(javaw)!;
            var backup = Path.Combine(javaBin, "_CPU_llvmpipe_original_backup");

            if (!Directory.Exists(backup))
                continue;

            foreach (var name in ManagedFiles)
            {
                var dst = Path.Combine(javaBin, name);

                if (File.Exists(dst))
                    File.Delete(dst);

                var src = Path.Combine(backup, name);

                if (File.Exists(src))
                    File.Copy(src, dst, true);
            }

            log($"Восстановлен runtime: {javaBin}");
        }
    }

    private static void ValidateBundle(bool requireScaler, bool nativeIntelGpu)
    {
        if (nativeIntelGpu)
        {
            if (!File.Exists(NativeIntelAgentJar))
                throw new FileNotFoundException(
                    $"Не найден модуль нативного Intel HD 2000: {NativeIntelAgentJar}");
        }
        else
        {
            foreach (var name in new[] { "opengl32.dll", "libgallium_wgl.dll" })
            {
                var p = Path.Combine(MesaDir, name);

                if (!File.Exists(p))
                    throw new FileNotFoundException($"Не найден Mesa runtime: {p}");
            }
        }

        if (requireScaler && !File.Exists(IntegerScalerExe))
            throw new FileNotFoundException($"Не найден IntegerScaler: {IntegerScalerExe}");
    }

    private static void InstallMesaToLegacyJava(Action<string> log)
    {
        var javaws = Directory.Exists(LauncherRoot)
            ? Directory.EnumerateFiles(
                LauncherRoot, "javaw.exe", SearchOption.AllDirectories).ToArray()
            : Array.Empty<string>();

        if (javaws.Length == 0)
            throw new InvalidOperationException(
                "В Legacy Launcher не найден ни один javaw.exe.");

        foreach (var javaw in javaws)
        {
            var javaBin = Path.GetDirectoryName(javaw)!;
            var backup = Path.Combine(javaBin, "_CPU_llvmpipe_original_backup");
            var mobileBackup = Path.Combine(javaBin, "_MobileGL_original_backup");

            Directory.CreateDirectory(backup);

            foreach (var name in ManagedFiles)
            {
                var backupFile = Path.Combine(backup, name);

                if (!File.Exists(backupFile))
                {
                    var originalFromMobile = Path.Combine(mobileBackup, name);
                    var current = Path.Combine(javaBin, name);

                    if (File.Exists(originalFromMobile))
                        File.Copy(originalFromMobile, backupFile, true);
                    else if (File.Exists(current))
                        File.Copy(current, backupFile, true);
                }
            }

            foreach (var name in ManagedFiles)
            {
                var dst = Path.Combine(javaBin, name);

                if (File.Exists(dst))
                    File.Delete(dst);
            }

            foreach (var name in new[]
                     {
                         "opengl32.dll", "opengl32sw.dll",
                         "libgallium_wgl.dll", "libglapi.dll"
                     })
            {
                var src = Path.Combine(MesaDir, name);

                if (File.Exists(src))
                    File.Copy(src, Path.Combine(javaBin, name), true);
            }

            log($"Mesa установлен в: {javaBin}");
        }
    }

    private static void PatchMinecraftOptions(int w, int h, Action<string> log)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);

        var vanilla = Path.Combine(appData, ".minecraft", "options.txt");

        if (File.Exists(vanilla))
            files.Add(vanilla);

        if (Directory.Exists(LauncherRoot))
        {
            foreach (var file in Directory.EnumerateFiles(
                         LauncherRoot, "options.txt", SearchOption.AllDirectories))
                files.Add(file);
        }

        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8).ToList();

                Upsert(lines, "overrideWidth:", w.ToString());
                Upsert(lines, "overrideHeight:", h.ToString());
                Upsert(lines, "fullscreen:", "false");
                Upsert(lines, "exclusiveFullscreen:", "false");

                File.WriteAllLines(file, lines, new UTF8Encoding(false));

                log($"options.txt: {w}×{h} → {file}");
            }
            catch (Exception ex)
            {
                log($"Не удалось изменить {file}: {ex.Message}");
            }
        }
    }

    private static void Upsert(List<string> lines, string key, string value)
    {
        var idx = lines.FindIndex(
            x => x.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0)
            lines[idx] = key + value;
        else
            lines.Add(key + value);
    }

    private static async Task<IntPtr> WaitForMinecraftWindowAsync(
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            IntPtr hwnd = FindMinecraftWindow();

            if (hwnd != IntPtr.Zero)
                return hwnd;

            await Task.Delay(500, ct);
        }

        throw new TimeoutException("Окно Minecraft не появилось за 4 минуты.");
    }

    private static IntPtr FindMinecraftWindow()
    {
        IntPtr result = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            int len = GetWindowTextLength(hwnd);

            if (len <= 0)
                return true;

            var sb = new StringBuilder(len + 1);

            GetWindowText(hwnd, sb, sb.Capacity);

            var title = sb.ToString();

            if (title.Contains("Minecraft 26.3", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);

                try
                {
                    var p = Process.GetProcessById((int)pid);

                    if (p.ProcessName.Contains("java", StringComparison.OrdinalIgnoreCase))
                    {
                        result = hwnd;
                        return false;
                    }
                }
                catch
                {
                    // Process may have disappeared while enumerating.
                }
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetWindowProcessPath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);

        if (pid == 0)
            throw new InvalidOperationException("Не удалось получить PID Minecraft.");

        using var p = Process.GetProcessById((int)pid);

        string? path = p.MainModule?.FileName;

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "Не удалось получить путь javaw.exe процесса Minecraft.");

        return Path.GetFullPath(path);
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);

        if (GetClassName(hwnd, sb, sb.Capacity) <= 0)
            throw new InvalidOperationException(
                "Не удалось получить Win32 class окна Minecraft.");

        return sb.ToString();
    }

    public static void StopFullscreenScaler(Action<string>? log = null)
    {
        log ??= _ => { };

        foreach (var name in new[] { "IntegerScaler_64bit", "IntegerScaler_32bit", "Magpie" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    log($"Останавливаю scaler {p.ProcessName} PID {p.Id}...");
                    p.Kill(true);
                    p.WaitForExit(3000);
                }
                catch
                {
                    // Best effort: stale or elevated process may disappear between
                    // enumeration and termination.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    private static async Task StartIntegerScalerAsync(
        IntPtr hwnd,
        int sourceWidth,
        int sourceHeight,
        int? targetWidth,
        int? targetHeight,
        bool stretchImage,
        Action<string> log,
        CancellationToken ct)
    {
        StopFullscreenScaler(log);

        string processPath = GetWindowProcessPath(hwnd);
        Directory.CreateDirectory(IntegerScalerDir);

        // IntegerScaler can auto-scale an app identified by its exact executable path.
        // This is more reliable than synthesizing Alt+F11.
        string autoPath = Path.Combine(IntegerScalerDir, "auto.txt");
        File.WriteAllText(autoPath, processPath + Environment.NewLine, new UTF8Encoding(false));

        var args = new List<string>
        {
            "-locale", "ru",
            "-nohotkeys",
            // -resize only makes the Minecraft client area match the low render size.
            // IntegerScaler's own documentation explicitly says this option is NOT
            // what enables scaling.
            "-resize", $"{sourceWidth}x{sourceHeight}"
        };

        var desktop = GetDesktopResolution();
        int requestedTargetW = targetWidth ?? desktop.Width;
        int requestedTargetH = targetHeight ?? desktop.Height;

        if (stretchImage)
        {
            double ratioX = requestedTargetW / (double)sourceWidth;
            double ratioY = requestedTargetH / (double)sourceHeight;
            double ratio = Math.Min(ratioX, ratioY);

            if (ratio < 1.0)
                throw new InvalidOperationException(
                    $"Целевой размер {requestedTargetW}×{requestedTargetH} меньше исходного {sourceWidth}×{sourceHeight}.");

            args.Add("-ratio");
            args.Add(ratio.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add("-ratio");
            args.Add("1");
        }

        // This is the option that actually applies IntegerScaler scaling.
        // Without it the old build only resized the Minecraft window.
        args.Add("-scale");

        log($"IntegerScaler target process: {processPath}");
        log($"Minecraft render/client: {sourceWidth}×{sourceHeight}");
        log($"Windows desktop stays: {desktop.Width}×{desktop.Height}");
        log($"Requested scaled image area: {requestedTargetW}×{requestedTargetH}");
        log($"IntegerScaler mode: {(stretchImage ? "scale low-res image" : "1:1 centered")}");

        // Make Minecraft the foreground app before IntegerScaler starts. The auto.txt
        // entry then keeps automatic scaling tied to this javaw.exe path.
        TryActivateWindow(hwnd);
        await Task.Delay(250, ct);

        var psi = new ProcessStartInfo(IntegerScalerExe)
        {
            WorkingDirectory = IntegerScalerDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        _ = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить IntegerScaler.");

        await Task.Delay(900, ct);
        TryActivateWindow(hwnd);

        // Do not block the launcher UI for the whole game session. Clean up the
        // scaler after Minecraft closes.
        _ = Task.Run(async () =>
        {
            try
            {
                while (IsWindow(hwnd))
                    await Task.Delay(1000);

                StopFullscreenScaler();
            }
            catch
            {
                // Process shutdown cleanup is best-effort.
            }
        });
    }

    private static void ResizeClient(IntPtr hwnd, int clientW, int clientH)
    {
        GetClientRect(hwnd, out var cr);
        GetWindowRect(hwnd, out var wr);

        int nonClientW = (wr.Right - wr.Left) - (cr.Right - cr.Left);
        int nonClientH = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);

        int outerW = clientW + Math.Max(nonClientW, 0);
        int outerH = clientH + Math.Max(nonClientH, 0);

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            20,
            20,
            outerW,
            outerH,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    private static void TryActivateWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd))
            return;

        ShowWindow(hwnd, SW_RESTORE);
        BringWindowToTop(hwnd);

        IntPtr foreground = GetForegroundWindow();

        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0;

        bool attachedForeground = false;
        bool attachedTarget = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(
                    currentThread, foregroundThread, true);

            if (targetThread != 0 && targetThread != currentThread)
                attachedTarget = AttachThreadInput(
                    currentThread, targetThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);

            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static DEVMODE GetCurrentDisplayMode()
    {
        var dm = DEVMODE.Create();

        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            throw new InvalidOperationException(
                "Не удалось прочитать текущий режим экрана.");

        return dm;
    }

    private static bool SetDisplayMode(int width, int height)
    {
        DEVMODE? best = null;

        for (int mode = 0; ; mode++)
        {
            var dm = DEVMODE.Create();

            if (!EnumDisplaySettings(null, mode, ref dm))
                break;

            if (dm.dmBitsPerPel < 32)
                continue;

            if (dm.dmPelsWidth != (uint)width ||
                dm.dmPelsHeight != (uint)height)
                continue;

            if (best is null ||
                dm.dmDisplayFrequency > best.Value.dmDisplayFrequency)
                best = dm;
        }

        if (best is null)
            return false;

        var selected = best.Value;

        // Use an actual DEVMODE returned by EnumDisplaySettings, as Win32
        // documentation recommends for supported dynamic mode changes.
        return ChangeDisplaySettings(ref selected, CDS_FULLSCREEN)
            == DISP_CHANGE_SUCCESSFUL;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_FULLSCREEN = 0x00000004;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;

        public static DEVMODE Create()
        {
            return new DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string? deviceName,
        int modeNum,
        ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(
        ref DEVMODE devMode,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hWnd,
        StringBuilder text,
        int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr hWnd,
        StringBuilder lpClassName,
        int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(
        IntPtr hWnd,
        out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(
        IntPtr hWnd,
        out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
