using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace S3bopus
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Aucun plantage silencieux : toute erreur est écrite dans /tmp/S3bopus-erreur.txt
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
            Settings.Load();
            Lang.En = Settings.GetBool("English", false);
            Archives.CleanTemp();
            try
            {
                AppBuilder.Configure(() => new App(args)).UsePlatformDetect().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex) { Report(ex); throw; }
        }

        internal static void Report(Exception ex)
        {
            if (ex == null) return;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "S3bopus-erreur.txt"), DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine); } catch { }
            Console.Error.WriteLine(ex);
        }
    }

    internal sealed class App : Application
    {
        readonly string[] _args;
        public App(string[] args) { _args = args; }

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://s3bopus/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
            RequestedThemeVariant = Settings.GetBool("Dark", false) ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow(_args);
            base.OnFrameworkInitializationCompleted();
        }
    }
}
