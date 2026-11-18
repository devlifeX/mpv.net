using System.Drawing.Imaging;
using MpvNet;
using MpvNet.Windows.WinForms;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Translator.Current = new BrokenTranslation();
        using (var dialog = new SaveFileDialog { Filter = VideoDownloadForm.FileFilter })
        {
            Check(dialog.Filter.Split('|').Length == 2 && dialog.Filter.EndsWith("|*.mkv"),
                "A translation containing separators cannot corrupt the native file filter");
        }
        Translator.Current = new IdentityTranslation();
        using var canceled = new CancellationTokenSource();
        const string filename = @"E:\Videos\North Sea - Episode 04.mkv";
        var selection = new BufferedVideoSelection(0, 60, 0, [new("video", "https://media.example.com/series/episode-04.mp4", 1, true)], []);
        using var form = new VideoDownloadForm(filename, selection, canceled);
        foreach (var size in new[] { new Size(840, 690), new Size(720, 590), new Size(1100, 850) })
        {
            form.Size = size;
            form.CreateControl();
            form.PerformLayout();
            form.Show();
            Application.DoEvents();
            var controls = Descendants(form).ToArray();
            Check(controls.Where(c => c.Visible && c is Label or ListView or NumericUpDown)
                .All(c => Contrast(c.ForeColor, c.BackColor) >= 4.5), "Visible controls use readable text: " + size);
            var buttons = controls.OfType<Button>().ToArray();
            Check(buttons.All(b => !b.UseVisualStyleBackColor && b.FlatStyle == FlatStyle.Flat), "Buttons cannot inherit conflicting Windows theme colors");
            Check(controls.OfType<NumericUpDown>().First().Value == 8, "Eight download connections are selected by default");
            foreach (var control in controls.Where(c => c is Button or CheckBox or NumericUpDown))
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), "Control remains inside its layout: " + control.Text);
            Capture(form, args[0], $"ready-{size.Width}x{size.Height}.png");
        }
        form.Hide();

        using var save = new VideoDownloadForm(filename, selection, canceled);
        var finishTransfer = new TaskCompletionSource();
        var verification = new TaskCompletionSource();
        var finished = new TaskCompletionSource();
        var run = save.RunAsync(async (control, status, chunks, stages) => {
            const int size = 1024 * 1024;
            var blocks = Enumerable.Range(0, 64).Select(i => {
                bool cached = i % 17 < 5 || i is >= 42 and <= 44;
                long received = cached || i < 25 ? size : i < 30 ? size / 2 : 0;
                return new DownloadChunk(i * size, size, received, cached,
                    received == size ? "Complete" : received > 0 ? "Receiving" : "Missing");
            }).ToArray();
            status.Report("Downloading missing chunks...");
            chunks.Report(new(64 * size, blocks.Sum(b => b.Received), 2 * size, false, blocks));
            await finishTransfer.Task;
            chunks.Report(new(64 * size, 64 * size, 0, false, blocks.Select(b => b with { Received = size, State = "Complete" }).ToArray()));
            await verification.Task;
            stages.Report(new("Verifying video and audio", 0.35));
            await finished.Task;
        });
        var saveControls = Descendants(save).ToArray();
        var start = saveControls.OfType<Button>().Single(b => b.Text == "Start");
        var pause = saveControls.OfType<Button>().Single(b => b.Text == "Pause");
        start.PerformClick();
        PumpUntil(() => saveControls.OfType<Label>().Any(l => l.Text.Contains("2.00 MiB/s")));
        Check(pause.Enabled, "Pause is enabled during transfer");
        Capture(save, args[0], "downloading.png");
        pause.PerformClick();
        Check(pause.Text == "Resume" && saveControls.OfType<Label>().Any(l => l.Text == "Paused"),
            "Paused transfer is clearly labeled and offers Resume");
        pause.PerformClick();
        save.Size = new Size(720, 590);
        Application.DoEvents();
        var clippedLabels = saveControls.OfType<Label>().Where(l => l.Visible && !l.AutoSize && l.ClientSize.Height < l.Font.Height)
            .Select(l => l.Text).ToArray();
        Check(clippedLabels.Length == 0, "Metrics, status and title remain visible at minimum size during a download" +
            (clippedLabels.Length > 0 ? ": " + string.Join(", ", clippedLabels) : ""));
        finishTransfer.SetResult();
        PumpUntil(() => saveControls.OfType<Label>().Any(l => l.Text.Contains("100%")));
        verification.SetResult();
        PumpUntil(() => saveControls.OfType<Label>().Any(l => l.Text.Contains("Verifying video and audio")));
        Check(saveControls.OfType<Label>().Any(l => l.Text.Contains("Download complete")) &&
            saveControls.OfType<Label>().Any(l => l.Text.Contains("35%")),
            "Final verification shows its own progress after download reaches 100 percent");
        Check(!pause.Enabled, "Pause is disabled during final verification");
        save.Size = new Size(840, 690);
        Application.DoEvents();
        Capture(save, args[0], "verifying.png");
        finished.SetResult();
        PumpUntil(() => run.IsCompleted);
        run.GetAwaiter().GetResult();
        Check(saveControls.OfType<Label>().Any(l => l.Text.StartsWith("Video saved and verified.")),
            "The dialog reports completion only after final verification returns");
        Capture(save, args[0], "complete.png");
    }

    static void Capture(Control control, string folder, string filename)
    {
        Directory.CreateDirectory(folder);
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(Path.Combine(folder, filename), ImageFormat.Png);
    }

    static double Contrast(Color foreground, Color background)
    {
        static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Linear(color.R / 255D) + 0.7152 * Linear(color.G / 255D) + 0.0722 * Linear(color.B / 255D);
        double first = Luminance(foreground), second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        if (!condition()) throw new Exception("The save dialog did not reach the expected stage.");
    }
    static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    static void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS: " + text); }
    sealed class IdentityTranslation : ITranslator
    {
        public string Gettext(string text) => text;
        public string GetParticularString(string context, string text) => text;
    }
    sealed class BrokenTranslation : ITranslator
    {
        public string Gettext(string text) => "ویدیو|بدون الگو|";
        public string GetParticularString(string context, string text) => Gettext(text);
    }
}
