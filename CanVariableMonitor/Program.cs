using System.Globalization;

namespace CanVariableMonitor;

static class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--syntax-highlight-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachParentConsole();
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = MainForm.RunSyntaxHighlightSelfTest(writer);
            string report = writer.ToString();
            Console.Write(report);
            WriteSyntaxHighlightSelfTestLog(report);
            return exitCode;
        }

        if (args.Any(arg => arg.Equals("--source-edit-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            AttachParentConsole();
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = SourceEditService.RunSelfTest(writer);
            string report = writer.ToString();
            Console.Write(report);
            WriteSelfTestLog("source_edit_selftest.log", report);
            return exitCode;
        }

        using var singleInstance = new System.Threading.Mutex(true, "CanVariableMonitor_Kangxu_SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            return 0;
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

        return 0;
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

    private static void AttachParentConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void WriteSyntaxHighlightSelfTestLog(string report)
    {
        WriteSelfTestLog("syntax_highlight_selftest.log", report);
    }

    private static void WriteSelfTestLog(string fileName, string report)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanVariableMonitor");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), report, System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }
}
