namespace MinecraftOnOldGPU;

internal static class CliProgram
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            if (args[0].Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                OldGpuCore.RestoreOriginalOpenGl(Console.WriteLine);
                Console.WriteLine("Готово.");
                return 0;
            }

            if (!args[0].Equals("launch", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Неизвестная команда: {args[0]}");
                PrintHelp();
                return 2;
            }

            string src = "320x180";
            string? target = null;
            int threads = Math.Clamp(Environment.ProcessorCount, 1, 8);
            bool scale = true;
            bool stretch = true;
            bool restoreDisplay = false;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--source":
                    case "-s":
                        src = NeedValue(args, ref i);
                        break;
                    case "--target":
                    case "-t":
                        target = NeedValue(args, ref i);
                        break;
                    case "--threads":
                        threads = int.Parse(NeedValue(args, ref i));
                        break;
                    case "--no-scale":
                        scale = false;
                        break;
                    case "--no-stretch":
                        stretch = false;
                        break;
                    case "--keep-display-mode":
                        // Compatibility no-op: display mode is never changed anymore.
                        restoreDisplay = false;
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный параметр: {args[i]}");
                }
            }

            if (!OldGpuCore.TryParseResolution(src, out int sw, out int sh))
                throw new ArgumentException($"Неверное исходное разрешение: {src}");

            int? tw = null, th = null;
            if (scale)
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    var desktop = OldGpuCore.GetDesktopResolution();
                    tw = desktop.Width;
                    th = desktop.Height;
                }
                else
                {
                    if (!OldGpuCore.TryParseResolution(target, out int parsedW, out int parsedH))
                        throw new ArgumentException($"Неверное целевое разрешение: {target}");
                    tw = parsedW;
                    th = parsedH;
                }
            }

            Console.WriteLine("Minecraft On OLD GPU CLI");
            Console.WriteLine($"Source: {sw}x{sh}");
            Console.WriteLine(scale ? $"Target: {tw}x{th}" : "Scaling: off");
            Console.WriteLine(scale ? $"Stretch: {(stretch ? "on" : "off (1:1)")}" : "Stretch: off");
            Console.WriteLine($"llvmpipe threads: {threads}");
            Console.WriteLine();

            var cfg = new LaunchConfig(sw, sh, tw, th, threads, scale, stretch, restoreDisplay);
            await OldGpuCore.LaunchAsync(cfg, Console.WriteLine);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static string NeedValue(string[] args, ref int i)
    {
        if (++i >= args.Length)
            throw new ArgumentException("После параметра отсутствует значение.");
        return args[i];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Minecraft On OLD GPU CLI

Команды:
  launch [параметры]
  restore

Параметры launch:
  -s, --source 320x180       Разрешение рендера Minecraft
  -t, --target 1920x1080     Целевой размер масштабированной картинки
      --threads 4            Потоки llvmpipe
      --no-scale             Не запускать полноэкранный scaler
      --no-stretch           Не растягивать изображение: 1:1 по центру
      --keep-display-mode    Совместимость: теперь режим Windows всегда сохраняется

Примеры:
  MinecraftOnOldGPU.CLI.exe launch -s 320x180
  MinecraftOnOldGPU.CLI.exe launch -s 320x180 -t 1280x720 --threads 4
  MinecraftOnOldGPU.CLI.exe restore
""");
    }
}
