using EarShare.UI;

namespace EarShare;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Two instances would fight over the same output devices.
        using var instanceMutex = new Mutex(initiallyOwned: true, "EarShare_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "EarShare is already running — look for its icon in the system tray.",
                "EarShare", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // PerMonitorV2: render sharply at the actual monitor DPI and re-lay-out when
        // the window moves to a screen with different scaling (e.g. docking the Surface).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());

        GC.KeepAlive(instanceMutex);
    }
}
