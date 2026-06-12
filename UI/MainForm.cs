using EarShare.Audio;
using EarShare.Settings;

namespace EarShare.UI;

public sealed class MainForm : Form
{
    private readonly MirrorEngine engine = new();
    private readonly AppSettings settings;

    private readonly Panel topPanel;
    private readonly Panel bottomPanel;
    private readonly ComboBox captureCombo;
    private readonly Label statusLabel;
    private readonly FlowLayoutPanel deviceList;
    private readonly Button addButton;
    private readonly Button startButton;
    private readonly NumericUpDown bufferBox;

    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem trayToggle;
    private readonly Icon appIcon = LoadAppIcon();
    private readonly Icon idleIcon = TrayIconFactory.Create(active: false);
    private readonly Icon activeIcon = TrayIconFactory.Create(active: true);

    private readonly System.Windows.Forms.Timer uiTimer;
    private bool suppressCaptureChange;
    private bool settingsDirty;
    private bool trayHintShown;
    private bool exiting;

    private IEnumerable<DeviceRow> Rows => deviceList.Controls.OfType<DeviceRow>();

    public MainForm()
    {
        settings = AppSettings.Load();

        Text = "EarShare";
        Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedSingle; // height is managed automatically
        MaximizeBox = false;
        ClientSize = new Size(470, 300);

        // --- top: capture source + status ---
        topPanel = new Panel { Dock = DockStyle.Top, Height = 64 };
        var mirrorFromLabel = new Label { Text = "Mirror from:", Location = new Point(10, 12), AutoSize = true };
        captureCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(92, 8),
        };
        captureCombo.DropDown += (_, _) => PopulateCaptureCombo();
        captureCombo.SelectedIndexChanged += (_, _) => OnCaptureSelectionChanged();
        statusLabel = new Label
        {
            Location = new Point(10, 40),
            Height = 18,
            Text = "Stopped — add output devices, then press Start.",
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
        };
        topPanel.Controls.AddRange(new Control[] { mirrorFromLabel, captureCombo, statusLabel });
        topPanel.Resize += (_, _) =>
        {
            captureCombo.Width = topPanel.ClientSize.Width - captureCombo.Left - 10;
            statusLabel.Width = topPanel.ClientSize.Width - 20;
        };

        // --- middle: device rows ---
        deviceList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(6, 2, 6, 2),
        };
        deviceList.ClientSizeChanged += (_, _) => ResizeRows();

        // --- bottom: actions + buffer setting ---
        bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        addButton = new Button { Text = "Add device  ▾", Bounds = new Rectangle(10, 9, 120, 30) };
        addButton.Click += (_, _) => ShowAddDeviceMenu();
        var bufferLabel = new Label { Text = "Buffer:", Location = new Point(142, 15), AutoSize = true };
        bufferBox = new NumericUpDown
        {
            Minimum = OutputPipeline.MinBufferMs,
            Maximum = OutputPipeline.MaxBufferMs,
            Increment = 10,
            Value = Math.Clamp(settings.BufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs),
            Location = new Point(190, 12),
            Width = 56,
            TextAlign = HorizontalAlignment.Right,
        };
        bufferBox.ValueChanged += (_, _) => OnBufferChanged();
        var bufferMsLabel = new Label { Text = "ms", Location = new Point(248, 15), AutoSize = true, ForeColor = SystemColors.GrayText };
        var bufferTip = new ToolTip();
        string bufferHint = "Audio queued per device before playback. Lower = tighter lip-sync; raise it if you hear crackling or dropouts.";
        bufferTip.SetToolTip(bufferBox, bufferHint);
        bufferTip.SetToolTip(bufferLabel, bufferHint);
        startButton = new Button { Text = "Start", Bounds = new Rectangle(0, 9, 120, 30) };
        startButton.Click += (_, _) => ToggleMirroring();
        bottomPanel.Controls.AddRange(new Control[] { addButton, bufferLabel, bufferBox, bufferMsLabel, startButton });
        bottomPanel.Resize += (_, _) => startButton.Left = bottomPanel.ClientSize.Width - startButton.Width - 10;

        Controls.Add(deviceList);   // Dock=Fill must be first in the collection
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);

        // --- tray ---
        trayToggle = new ToolStripMenuItem("Start mirroring", null, (_, _) => ToggleMirroring());
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(new ToolStripMenuItem("Open EarShare", null, (_, _) => RestoreFromTray()));
        trayMenu.Items.Add(trayToggle);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => ExitApp()));
        tray = new NotifyIcon
        {
            Icon = idleIcon,
            Text = "EarShare — stopped",
            Visible = true,
            ContextMenuStrip = trayMenu,
        };
        tray.DoubleClick += (_, _) => RestoreFromTray();

        // --- engine events (fire on audio threads; marshal to UI) ---
        engine.OutputFailed += (pipeline, _) => SafeInvoke(() =>
        {
            Rows.FirstOrDefault(r => r.DeviceId == pipeline.DeviceId)?.SetState("device lost", error: true);
            tray.ShowBalloonTip(3000, "EarShare",
                $"Output lost: {pipeline.FriendlyName}. The other devices keep playing.", ToolTipIcon.Warning);
        });
        engine.CaptureStopped += _ => SafeInvoke(() =>
        {
            engine.Stop();
            SetUiStopped();
            tray.ShowBalloonTip(4000, "EarShare",
                "Mirroring stopped — the capture device changed or disappeared.", ToolTipIcon.Warning);
        });

        // --- restore saved state ---
        var activeDevices = MirrorEngine.ListRenderDevices().Select(d => d.Id).ToHashSet();
        foreach (var saved in settings.Devices.ToList())
        {
            var row = AddDeviceRow(saved.Id, saved.Name, saved.Volume, saved.DelayMs, save: false, attachIfRunning: false);
            if (row != null && !activeDevices.Contains(saved.Id))
                row.SetState("not connected", error: false);
        }
        PopulateCaptureCombo();
        UpdateWindowHeight();

        uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        uiTimer.Tick += (_, _) => OnUiTimerTick();
        uiTimer.Start();
    }

    // ---------------------------------------------------------------- capture source

    private sealed class CaptureChoice
    {
        public string? Id { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    private void PopulateCaptureCombo()
    {
        suppressCaptureChange = true;
        try
        {
            var devices = MirrorEngine.ListRenderDevices();
            string? defaultId = MirrorEngine.GetDefaultRenderId();
            string defaultName = devices.FirstOrDefault(d => d.Id == defaultId).Name ?? "no device";

            captureCombo.BeginUpdate();
            captureCombo.Items.Clear();
            captureCombo.Items.Add(new CaptureChoice { Id = null, Text = $"System default  ({defaultName})" });
            int selectIndex = 0;
            foreach (var (id, name) in devices)
            {
                captureCombo.Items.Add(new CaptureChoice { Id = id, Text = name });
                if (settings.CaptureDeviceId == id)
                    selectIndex = captureCombo.Items.Count - 1;
            }
            captureCombo.SelectedIndex = selectIndex;
            captureCombo.EndUpdate();
        }
        finally
        {
            suppressCaptureChange = false;
        }
    }

    private void OnCaptureSelectionChanged()
    {
        if (suppressCaptureChange)
            return;
        settings.CaptureDeviceId = (captureCombo.SelectedItem as CaptureChoice)?.Id;
        SaveSettings();
        if (engine.IsRunning)
        {
            // switch the source live: tear down and rebuild with the same outputs
            engine.Stop();
            foreach (var row in Rows)
                row.SetState("", error: false);
            StartMirroring();
        }
    }

    // ---------------------------------------------------------------- mirroring

    private void ToggleMirroring()
    {
        if (engine.IsRunning)
        {
            engine.Stop();
            SetUiStopped();
        }
        else
        {
            StartMirroring();
        }
    }

    private void StartMirroring()
    {
        var rows = Rows.ToList();
        if (rows.Count == 0)
        {
            RestoreFromTray();
            MessageBox.Show(this, "Add at least one output device first.",
                "EarShare", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var requests = rows.Select(r => new OutputRequest(r.DeviceId, r.VolumeScalar, r.DelayMs)).ToList();
        var statuses = engine.Start(requests, settings.CaptureDeviceId, settings.BufferMs);
        foreach (var status in statuses)
            rows.FirstOrDefault(r => r.DeviceId == status.DeviceId)?.SetState(status.Detail, !status.Ok);

        if (engine.IsRunning)
        {
            var format = engine.CaptureFormat!;
            statusLabel.Text = $"Mirroring from: {engine.CaptureDeviceName}  ({format.SampleRate / 1000.0:0.#} kHz, {format.Channels} ch)";
            statusLabel.ForeColor = Color.FromArgb(0, 130, 60);
            startButton.Text = "Stop";
        }
        else
        {
            statusLabel.Text = "Could not start — no usable output device (see device status).";
            statusLabel.ForeColor = Color.Firebrick;
        }
        UpdateTray();
    }

    private void SetUiStopped()
    {
        startButton.Text = "Start";
        statusLabel.Text = "Stopped — add output devices, then press Start.";
        statusLabel.ForeColor = SystemColors.GrayText;
        foreach (var row in Rows)
            row.SetState("", error: false);
        UpdateTray();
    }

    private void UpdateTray()
    {
        // window/taskbar keeps the static logo (appIcon); the tray icon doubles
        // as a status light: gray = stopped, green = mirroring
        bool running = engine.IsRunning;
        trayToggle.Text = running ? "Stop mirroring" : "Start mirroring";
        tray.Icon = running ? activeIcon : idleIcon;
        tray.Text = running ? "EarShare — mirroring" : "EarShare — stopped";
    }

    private void OnUiTimerTick()
    {
        if (settingsDirty)
        {
            settingsDirty = false;
            SaveSettings();
        }
        if (!engine.IsRunning)
            return;
        foreach (var row in Rows)
            if (engine.TryGetOutputInfo(row.DeviceId, out int rate, out double ms))
                row.SetState($"{rate / 1000.0:0.#} kHz • buffer {ms:0} ms", error: false);
    }

    // ---------------------------------------------------------------- device rows

    private void ShowAddDeviceMenu()
    {
        var menu = new ContextMenuStrip();
        string? captureId = engine.IsRunning ? engine.CaptureDeviceId
            : settings.CaptureDeviceId ?? MirrorEngine.GetDefaultRenderId();

        foreach (var (id, name) in MirrorEngine.ListRenderDevices())
        {
            if (Rows.Any(r => r.DeviceId == id))
                continue;
            bool isCapture = id == captureId;
            var item = new ToolStripMenuItem(isCapture ? name + "   — capture source" : name);
            string deviceId = id, deviceName = name;
            item.Click += (_, _) => AddDeviceRow(deviceId, deviceName, 100, 0, save: true, attachIfRunning: true);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("(no further active output devices found)") { Enabled = false });

        menu.Show(addButton, new Point(0, addButton.Height));
    }

    private DeviceRow? AddDeviceRow(string id, string name, int volumePercent, int delayMs, bool save, bool attachIfRunning)
    {
        if (Rows.Any(r => r.DeviceId == id))
            return null;

        var row = new DeviceRow(id, name, volumePercent, delayMs);
        row.RemoveClicked += OnRowRemoveClicked;
        row.VolumeChanged += OnRowVolumeChanged;
        row.DelayChanged += OnRowDelayChanged;
        deviceList.Controls.Add(row);
        ResizeRows();
        UpdateWindowHeight();

        if (attachIfRunning && engine.IsRunning)
        {
            var status = engine.AddOutput(new OutputRequest(id, row.VolumeScalar, row.DelayMs));
            row.SetState(status.Detail, !status.Ok);
        }
        if (save)
        {
            SyncSettingsFromRows();
            SaveSettings();
        }
        return row;
    }

    private void OnRowRemoveClicked(DeviceRow row)
    {
        engine.RemoveOutput(row.DeviceId);
        deviceList.Controls.Remove(row);
        row.Dispose();
        UpdateWindowHeight();
        SyncSettingsFromRows();
        SaveSettings();
    }

    private void OnRowVolumeChanged(DeviceRow row)
    {
        if (engine.IsRunning)
            engine.SetVolume(row.DeviceId, row.VolumeScalar);
        SyncSettingsFromRows();
        settingsDirty = true; // flushed by the UI timer, avoids a disk write per slider tick
    }

    private void OnRowDelayChanged(DeviceRow row)
    {
        engine.SetOutputDelay(row.DeviceId, row.DelayMs);
        SyncSettingsFromRows();
        settingsDirty = true;
    }

    private void OnBufferChanged()
    {
        settings.BufferMs = (int)bufferBox.Value;
        engine.SetBufferTarget(settings.BufferMs); // applies live if mirroring
        settingsDirty = true;
    }

    private void ResizeRows()
    {
        int width = deviceList.ClientSize.Width - 16;
        foreach (var row in Rows)
            row.Width = Math.Max(200, width);
    }

    /// <summary>Grow/shrink the window to fit the device list (capped to 3/4 of the screen, then it scrolls).</summary>
    private void UpdateWindowHeight()
    {
        int rowUnit = Rows.FirstOrDefault() is { } first
            ? first.Height + first.Margin.Vertical
            : LogicalToDeviceUnits(64);
        int count = Math.Max(1, Rows.Count());
        int listHeight = count * rowUnit + deviceList.Padding.Vertical + LogicalToDeviceUnits(6);
        int desired = topPanel.Height + bottomPanel.Height + listHeight;
        int max = Screen.FromControl(this).WorkingArea.Height * 3 / 4;
        ClientSize = new Size(ClientSize.Width, Math.Min(desired, max));
    }

    // ---------------------------------------------------------------- settings

    private void SyncSettingsFromRows()
    {
        settings.Devices = Rows
            .Select(r => new SavedDevice { Id = r.DeviceId, Name = r.DeviceName, Volume = r.VolumePercent, DelayMs = r.DelayMs })
            .ToList();
    }

    private void SaveSettings() => settings.Save();

    // ---------------------------------------------------------------- tray / lifecycle

    private static Icon LoadAppIcon()
    {
        // earshare.ico is an EmbeddedResource so it works in single-file publish too
        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("EarShare.UI.Assets.earshare.ico");
        return stream != null ? new Icon(stream) : TrayIconFactory.Create(active: false);
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try { BeginInvoke(action); } catch { }
    }

    private void HideToTray()
    {
        Hide();
        if (!trayHintShown)
        {
            trayHintShown = true;
            tray.ShowBalloonTip(2500, "EarShare",
                "Still running here. Double-click to reopen, right-click to stop or quit.", ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        exiting = true;
        Close();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X and minimize both go to the tray so closing the window never kills the
        // family's audio mid-movie; "Quit" in the tray menu really exits.
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SyncSettingsFromRows();
        SaveSettings();
        uiTimer.Stop();
        engine.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}
