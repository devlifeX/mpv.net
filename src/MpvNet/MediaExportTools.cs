using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace MpvNet;

// FFmpeg only sees local files; mpv resolves protocols and preserves the playback connection options.
internal sealed record ExportCoverage(double Duration, int Packets, bool Continuous, double Tolerance);

internal sealed class MediaExportTools
{
    static string Tool(string name)
    {
        string filename = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        string bundled = Path.Combine(AppContext.BaseDirectory, filename);
        return File.Exists(bundled) ? bundled : filename;
    }

    public async Task CheckAsync(CancellationToken token)
    {
        await RunAsync("ffmpeg", ["-version"], null, token);
    }

    public async Task<ExportCoverage?> MeasureAsync(string filename, BufferedVideoTrack track, CancellationToken token)
    {
        double first = double.PositiveInfinity, end = double.NegativeInfinity, previous = double.NaN;
        double largestGap = 0, largestPacket = 0, previousDuration = 0;
        int count = 0;
        string selector = track.Type == "video" ? "v:0" : "a:0";
        double timeBase = 0;
        try
        {
            await RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-copyts", "-i", filename,
                "-map", "0:" + selector, "-c", "copy", "-f", "framecrc", "-"], line => {
                if (line.StartsWith("#tb 0: "))
                {
                    var ratio = line[7..].Split('/');
                    timeBase = double.Parse(ratio[0], CultureInfo.InvariantCulture) /
                        double.Parse(ratio[1], CultureInfo.InvariantCulture);
                }
                if (line.StartsWith('#') || timeBase <= 0) return;
                var fields = line.Split(',');
                if (fields.Length < 6) return;
                double Read(int index) => double.TryParse(fields[index], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value) ? value * timeBase : double.NaN;
                double dts = Read(1), pts = Read(2), packetDuration = Read(3);
                if (!double.IsFinite(pts)) return;
                if (!double.IsFinite(packetDuration) || packetDuration < 0) packetDuration = 0;
                first = Math.Min(first, pts);
                end = Math.Max(end, pts + packetDuration);
                largestPacket = Math.Max(largestPacket, packetDuration);
                if (double.IsFinite(dts))
                {
                    if (double.IsFinite(previous) && previousDuration > 0)
                        largestGap = Math.Max(largestGap, dts - previous - previousDuration);
                    previous = dts;
                    previousDuration = packetDuration;
                }
                count++;
            }, token);
        }
        catch (IOException) { return null; }
        double tolerance = Math.Max(0.1, largestPacket * 2);
        return count > 0 && double.IsFinite(first) && double.IsFinite(end) && end > first
            ? new ExportCoverage(end - first, count, largestGap <= 0.003, tolerance) : null;
    }

    public async Task MuxAsync(List<(BufferedVideoTrack Track, string Filename)> inputs,
        double audioDelay, string output, CancellationToken token)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-y" };
        foreach (var input in inputs)
        {
            if (input.Track.Type == "audio" && audioDelay != 0)
                args.AddRange(["-itsoffset", BufferedVideo.Number(audioDelay)]);
            args.AddRange(["-i", input.Filename]);
        }
        for (int i = 0; i < inputs.Count; i++)
            args.AddRange(["-map", i + (inputs[i].Track.Type == "video" ? ":v:0" : ":a:0")]);
        args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_non_negative", output]);
        await RunAsync("ffmpeg", args, null, token);
    }

    public Task VerifyDecodeAsync(string filename, CancellationToken token) =>
        RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-err_detect", "explode", "-threads", "2", "-i", filename,
            "-map", "0:v:0", "-map", "0:a:0?", "-f", "null", "-"], null, token);

    static async Task RunAsync(string name, IEnumerable<string> args, Action<string>? line, CancellationToken token)
    {
        var info = new ProcessStartInfo(Tool(name)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException(_("The FFmpeg tool is missing from this installation. Reinstall the complete mpv.net package."));
        }
        using var canceled = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        // Drain both pipes concurrently, without retaining packet lists or credential-bearing diagnostics.
        bool reportedError = false;
        var stderr = Task.Run(async () => {
            while (await process.StandardError.ReadLineAsync() is { } error)
                if (!string.IsNullOrWhiteSpace(error)) reportedError = true;
        });
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } text) line?.Invoke(text);
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || reportedError) throw new IOException(_("Could not verify or save the video: ") + name + " (" + process.ExitCode + ").");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
        }
    }
}
