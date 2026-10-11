using System;
using System.Windows;

namespace AsepriteManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--layout-smoke", StringComparer.OrdinalIgnoreCase))
        {
            try { LayoutSmoke.Run(); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        new MainWindow().Show();
    }
}
