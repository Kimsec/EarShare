using EarShare.Audio;

namespace EarShare.UI;

/// <summary>One row in the device list: name, live status, volume slider, delay trim, remove button.</summary>
public sealed class DeviceRow : Panel
{
    private readonly Label nameLabel;
    private readonly Label stateLabel;
    private readonly Label percentLabel;
    private readonly Label msLabel;
    private readonly TrackBar volumeBar;
    private readonly NumericUpDown delayBox;
    private readonly Button removeButton;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public int VolumePercent => volumeBar.Value;
    public int DelayMs => (int)delayBox.Value;

    /// <summary>Slider value with a squared taper, which feels roughly linear in loudness.</summary>
    public float VolumeScalar
    {
        get
        {
            float v = volumeBar.Value / 100f;
            return v * v;
        }
    }

    public event Action<DeviceRow>? RemoveClicked;
    public event Action<DeviceRow>? VolumeChanged;
    public event Action<DeviceRow>? DelayChanged;

    public DeviceRow(string deviceId, string deviceName, int volumePercent, int delayMs)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;

        Margin = new Padding(2, 3, 2, 3);
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;

        nameLabel = new Label
        {
            Text = deviceName,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = false,
            AutoEllipsis = true,
        };
        stateLabel = new Label
        {
            Text = "",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
            AutoEllipsis = true,
        };
        percentLabel = new Label
        {
            Text = volumePercent + "%",
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
        };
        volumeBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(volumePercent, 0, 100),
            TickStyle = TickStyle.None,
            AutoSize = false,
            SmallChange = 2,
            LargeChange = 10,
        };
        volumeBar.ValueChanged += (_, _) =>
        {
            percentLabel.Text = volumeBar.Value + "%";
            VolumeChanged?.Invoke(this);
        };
        delayBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = OutputPipeline.MaxExtraDelayMs,
            Increment = 10,
            Value = Math.Clamp(delayMs, 0, OutputPipeline.MaxExtraDelayMs),
            TextAlign = HorizontalAlignment.Right,
        };
        delayBox.ValueChanged += (_, _) => DelayChanged?.Invoke(this);
        msLabel = new Label
        {
            Text = "ms",
            AutoSize = true, // sized from the actual (DPI-scaled) font so it never truncates
            ForeColor = SystemColors.GrayText,
        };
        removeButton = new Button
        {
            Text = "✕",
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
        };
        removeButton.FlatAppearance.BorderSize = 0;
        removeButton.Click += (_, _) => RemoveClicked?.Invoke(this);

        var tip = new ToolTip();
        tip.SetToolTip(nameLabel, deviceName);
        tip.SetToolTip(removeButton, "Remove this device");
        string delayHint = "Extra delay for this device. Raise it on fast devices (wired / 2.4 GHz) to line them up with slower Bluetooth headsets.";
        tip.SetToolTip(delayBox, delayHint);
        tip.SetToolTip(msLabel, delayHint);

        Controls.AddRange(new Control[] { nameLabel, stateLabel, volumeBar, percentLabel, delayBox, msLabel, removeButton });
        Resize += (_, _) => Relayout();
        Relayout();
    }

    public void SetState(string text, bool error)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetState(text, error));
            return;
        }
        stateLabel.Text = text;
        stateLabel.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        // fires when the row inherits the form's DPI-scaled font after being added
        base.OnFontChanged(e);
        Relayout();
    }

    /// <summary>
    /// All sizes derive from the current font height so rows look right at any
    /// Windows display scaling, including rows added after the form was scaled
    /// (WinForms does not rescale controls added at runtime).
    /// </summary>
    private void Relayout()
    {
        int fh = Font.Height;        // 15 px at 100 % scaling
        float k = fh / 15f;
        int S(double v) => (int)Math.Round(v * k);

        int desiredHeight = S(58);
        if (Height != desiredHeight)
            Height = desiredHeight;  // re-enters Relayout once; values are idempotent

        int w = ClientSize.Width;

        nameLabel.Height = fh + 3;
        stateLabel.Height = fh + 3;
        stateLabel.Width = S(170);
        nameLabel.Location = new Point(S(8), S(6));
        stateLabel.Location = new Point(w - stateLabel.Width - S(8), S(6));
        nameLabel.Width = Math.Max(S(40), w - stateLabel.Width - S(24));

        int rowY = S(27);
        removeButton.Size = new Size(S(26), S(26));
        removeButton.Location = new Point(w - removeButton.Width - S(6), rowY);
        msLabel.Location = new Point(removeButton.Left - msLabel.Width - S(2), rowY + S(6));
        delayBox.Width = S(54);
        delayBox.Location = new Point(msLabel.Left - delayBox.Width - S(2), rowY + S(2));
        percentLabel.Width = S(42);
        percentLabel.Height = fh + 3;
        percentLabel.Location = new Point(delayBox.Left - percentLabel.Width - S(6), rowY + S(5));
        volumeBar.Height = S(26);
        volumeBar.Location = new Point(S(4), rowY + S(1));
        volumeBar.Width = Math.Max(S(60), percentLabel.Left - volumeBar.Left - S(4));
    }
}
