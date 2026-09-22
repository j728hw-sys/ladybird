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
    bool RestoreDisplayAfterExit = true);

public static class OldGpuCore
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string LauncherRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ".tlauncher", "legacy", "Minecraft");
    public static string LauncherExe => Path.Combine(LauncherRoot, "TL.exe");
    public static string MesaDir => Path.Combine(AppDir, "runtime", "mesa");
    public static string MagpieDir => Path.Combine(AppDir, "runtime", "magpie");
    public static string MagpieExe => Path.Combine(MagpieDir, "Magpie.exe");

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
        var m = Regex.Match(text.Trim(), @"^\s*(\d{2,5})\s*[xх×]\s*(\d{2,5})\s*$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        width = int.Parse(m.Groups[1].Value);
        height = int.Parse(m.Groups[2].Value);
        return width >= 160 && height >= 90 && width <= 7680 && height <= 4320;
    }

    public static async Task LaunchAsync(LaunchConfig cfg, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };

        ValidateBundle();
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
                        $"Windows не приняла режим {tw}×{th}. Выбери поддерживаемое разрешение.");
            }
        }

        Process? magpie = null;
        try
        {
            if (cfg.FullscreenUpscale)
            {
                if (!File.Exists(MagpieExe))
                    throw new FileNotFoundException($"Magpie не найден: {MagpieExe}");

                var existing = Process.GetProcessesByName("Magpie").FirstOrDefault();
                if (existing is null)
                {
                    log("Запускаю Magpie scaler...");
                    magpie = Process.Start(new ProcessStartInfo(MagpieExe)
                    {
                        WorkingDirectory = MagpieDir,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                    await Task.Delay(2500, ct);
                }
                else
                {
                    magpie = existing;
                    log("Magpie уже запущен.");
                }
            }

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
            var launcher = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить Legacy Launcher.");

            log("Жду окно Minecraft 26.3...");
            IntPtr hwnd = await WaitForMinecraftWindowAsync(TimeSpan.FromMinutes(4), log, ct);

            log($"Нашёл Minecraft. Фиксирую клиентскую область {cfg.SourceWidth}×{cfg.SourceHeight}...");
            ResizeClient(hwnd, cfg.SourceWidth, cfg.SourceHeight);
            await Task.Delay(1200, ct);

            if (cfg.FullscreenUpscale)
            {
                log("Включаю полноэкранное масштабирование Magpie...");
                SetForegroundWindow(hwnd);
                await Task.Delay(350, ct);
                SendAltShiftA();
                log("Готово: Minecraft рендерит низкое разрешение, Magpie растягивает его на экран.");
            }
            else
            {
                log("Готово: запущено без внешнего масштабирования.");
            }

            if (cfg.RestoreDisplayAfterExit && originalMode is not null)
            {
                log("Буду ждать закрытия Minecraft, чтобы вернуть исходное разрешение экрана.");
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

        foreach (var javaw in Directory.EnumerateFiles(LauncherRoot, "javaw.exe", SearchOption.AllDirectories))
        {
            var javaBin = Path.GetDirectoryName(javaw)!;
            var backup = Path.Combine(javaBin, "_CPU_llvmpipe_original_backup");
            if (!Directory.Exists(backup)) continue;

            foreach (var name in ManagedFiles)
            {
                var dst = Path.Combine(javaBin, name);
                if (File.Exists(dst)) File.Delete(dst);

                var src = Path.Combine(backup, name);
                if (File.Exists(src))
                    File.Copy(src, dst, true);
            }

            log($"Восстановлен runtime: {javaBin}");
        }
    }

    private static void ValidateBundle()
    {
        foreach (var name in new[] { "opengl32.dll", "libgallium_wgl.dll" })
        {
            var p = Path.Combine(MesaDir, name);
            if (!File.Exists(p))
                throw new FileNotFoundException($"Не найден Mesa runtime: {p}");
        }
    }

    private static void InstallMesaToLegacyJava(Action<string> log)
    {
        var javaws = Directory.Exists(LauncherRoot)
            ? Directory.EnumerateFiles(LauncherRoot, "javaw.exe", SearchOption.AllDirectories).ToArray()
            : Array.Empty<string>();

        if (javaws.Length == 0)
            throw new InvalidOperationException("В Legacy Launcher не найден ни один javaw.exe.");

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
                if (File.Exists(dst)) File.Delete(dst);
            }

            foreach (var name in new[] { "opengl32.dll", "opengl32sw.dll", "libgallium_wgl.dll", "libglapi.dll" })
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

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var vanilla = Path.Combine(appData, ".minecraft", "options.txt");
        if (File.Exists(vanilla)) files.Add(vanilla);

        if (Directory.Exists(LauncherRoot))
        {
            foreach (var file in Directory.EnumerateFiles(LauncherRoot, "options.txt", SearchOption.AllDirectories))
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
        var idx = lines.FindIndex(x => x.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) lines[idx] = key + value;
        else lines.Add(key + value);
    }

    private static async Task<IntPtr> WaitForMinecraftWindowAsync(
        TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        IntPtr last = IntPtr.Zero;

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            last = FindMinecraftWindow();
            if (last != IntPtr.Zero) return last;
            await Task.Delay(500, ct);
        }

        throw new TimeoutException("Окно Minecraft не появилось за 4 минуты.");
    }

    private static IntPtr FindMinecraftWindow()
    {
        IntPtr result = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;

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
                catch { }
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static void ResizeClient(IntPtr hwnd, int clientW, int clientH)
    {
        GetClientRect(hwnd, out var cr);
        GetWindowRect(hwnd, out var wr);

        int nonClientW = (wr.Right - wr.Left) - (cr.Right - cr.Left);
        int nonClientH = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);

        int outerW = clientW + Math.Max(nonClientW, 0);
        int outerH = clientH + Math.Max(nonClientH, 0);

        SetWindowPos(hwnd, IntPtr.Zero, 20, 20, outerW, outerH,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private static void SendAltShiftA()
    {
        var inputs = new INPUT[]
        {
            KeyDown(VK_MENU),
            KeyDown(VK_SHIFT),
            KeyDown((ushort)'A'),
            KeyUp((ushort)'A'),
            KeyUp(VK_SHIFT),
            KeyUp(VK_MENU)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyDown(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } }
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
    };

    private static DEVMODE GetCurrentDisplayMode()
    {
        var dm = DEVMODE.Create();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            throw new InvalidOperationException("Не удалось прочитать текущий режим экрана.");
        return dm;
    }

    private static bool SetDisplayMode(int width, int height)
    {
        var dm = GetCurrentDisplayMode();
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        return ChangeDisplaySettings(ref dm, CDS_FULLSCREEN) == DISP_CHANGE_SUCCESSFUL;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint CDS_FULLSCREEN = 0x00000004;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const ushort VK_MENU = 0x12;
    private const ushort VK_SHIFT = 0x10;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;

        public static DEVMODE Create()
        {
            var dm = new DEVMODE
            {
                dmDeviceName = new string('\0', 32),
                dmFormName = new string('\0', 32),
                dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
            };
            return dm;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettings(ref DEVMODE devMode, uint flags);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
