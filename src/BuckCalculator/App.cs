using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BuckCalculator;

public static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? smokeDirectory = null;
        if (args.Length > 0)
        {
            if (args.Length != 2 || args[0] != "--smoke-test")
                return 2;
            smokeDirectory = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(smokeDirectory);
        }

        try
        {
            var application = new Application();
            var window = new MainWindow();
            if (smokeDirectory is not null)
            {
                string outputDirectory = smokeDirectory;
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        try
                        {
                            window.RunSmokeChecks(outputDirectory);
                            application.Shutdown(0);
                        }
                        catch (Exception exception)
                        {
                            File.WriteAllText(Path.Combine(outputDirectory, "smoke-error.txt"), exception.ToString());
                            application.Shutdown(1);
                        }
                    }));
            }

            return application.Run(window);
        }
        catch (Exception exception)
        {
            if (smokeDirectory is not null)
                File.WriteAllText(Path.Combine(smokeDirectory, "smoke-error.txt"), exception.ToString());
            else
                MessageBox.Show(exception.Message, "BUCK 计算器启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
