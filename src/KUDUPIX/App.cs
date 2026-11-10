using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kudupix.Core;
using Kudupix.Views;

namespace Kudupix
{
    public static class Program
    {
        /// <summary>
        /// KUDUPIX.exe                                   open
        /// KUDUPIX.exe --self-test --samples folder      automated checks for the build (with the test photos and video)
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new App();
            if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                var i = Array.FindIndex(args, a => string.Equals(a, "--samples", StringComparison.OrdinalIgnoreCase));
                return app.RunSelfTest(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
            }

            // One copy at a time: two would share (and fight over) the same library.
            using (var mutex = new Mutex(true, @"Local\KUDUPIX-" + Environment.UserName, out var first))
            {
                if (!first)
                {
                    MessageBox.Show("KUDUPIX is already open.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                return app.Start();
            }
        }
    }

    public sealed class App : Application
    {
        public int Start()
        {
            Startup += (s, e) =>
            {
                Init();
                var catalog = new Catalog();
                var window = new MainWindow(catalog);
                MainWindow = window;
                var first = !Settings.FoldersChosen;
                if (first)
                {
                    // The first time: the usual places for photos and videos.
                    Settings.Folders = Settings.UsualFolders();
                    Settings.FoldersChosen = true;
                }
                window.Show();
                if (first) window.ShowFirstStartNote(catalog.Folders);
                catalog.Scan();
            };
            return Run();
        }

        public int RunSelfTest(string samples)
        {
            var code = 1;
            Startup += async (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = await SelfTest.Run(samples);
                Shutdown(code);
            };
            Run();
            return code;
        }

        private void Init()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            SafetyNet.Install(); // says so if an error behind the window closes the app
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
