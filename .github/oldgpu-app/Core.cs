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
    bool RestoreDisplayAfterExit = true);

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
    {
        var set = new HashSet<(int W, int H)>();

        for (int mode = 0; ; mode++)
        {
            var dm = DEVMODE.Create();
            if (!EnumDisplaySettings(null, mode, ref dm))
                break;

            // Windows 8+ display-mode changes are effectively 32-bpp for modern apps.
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0 && dm.dmBitsPerPel >= 32)
                set.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight));
        }

        var current = GetDesktopResolution();
        set.Add((current.Width, current.Height));

        return set
            .Select(x => new DisplayResolution(x.W, x.H))
            .OrderBy(x => (long)x.Width * x.Height)
            .ThenBy(x => x.Width)
            .ThenBy(x => x.Height)
            .ToArray();
    }

    public static IReadOnlyList<DisplayResolution> GetRenderResolutionPresets()
    {
        var set = new HashSet<(int W, int H)>();

        // Every exact 16:9 size whose width is a multiple of 16, including very low modes.
        // The combo is editable too, so arbitrary W x H remains possible.
        int maxWidth = Math.Max(GetDesktopResolution().Width, 1920);
        maxWidth = Math.Min(maxWidth, 4096);

        for (int w = 16; w <= maxWidth; w += 16)
        {
            int h = w * 9 / 16;
            if (h >= 9)
                set.Add((w, h));
        }

        // Common non-multiple-of-16 modes.
        foreach (var r in new[]
        {
            (80,45), (96,54), (128,72), (160,90), (192,108), (256,144),
            (320,180), (426,240), (480,270), (640,360), (854,480),
            (960,540), (1024,576), (1280,720), (1366,768),
            (1600,900), (1920,1080), (2560,1440), (3840,2160)
        })
            set.Add(r);

        return set
            .Select(x => new DisplayResolution(x.W, x.H))
            .OrderBy(x => (long)x.Width * x.Height)
            .ThenBy(x => x.Width)
            .ToArray();
    }

    public static async Task LaunchAsync(
        LaunchConfig cfg,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        log ??= _ => { };

        ValidateBundle(cfg.FullscreenUpscale);

        // Always terminate scalers left by an older launch first. This makes
        // "Fullscreen scaling = OFF" a real OFF state instead of leaving the
        // previous scaler running.
        StopFullscreenScaler(log);

        if (!File.Exists(LauncherExe))
            throw new FileNotFoundException($"Legacy Launcher не найден: {LauncherExe}");

        log("Подготавливаю Mesa llvmpipe...");
        InstallMesaToLegacyJava(log);

        log($"Ставлю Minecraft {cfg.SourceWidth}×{cfg.SourceHeight}, оконный режим...");
        PatchMinecraftOptions(cfg.SourceWidth, cfg.SourceHeight, log);

        DEVMODE? originalMode = null;

        if (cfg.TargetWidth is int tw && cfg.TargetHeight is int th)
        {
            var desktop = GetDesktopResolution();
            if (desktop.Width != tw || desktop.Height != th)
            {
                originalMode = GetCurrentDisplayMode();
                log($"Переключаю экран {desktop.Width}×{desktop.Height} → {tw}×{th}...");

                if (!SetDisplayMode(tw, th))
                    throw new InvalidOperationException(
                        $"Windows/драйвер не поддерживает режим {tw}×{th}.");
            }
        }

        try
        {
            var psi = new ProcessStartInfo(LauncherExe)
            {
                WorkingDirectory = LauncherRoot,
                UseShellExecute = false
            };

            psi.Environment["GALLIUM_DRIVER"] = "llvmpipe";
            psi.Environment["LIBGL_ALWAYS_SOFTWARE"] = "true";
            psi.Environment["MESA_LOADER_DRIVER_OVERRIDE"] = "llvmpipe";
            psi.Environment["LP_NUM_THREADS"] = Math.Clamp(cfg.LlvmThreads, 1, 32).ToString();
            psi.Environment["PATH"] = $"{MesaDir};{psi.Environment["PATH"]}";

            log($"Запускаю Legacy Launcher. llvmpipe threads: {cfg.LlvmThreads}");

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

            if (cfg.RestoreDisplayAfterExit && originalMode is not null)
            {
                log("Жду закрытия Minecraft, чтобы вернуть исходное разрешение экрана.");

                while (IsWindow(hwnd) && !ct.IsCancellationRequested)
                    await Task.Delay(1000, ct);
            }
        }
        finally
        {
            if (cfg.RestoreDisplayAfterExit && originalMode is DEVMODE dm)
            {
                log("Возвращаю исходное разрешение экрана...");
                ChangeDisplaySettings(ref dm, 0);
            }
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

    private static void ValidateBundle(bool requireScaler)
    {
        foreach (var name in new[] { "opengl32.dll", "libgallium_wgl.dll" })
        {
            var p = Path.Combine(MesaDir, name);

            if (!File.Exists(p))
                throw new FileNotFoundException($"Не найден Mesa runtime: {p}");
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
            "-resize", $"{sourceWidth}x{sourceHeight}"
        };

        if (stretchImage)
        {
            // Allow the window to use the entire available screen area even when the
            // scale is not an integer. For 640x360 -> 1920x1080 this is exactly 3x.
            args.Add("-fractional");
        }
        else
        {
            // Full-screen presentation stays enabled, but the image itself is not
            // enlarged. IntegerScaler centers it at 1:1 on a black background.
            args.Add("-ratio");
            args.Add("1");
        }

        log($"IntegerScaler target: {processPath}");
        log($"IntegerScaler mode: {(stretchImage ? "stretch-to-screen" : "1:1 no stretch")}");

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
