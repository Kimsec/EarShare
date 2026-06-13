using EarShare.Audio;
using EarShare.Settings;

namespace EarShare.UI;

public sealed class MainForm : Form
{
    private readonly MirrorEngine engine = new();
    private readonly AppSettings settings;

    private readonly Panel topPanel;
    private readonly Panel bottomPanel;
    private readonly Label mirrorFromLabel;
    private readonly ComboBox captureCombo;
    private readonly Label statusLabel;
    private readonly FlowLayoutPanel deviceList;
    private readonly Button addButton;
    private readonly Button startButton;
    private readonly Label bufferLabel;
    private readonly NumericUpDown bufferBox;
    private readonly Label bufferMsLabel;

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

    /// <summary>Converts a 100 %-scale (96 DPI) pixel value to the current monitor's DPI.</summary>
    private int Scale(double v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public MainForm()
    {
        settings = AppSettings.Load();

        Text = "EarShare";
        Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        // We scale every coordinate ourselves (see LayoutForm), so WinForms auto-scaling
        // is turned off — one predictable mechanism that is pixel-identical at 100 %.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle; // size is managed automatically
        MaximizeBox = false;
        ClientSize = new Size(470, 300); // provisional; LayoutForm sets the real size

        // --- top: capture source + status ---
        topPanel = new Panel { Dock = DockStyle.Top };
        mirrorFromLabel = new Label { Text = "Mirror from:", AutoSize = true };
        captureCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        captureCombo.DropDown += (_, _) => PopulateCaptureCombo();
        captureCombo.SelectedIndexChanged += (_, _) => OnCaptureSelectionChanged();
        statusLabel = new Label
        {
            Text = "Stopped — add output devices, then press Start.",
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            AutoEllipsis = true,
        };
        topPanel.Controls.AddRange(new Control[] { mirrorFromLabel, captureCombo, statusLabel });

        // --- middle: device rows ---
        deviceList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        deviceList.ClientSizeChanged += (_, _) => ResizeRows();

        // --- bottom: actions + buffer setting ---
        bottomPanel = new Panel { Dock = DockStyle.Bottom };
        addButton = new Button { Text = "Add device  ▾" };
        addButton.Click += (_, _) => ShowAddDeviceMenu();
        bufferLabel = new Label { Text = "Buffer:", AutoSize = true };
        bufferBox = new NumericUpDown
        {
            Minimum = OutputPipeline.MinBufferMs,
            Maximum = OutputPipeline.MaxBufferMs,
            Increment = 10,
            Value = Math.Clamp(settings.BufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs),
            TextAlign = HorizontalAlignment.Right,
        };
        bufferBox.ValueChanged += (_, _) => OnBufferChanged();
        bufferMsLabel = new Label { Text = "ms", AutoSize = true, ForeColor = SystemColors.GrayText };
        var bufferTip = new ToolTip();
        string bufferHint = "Audio queued per device before playback. Lower = tighter lip-sync; raise it if you hear crackling or dropouts.";
        bufferTip.SetToolTip(bufferBox, bufferHint);
        bufferTip.SetToolTip(bufferLabel, bufferHint);
        startButton = new Button { Text = "Start" };
        startButton.Click += (_, _) => ToggleMirroring();
        bottomPanel.Controls.AddRange(new Control[] { addButton, bufferLabel, bufferBox, bufferMsLabel, startButton });

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

        uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        uiTimer.Tick += (_, _) => OnUiTimerTick();
        uiTimer.Start();
    }

    // ---------------------------------------------------------------- DPI-aware layout

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutForm(); // DeviceDpi is now the real monitor DPI
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutForm(); // window dragged to a screen with different scaling
    }

    /// <summary>
    /// Positions and sizes every control from a single DPI factor. At 100 % the factor
    /// is 1.0, so the result is pixel-identical to the original hand-tuned layout; at
    /// 200 % everything is exactly doubled. Device rows scale themselves the same way.
    /// </summary>
    private void LayoutForm()
    {
        SuspendLayout();
        topPanel.SuspendLayout();
        bottomPanel.SuspendLayout();

        int pad = Scale(10);
        int formW = Scale(420);
        int topH = Scale(64);
        int botH = Scale(48);

        topPanel.Height = topH;
        bottomPanel.Height = botH;
        deviceList.Padding = new Padding(Scale(6), Scale(2), Scale(6), Scale(2));

        // Fixed-width window. The docked panels follow this width, but we lay out from
        // formW directly — reading panel.ClientSize here returns a stale width because
        // layout is suspended, which previously pushed the Start button off-screen.
        ClientSize = new Size(formW, ClientSize.Height);

        // top panel: label, then combo tucked right after the (scaled) label text.
        // The small floor is only a safety net against under-measured label width.
        mirrorFromLabel.Location = new Point(pad, Scale(12));
        int comboLeft = Math.Max(Scale(86), mirrorFromLabel.Right + Scale(4));
        captureCombo.Location = new Point(comboLeft, Scale(8));
        captureCombo.Width = Math.Max(Scale(80), formW - comboLeft - pad);
        statusLabel.Location = new Point(pad, Scale(40));
        statusLabel.Size = new Size(formW - Scale(20), Scale(18));

        // bottom panel: Add (left), buffer group (after it), Start (right), vertically centred
        addButton.Size = new Size(Scale(120), Scale(30));
        addButton.Location = new Point(pad, (botH - addButton.Height) / 2);
        startButton.Size = new Size(Scale(120), Scale(30));
        startButton.Location = new Point(formW - startButton.Width - pad, (botH - startButton.Height) / 2);
        bufferBox.Size = new Size(Scale(56), Scale(24));
        bufferLabel.Location = new Point(addButton.Right + Scale(12), (botH - bufferLabel.Height) / 2);
        bufferBox.Location = new Point(bufferLabel.Right + Scale(4), (botH - bufferBox.Height) / 2);
        bufferMsLabel.Location = new Point(bufferBox.Right + Scale(2), (botH - bufferMsLabel.Height) / 2);

        bottomPanel.ResumeLayout();
        topPanel.ResumeLayout();
        ResumeLayout();

        ResizeRows();
        UpdateWindowHeight();
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
        int width = deviceList.ClientSize.Width - Scale(16);
        foreach (var row in Rows)
            row.Width = Math.Max(Scale(200), width);
    }

    /// <summary>
    /// Grow/shrink the window so its height tracks the number of output devices:
    /// no devices = compact (just the top bar + buttons), then one row taller per
    /// device, capped to 3/4 of the screen after which the list scrolls.
    /// </summary>
    private void UpdateWindowHeight()
    {
        int count = Rows.Count();
        int listHeight = 0;
        if (count > 0)
        {
            var first = Rows.First();
            int rowUnit = first.Height + first.Margin.Vertical;
            listHeight = count * rowUnit + deviceList.Padding.Vertical + Scale(6);
        }
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
