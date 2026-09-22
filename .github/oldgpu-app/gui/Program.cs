using System.Drawing.Drawing2D;

namespace MinecraftOnOldGPU;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly ComboBox _source = new();
    private readonly ComboBox _target = new();
    private readonly NumericUpDown _threads = new();
    private readonly CheckBox _scale = new();
    private readonly CheckBox _stretch = new();
    private readonly CheckBox _restore = new();
    private readonly TextBox _log = new();
    private readonly AccentButton _launch = new();
    private readonly FlatButton _restoreOpenGl = new();
    private readonly Label _status = new();

    private readonly Color Bg = Color.FromArgb(15, 18, 17);
    private readonly Color Card = Color.FromArgb(25, 31, 29);
    private readonly Color Card2 = Color.FromArgb(31, 39, 36);
    private readonly Color TextPrimary = Color.FromArgb(242, 247, 244);
    private readonly Color TextMuted = Color.FromArgb(156, 170, 163);
    private readonly Color Accent = Color.FromArgb(91, 214, 129);

    public MainForm()
    {
        Text = "Minecraft On OLD GPU";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(820, 590);
        BackColor = Bg;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10f);
        DoubleBuffered = true;

        BuildUi();
        LoadDefaults();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(26),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSettingsCard(), 0, 1);
        root.Controls.Add(BuildStatusCard(), 0, 2);
        root.Controls.Add(BuildLogCard(), 0, 3);
        root.Controls.Add(BuildButtons(), 0, 4);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        var icon = new PixelIcon
        {
            Location = new Point(0, 8),
            Size = new Size(58, 58),
            Accent = Accent
        };
        panel.Controls.Add(icon);

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(76, 7),
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI Semibold", 24f),
            Text = "Minecraft On OLD GPU"
        };
        panel.Controls.Add(title);

        var sub = new Label
        {
            AutoSize = true,
            Location = new Point(79, 49),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 10.5f),
            Text = "Mesa llvmpipe CPU renderer • low-resolution render • fullscreen upscale"
        };
        panel.Controls.Add(sub);

        return panel;
    }

    private Control BuildSettingsCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Card,
            Padding = new Padding(22),
            Radius = 16
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Card
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(grid);

        grid.Controls.Add(MakeCaption("Рендер Minecraft — любой W×H"), 0, 0);
        grid.Controls.Add(MakeCaption("Растянуть до — полный каталог"), 2, 0);

        StyleCombo(_source);
        StyleCombo(_target);
        grid.Controls.Add(_source, 0, 1);
        grid.SetColumnSpan(_source, 2);
        grid.Controls.Add(_target, 2, 1);
        grid.SetColumnSpan(_target, 2);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Card
        };

        _scale.Text = "Полноэкранное масштабирование";
        _scale.Checked = true;
        _scale.AutoSize = true;
        _scale.ForeColor = TextPrimary;
        _scale.FlatStyle = FlatStyle.Flat;
        _scale.Margin = new Padding(0, 7, 20, 0);
        _scale.CheckedChanged += (_, _) =>
        {
            UpdateScaleUi();
            if (!_scale.Checked)
            {
                OldGpuCore.StopFullscreenScaler(Log);
                SetStatus("Полноэкранное масштабирование отключено");
            }
        };

        _stretch.Text = "Растягивать изображение";
        _stretch.Checked = true;
        _stretch.AutoSize = true;
        _stretch.ForeColor = TextPrimary;
        _stretch.FlatStyle = FlatStyle.Flat;
        _stretch.Margin = new Padding(0, 7, 20, 0);

        _restore.Text = "Вернуть разрешение после выхода";
        _restore.Checked = true;
        _restore.AutoSize = true;
        _restore.ForeColor = TextPrimary;
        _restore.FlatStyle = FlatStyle.Flat;
        _restore.Margin = new Padding(0, 7, 28, 0);

        var threadLabel = new Label
        {
            AutoSize = true,
            Text = "Потоки CPU:",
            ForeColor = TextMuted,
            Margin = new Padding(0, 8, 8, 0)
        };

        _threads.Minimum = 1;
        _threads.Maximum = 32;
        _threads.Width = 58;
        _threads.BackColor = Card2;
        _threads.ForeColor = TextPrimary;
        _threads.BorderStyle = BorderStyle.FixedSingle;
        _threads.Margin = new Padding(0, 4, 0, 0);

        bottom.Controls.Add(_scale);
        bottom.Controls.Add(_stretch);
        bottom.Controls.Add(_restore);
        bottom.Controls.Add(threadLabel);
        bottom.Controls.Add(_threads);

        grid.Controls.Add(bottom, 0, 2);
        grid.SetColumnSpan(bottom, 4);

        return card;
    }

    private Control BuildStatusCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Card2,
            Padding = new Padding(18, 14, 18, 12),
            Radius = 14
        };

        var dot = new Panel
        {
            Size = new Size(10, 10),
            Location = new Point(18, 18),
            BackColor = Accent
        };
        card.Controls.Add(dot);

        _status.AutoSize = true;
        _status.Location = new Point(38, 12);
        _status.ForeColor = TextPrimary;
        _status.Font = new Font("Segoe UI Semibold", 10.5f);
        _status.Text = "Готово к запуску";
        card.Controls.Add(_status);

        var desc = new Label
        {
            AutoSize = true,
            Location = new Point(38, 36),
            ForeColor = TextMuted,
            Text = "Minecraft рендерится через llvmpipe; IntegerScaler масштабирует окно средствами Windows."
        };
        card.Controls.Add(desc);

        return card;
    }

    private Control BuildLogCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Card,
            Padding = new Padding(16),
            Radius = 14
        };

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(18, 23, 21);
        _log.ForeColor = Color.FromArgb(183, 201, 191);
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Cascadia Mono", 9f);
        card.Controls.Add(_log);

        return card;
    }

    private Control BuildButtons()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = Bg,
            Padding = new Padding(0, 12, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

        _restoreOpenGl.Text = "Восстановить OpenGL";
        _restoreOpenGl.Dock = DockStyle.Fill;
        _restoreOpenGl.Margin = new Padding(0, 0, 12, 0);
        _restoreOpenGl.Click += (_, _) => RestoreOpenGl();
        panel.Controls.Add(_restoreOpenGl, 1, 0);

        _launch.Text = "ЗАПУСТИТЬ MINECRAFT 26.3";
        _launch.Dock = DockStyle.Fill;
        _launch.Margin = new Padding(0);
        _launch.Accent = Accent;
        _launch.Click += async (_, _) => await Launch();
        panel.Controls.Add(_launch, 2, 0);

        return panel;
    }

    private void LoadDefaults()
    {
        _source.Items.Clear();
        foreach (var r in OldGpuCore.GetRenderResolutionPresets())
            _source.Items.Add(r.ToString());

        // Editable field: values below 320x180 are allowed and any custom W×H
        // can be typed even if it is not in the preset list.
        _source.Text = "320x180";

        var desktop = OldGpuCore.GetDesktopResolution();

        _target.Items.Clear();
        foreach (var r in OldGpuCore.GetSupportedDisplayResolutions())
            _target.Items.Add(r.ToString());

        _target.Text = $"{desktop.Width}x{desktop.Height}";

        _threads.Value = Math.Clamp(Environment.ProcessorCount, 1, 8);

        Log($"Desktop: {desktop.Width}x{desktop.Height}");
        Log($"Resolution choices: {_target.Items.Count}");
        Log($"Render presets: {_source.Items.Count}; custom W×H is also accepted");
        Log("Renderer mode: Mesa llvmpipe (CPU only)");
        Log("Fullscreen scaler: IntegerScaler 2.20 (Windows magnification, no frame capture)");
        UpdateScaleUi();
    }

    private async Task Launch()
    {
        if (!OldGpuCore.TryParseResolution(_source.Text, out int sw, out int sh))
        {
            MessageBox.Show(this, "Неверное исходное разрешение. Пример: 320x180.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!OldGpuCore.TryParseResolution(_target.Text, out int tw, out int th))
        {
            MessageBox.Show(this, "Неверное выходное разрешение. Пример: 1920x1080.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ToggleBusy(true);
        try
        {
            SetStatus("Запуск...");
            var cfg = new LaunchConfig(
                sw, sh,
                _scale.Checked ? tw : null,
                _scale.Checked ? th : null,
                (int)_threads.Value,
                _scale.Checked,
                _stretch.Checked,
                _restore.Checked);

            await OldGpuCore.LaunchAsync(cfg, msg =>
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    Log(msg);
                    SetStatus(msg);
                });
            });

            SetStatus("Minecraft запущен");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            SetStatus("Ошибка запуска");
            MessageBox.Show(this, ex.Message, "Minecraft On OLD GPU",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    private void RestoreOpenGl()
    {
        try
        {
            OldGpuCore.RestoreOriginalOpenGl(Log);
            SetStatus("Оригинальный OpenGL восстановлен");
            MessageBox.Show(this, "OpenGL-файлы Java восстановлены из резервных копий.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleBusy(bool busy)
    {
        _launch.Enabled = !busy;
        _restoreOpenGl.Enabled = !busy;
        _source.Enabled = !busy;
        _target.Enabled = !busy;
        _threads.Enabled = !busy;
        _scale.Enabled = !busy;
        _stretch.Enabled = !busy && _scale.Checked;
        _target.Enabled = !busy && _scale.Checked;
        _restore.Enabled = !busy && _scale.Checked;
    }

    private void UpdateScaleUi()
    {
        bool enabled = _scale.Checked;
        _target.Enabled = enabled;
        _stretch.Enabled = enabled;
        _restore.Enabled = enabled;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void Log(string text)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private Label MakeCaption(string text) => new()
    {
        Text = text,
        ForeColor = TextMuted,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Margin = new Padding(0, 2, 0, 0)
    };

    private void StyleCombo(ComboBox cb)
    {
        cb.DropDownStyle = ComboBoxStyle.DropDown;
        cb.FlatStyle = FlatStyle.Flat;
        cb.BackColor = Card2;
        cb.ForeColor = TextPrimary;
        cb.Font = new Font("Segoe UI Semibold", 12f);
        cb.Dock = DockStyle.Fill;
        cb.MaxDropDownItems = 18;
        cb.IntegralHeight = true;
        cb.Margin = new Padding(0, 0, 14, 6);
    }
}

internal sealed class RoundedPanel : Panel
{
    public int Radius { get; set; } = 14;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = RoundedRect(ClientRectangle, Radius);
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal class FlatButton : Button
{
    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
        FlatAppearance.BorderColor = Color.FromArgb(65, 78, 72);
        BackColor = Color.FromArgb(27, 34, 31);
        ForeColor = Color.FromArgb(232, 239, 235);
        Font = new Font("Segoe UI Semibold", 10f);
        Cursor = Cursors.Hand;
    }
}

internal sealed class AccentButton : FlatButton
{
    private Color _accent = Color.FromArgb(91, 214, 129);
    public Color Accent
    {
        get => _accent;
        set
        {
            _accent = value;
            BackColor = value;
            ForeColor = Color.FromArgb(12, 28, 18);
            FlatAppearance.BorderColor = value;
        }
    }
}

internal sealed class PixelIcon : Control
{
    public Color Accent { get; set; } = Color.LimeGreen;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;

        using var dark = new SolidBrush(Color.FromArgb(30, 38, 34));
        using var green = new SolidBrush(Accent);
        e.Graphics.FillRectangle(dark, ClientRectangle);

        int s = Math.Max(4, Width / 8);
        int ox = (Width - 6 * s) / 2;
        int oy = (Height - 6 * s) / 2;

        int[,] map =
        {
            {0,1,1,1,1,0},
            {1,1,0,0,1,1},
            {1,0,1,1,0,1},
            {1,0,1,1,0,1},
            {1,1,0,0,1,1},
            {0,1,1,1,1,0}
        };

        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 6; x++)
                if (map[y, x] == 1)
                    e.Graphics.FillRectangle(green, ox + x * s, oy + y * s, s, s);
    }
}
