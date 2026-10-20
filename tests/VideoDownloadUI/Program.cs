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
        var selection = new BufferedVideoSelection(0, 60, 0, [new("video", "https://example.com/video.mp4", 1, true)], []);
        using var form = new VideoDownloadForm(@"E:\Video\Saved video.mkv", selection, canceled);
        foreach (var size in new[] { new Size(820, 720), new Size(760, 620), new Size(1100, 850) })
        {
            form.Size = size;
            form.CreateControl();
            form.PerformLayout();
            form.Show();
            Application.DoEvents();
            var controls = Descendants(form).ToArray();
            Check(controls.All(c => c.ForeColor == Color.FromArgb(235, 238, 243)), "Every control uses the same readable foreground: " + size);
            Check(controls.Where(c => c is TextBoxBase or ListView or NumericUpDown).All(c => c.BackColor == Color.FromArgb(27, 30, 35)),
                "Text fields, numeric inputs and the chunk table share one dark surface");
            var buttons = controls.OfType<Button>().ToArray();
            Check(buttons.All(b => !b.UseVisualStyleBackColor && b.FlatStyle == FlatStyle.Flat), "Buttons cannot inherit conflicting Windows theme colors");
            Check(controls.OfType<NumericUpDown>().First().Value == 8, "Eight download connections are selected by default");
            foreach (var control in controls.Where(c => c is Button or CheckBox or NumericUpDown))
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), "Control remains inside its layout: " + control.Text);
            string file = Path.Combine(args[0], $"download-{size.Width}x{size.Height}.png");
            Directory.CreateDirectory(args[0]);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(file, ImageFormat.Png);
        }
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
