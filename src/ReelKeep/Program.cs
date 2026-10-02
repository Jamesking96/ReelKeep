using ReelKeep.Core;
using ReelKeep.Host;

namespace ReelKeep;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Unexpected errors go to %LOCALAPPDATA%\ReelKeep\error.log instead of being lost
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Report(ex, showDialog: false); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception, showDialog: false); e.SetObserved(); };

        Application.Run(new MainWindow());
    }

    private static void Report(Exception ex, bool showDialog = true)
    {
        var log = Path.Combine(AppSettings.AppDataDir, "error.log");
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDir);
            File.AppendAllText(log, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\r\n{ex}\r\n\r\n");
        }
        catch { /* ignore */ }
        if (showDialog)
            MessageBox.Show($"Something went wrong:\n\n{ex.Message}\n\nDetails were saved to:\n{log}", AppSettings.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
