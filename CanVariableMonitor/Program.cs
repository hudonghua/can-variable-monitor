namespace CanVariableMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var singleInstance = new System.Threading.Mutex(true, "CanVariableMonitor_Kangxu_SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            return;
        }

        Application.ThreadException += (_, e) => WriteFatalLog("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteFatalLog("APP", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteFatalLog("TASK", e.Exception);
            e.SetObserved();
        };

        try
        {
            ApplicationConfiguration.Initialize();
            using (var welcome = new WelcomeForm())
            {
                welcome.ShowDialog();
            }

            Application.Run(new MainForm());
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void WriteFatalLog(string source, Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "diagnostic.log");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 未捕获异常({source})：{ex}";
            File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }
}
