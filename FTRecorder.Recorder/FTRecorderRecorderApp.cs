using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>The Recorder app: one window that records VRCFaceTracking's output, visemes and the microphone.</summary>
    internal sealed class FTRecorderRecorderApp : Application
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            // The app has no console: what the shared code writes there goes to the window's log.
            var log = new FTRecorderRecorderLog();
            Console.SetOut(log);
            Console.SetError(log);
            return AppBuilder.Configure<FTRecorderRecorderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }

        public override void Initialize()
        {
            Name = "RiryTools FTRecorder Recorder";
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new FTRecorderRecorderWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>Console output as whole lines, for the window's log (kept until the window takes them).</summary>
    internal sealed class FTRecorderRecorderLog : TextWriter
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Backlog = new List<string>();
        private static Action<string> sink;
        private readonly StringBuilder line = new StringBuilder();

        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>Where lines go from now on; the lines written before are given first.</summary>
        internal static void Attach(Action<string> target)
        {
            lock (Gate)
            {
                sink = target;
                foreach (string text in Backlog) target(text);
                Backlog.Clear();
            }
        }

        public override void Write(char value)
        {
            lock (Gate)
            {
                if (value == '\r') return;
                if (value != '\n')
                {
                    line.Append(value);
                    return;
                }
                string text = line.ToString();
                line.Clear();
                if (sink != null) sink(text);
                else Backlog.Add(text);
            }
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Recorder app
// ============================================================================
//
// FTRecorderRecorderApp (this file)  Entry point (Main) and Avalonia application (Fluent theme); the main window is
//                            FTRecorderRecorderWindow. Console output and errors go to FTRecorderRecorderLog.
// FTRecorderRecorderLog (this file)  A TextWriter that splits what is written into lines for the window's log (Attach);
//                            lines written before the window attaches are kept and given first.
