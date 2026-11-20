using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Riry.RiryTools.FTRecorder
{
    /// <summary>
    /// The Recorder window: where the recording goes, the two proxy ports, the microphone, ffmpeg and OVRLipSync, a
    /// Start/Stop button, the counts while recording, and a log.
    /// </summary>
    internal sealed class FTRecorderRecorderWindow : Window
    {
        private readonly TextBox output = new TextBox();
        private readonly NumericUpDown inPort = Port(9010);
        private readonly NumericUpDown outPort = Port(9000);
        private readonly ComboBox microphone = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Looking for microphones…" };
        private readonly TextBox ffmpeg = new TextBox { Text = "ffmpeg" };
        private readonly TextBox ovrLipSync = new TextBox { PlaceholderText = "VRChat's copy (found through Steam)" };
        private readonly Button startStop = new Button { Content = "Start recording", MinWidth = 140 };
        private readonly TextBlock status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly TextBox log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly List<Control> fields = new List<Control>();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private FTRecorderRecorderSession session;
        private bool busy;
        private bool closeWhenSaved;

        internal FTRecorderRecorderWindow()
        {
            Title = "RiryTools FTRecorder Recorder";
            Width = 720;
            Height = 560;
            MinWidth = 480;
            MinHeight = 400;
            output.Text = NewOutputPath();

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 8, ColumnSpacing = 8 };
            int row = 0;
            AddRow(grid, ref row, "Recording (.mkv)", output, Browse("Browse…", ChooseOutput));
            AddRow(grid, ref row, "VRCFaceTracking OSC output port", inPort, Hint("Set VRCFaceTracking's OSC output port to this."));
            AddRow(grid, ref row, "Pass on to port", outPort, Hint("VRChat's OSC input port (9000)."));
            AddRow(grid, ref row, "Microphone", microphone, null);
            AddRow(grid, ref row, "ffmpeg", ffmpeg, Browse("Browse…", ChooseFfmpeg));
            AddRow(grid, ref row, "OVRLipSync.dll", ovrLipSync, Browse("Browse…", ChooseOvrLipSync));
            fields.AddRange(new Control[] { output, inPort, outPort, microphone, ffmpeg, ovrLipSync });
            fields.AddRange(grid.Children.OfType<Button>());

            var controls = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(startStop, Dock.Left);
            controls.Children.Add(startStop);
            status.Margin = new Thickness(12, 0, 0, 0);
            controls.Children.Add(status);
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(controls, row);
            Grid.SetColumnSpan(controls, 3);
            grid.Children.Add(controls);
            row++;
            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            log.MinHeight = 120;
            Grid.SetRow(log, row);
            Grid.SetColumnSpan(log, 3);
            grid.Children.Add(log);
            Content = grid;

            startStop.Click += (_, _) => _ = StartOrStop();
            timer.Tick += (_, _) => status.Text = session?.Status() ?? "";
            Closing += OnClosing;
            FTRecorderRecorderLog.Attach(text => Dispatcher.UIThread.Post(() => Log(text)));
            Log("Set VRCFaceTracking's OSC output port to the first port, then press Start recording.");
            _ = ListMicrophones();
        }

        private static NumericUpDown Port(int value) =>
            new NumericUpDown { Value = value, Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };

        private static TextBlock Hint(string text) => new TextBlock { Text = text, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 220 };

        private static Button Browse(string text, Func<Task> choose)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => _ = choose();
            return button;
        }

        private static void AddRow(Grid grid, ref int row, string label, Control field, Control extra)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
            if (extra != null)
            {
                Grid.SetRow(extra, row);
                Grid.SetColumn(extra, 2);
                grid.Children.Add(extra);
            }
            row++;
        }

        // A new name in Documents\RiryTools FTRecorder each time, so recordings never collide.
        private static string NewOutputPath()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RiryTools FTRecorder");
            return Path.Combine(folder, "recording-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mkv");
        }

        private string FfmpegPath => string.IsNullOrWhiteSpace(ffmpeg.Text) ? null : ffmpeg.Text.Trim();

        private void Log(string text)
        {
            log.Text = string.IsNullOrEmpty(log.Text) ? text : log.Text + Environment.NewLine + text;
            log.CaretIndex = log.Text.Length;
        }

        // The microphones ffmpeg lists (dshow, so Windows only); the first is chosen, as the command line did.
        private async Task ListMicrophones()
        {
            if (!OperatingSystem.IsWindows())
            {
                microphone.PlaceholderText = "No microphone found";
                return;
            }
            string path = FfmpegPath;
            List<string> names;
            try
            {
                names = await Task.Run(() => FTRecorderFfmpeg.ListMicrophones(path));
            }
            catch (Exception exception) when (exception is FileNotFoundException || exception is InvalidOperationException)
            {
                Log(exception.Message);
                names = new List<string>();
            }
            object chosen = microphone.SelectedItem;
            microphone.ItemsSource = names;
            microphone.SelectedItem = chosen != null && names.Contains((string)chosen) ? chosen : names.Count > 0 ? names[0] : null;
            microphone.PlaceholderText = names.Count > 0 ? "" : "No microphone found";
        }

        private async Task StartOrStop()
        {
            if (busy) return;
            busy = true;
            startStop.IsEnabled = false;
            try
            {
                if (session == null) await Start();
                else await Stop();
            }
            finally
            {
                busy = false;
                startStop.IsEnabled = true;
            }
            if (closeWhenSaved) Close();
        }

        private async Task Start()
        {
            // No microphone chosen yet (or ffmpeg's path was just fixed): asked again first.
            if (microphone.SelectedItem == null) await ListMicrophones();
            var settings = new FTRecorderRecorderSettings
            {
                Output = output.Text?.Trim(),
                InPort = (int)(inPort.Value ?? 9010),
                OutPort = (int)(outPort.Value ?? 9000),
                Microphone = microphone.SelectedItem as string,
                Ffmpeg = FfmpegPath,
                OvrLipSync = ovrLipSync.Text?.Trim(),
            };
            SetFieldsEnabled(false);
            try
            {
                session = await Task.Run(() => FTRecorderRecorderSession.Start(settings, AskOvrLipSync, text => Dispatcher.UIThread.Post(() => Log(text))));
                startStop.Content = "Stop and save";
                timer.Start();
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                Log("Error: " + exception.Message);
                SetFieldsEnabled(true);
            }
        }

        private async Task Stop()
        {
            timer.Stop();
            FTRecorderRecorderSession stopping = session;
            status.Text = "Saving…";
            try
            {
                await Task.Run(stopping.Stop);
                output.Text = NewOutputPath();
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                // The window stays open so the error (and where the recording was kept) can be read.
                Log("Error: " + exception.Message);
                closeWhenSaved = false;
            }
            session = null;
            status.Text = "";
            startStop.Content = "Start recording";
            SetFieldsEnabled(true);
        }

        private static bool IsExpected(Exception exception) =>
            exception is ArgumentException || exception is IOException || exception is InvalidOperationException || exception is InvalidDataException ||
            exception is UnauthorizedAccessException || exception is PlatformNotSupportedException;

        private void SetFieldsEnabled(bool enabled)
        {
            foreach (Control field in fields) field.IsEnabled = enabled;
        }

        // Closing while recording stops and saves first.
        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (session == null && !busy) return;
            e.Cancel = true;
            if (closeWhenSaved) return;
            closeWhenSaved = true;
            Log("Saving the recording before closing…");
            if (session != null && !busy) _ = StartOrStop();
        }

        // Called from the session's thread when OVRLipSync cannot be found or loaded: the error, then a file picker;
        // cancelling it stops the start.
        private string AskOvrLipSync(string reason)
        {
            return Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Log("Error: " + reason);
                Log("Choose OVRLipSync.dll (cancel to stop).");
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "OVRLipSync.dll",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("OVRLipSync.dll") { Patterns = new[] { "OVRLipSync.dll", "*.dll" } } },
                });
                string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (path != null) ovrLipSync.Text = path;
                return path;
            }).GetAwaiter().GetResult();
        }

        private async Task ChooseOutput()
        {
            IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Recording",
                DefaultExtension = "mkv",
                SuggestedFileName = Path.GetFileName(output.Text ?? ""),
                FileTypeChoices = new[] { new FilePickerFileType("Matroska") { Patterns = new[] { "*.mkv" } } },
            });
            string path = file?.TryGetLocalPath();
            if (path != null) output.Text = path;
        }

        private async Task ChooseFfmpeg()
        {
            string path = await PickFile("ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (path == null) return;
            ffmpeg.Text = path;
            await ListMicrophones();
        }

        private async Task ChooseOvrLipSync()
        {
            string path = await PickFile("OVRLipSync.dll", "*.dll");
            if (path != null) ovrLipSync.Text = path;
        }

        private async Task<string> PickFile(string title, string pattern)
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(title) { Patterns = new[] { pattern } } },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}

// ============================================================================
// GLOSSARY: FTRecorder Recorder window
// ============================================================================
//
// FTRecorderRecorderWindow (this file)  The Recorder's only window.
//   Fields                   Recording (.mkv; a new Documents\RiryTools FTRecorder\recording-<time>.mkv after each save),
//                            VRCFaceTracking OSC output port (9010), Pass on to port (9000), Microphone (ffmpeg's dshow
//                            list, first chosen), ffmpeg ("ffmpeg"), OVRLipSync.dll (empty: VRChat's copy). Browse
//                            buttons open file pickers. Fields are locked while recording.
//   Start recording          FTRecorderRecorderSession.Start off the window's thread; the counts every second after.
//   Stop and save            FTRecorderRecorderSession.Stop off the window's thread (writes the .mkv).
//   AskOvrLipSync(reason)    The error in the log, then a file picker; cancel stops the start.
//   Closing                  While recording: stops and saves, then closes; stays open when saving fails.
//   Log                      Messages, errors and everything the shared code writes to the console.
