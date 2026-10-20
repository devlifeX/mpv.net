using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using static MpvNet.Native.LibMpv;

namespace MpvNet;

// Immutable selection taken before the Save dialog; later track switches cannot change the export.
public sealed record BufferedVideoSelection(long Generation, double Duration,
    double AudioDelay, BufferedVideoTrack[] Tracks, Dictionary<string, string> Options,
    VideoRangeSource? ByteCache = null);

public sealed record BufferedVideoTrack(string Type, string Source, int Id, bool CanUseCache);

public static class BufferedVideo
{
    static readonly string[] ConnectionOptions = [
        "http-header-fields", "user-agent", "referrer", "http-proxy", "cookies", "cookies-file",
        "tls-verify", "tls-ca-file", "tls-cert-file", "tls-key-file", "demuxer-lavf-o",
        "network-timeout", "edition", "config-dir"
    ];

    internal static Dictionary<string, string> ConnectionSettings(MpvClient player)
    {
        var options = new Dictionary<string, string>();
        foreach (string name in ConnectionOptions)
            if (player.TryGetPropertyString(name, out var value) && value != "") options[name] = value;
        return options;
    }

    public static BufferedVideoSelection Capture(MpvClient player, long generation)
    {
        if (player.PlaybackGeneration != generation || !player.TryGetPropertyString("path", out var path) || path == "")
            throw new InvalidOperationException(_("The playing file changed. Please try again."));

        string json = player.GetPropertyString("track-list");
        using var document = JsonDocument.Parse(json);
        var tracks = document.RootElement.EnumerateArray().ToArray();
        string source = player.GetPropertyString("stream-open-filename");
        if (source == "") source = path;
        var byteCache = (player as MainPlayer)?.FindByteCache(source);
        if (byteCache != null) source = byteCache.Url;
        var selected = new List<BufferedVideoTrack>();
        foreach (var track in tracks)
        {
            string type = track.GetProperty("type").GetString()!;
            if (type is not ("video" or "audio") || !Flag(track, "selected")) continue;
            bool external = Flag(track, "external");
            string filename = external ? track.GetProperty("external-filename").GetString()! : source;
            // External IDs belong to the foreground player, not to the external file's own demuxer.
            int id = external ? tracks.TakeWhile(t => !t.Equals(track)).Count(t =>
                t.GetProperty("type").GetString() == type && Flag(t, "external") &&
                t.GetProperty("external-filename").GetString() == filename) + 1 : track.GetProperty("id").GetInt32();
            selected.Add(new(type, filename, id, !external &&
                tracks.Count(t => t.GetProperty("type").GetString() == type && !Flag(t, "external")) == 1));
        }
        if (!selected.Any(t => t.Type == "video"))
            throw new InvalidOperationException(_("Open a video before saving."));

        var options = ConnectionSettings(player);
        double duration = player.GetPropertyDouble("duration");
        double delay = player.GetPropertyDouble("audio-delay");
        if (player.PlaybackGeneration != generation || player.GetPropertyString("track-list") != json)
            throw new InvalidOperationException(_("The playing file changed. Please try again."));
        return new(generation, duration, delay, selected.ToArray(), options, byteCache);
    }

    static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static (double Start, double End)? GetCompleteRange(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var document = JsonDocument.Parse(json);
        var state = document.RootElement;
        if (!Flag(state, "bof-cached") || !Flag(state, "eof-cached") ||
            !state.TryGetProperty("seekable-ranges", out var ranges) || ranges.GetArrayLength() != 1) return null;
        double start = ranges[0].GetProperty("start").GetDouble();
        double end = ranges[0].GetProperty("end").GetDouble();
        return double.IsFinite(start) && double.IsFinite(end) && end > start ? (start, end) : null;
    }

    public static Task SaveAsync(MpvClient player, string filename, long playbackGeneration) =>
        SaveAsync(player, filename, Capture(player, playbackGeneration));

    public static async Task SaveAsync(MpvClient player, string filename, BufferedVideoSelection selection,
        CancellationToken cancellationToken = default, IProgress<string>? progress = null,
        VideoDownloadControl? control = null, IProgress<DownloadSnapshot>? downloadProgress = null)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Shutdown() => canceled.Cancel();
        player.Shutdown += Shutdown;
        var token = canceled.Token;
        control ??= new();
        bool success = false;
        string parts = Path.GetFullPath(filename) + ".mpvnet-parts";
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filename))!, ".mpvnet-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var tools = new MediaExportTools();
            bool freshSources = false;
            await tools.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            string cache = Path.Combine(directory, "cache.mkv");
            string currentJson = player.GetPropertyString("track-list");
            using var currentTracks = JsonDocument.Parse(currentJson == "" ? "[]" : currentJson);
            var cacheable = selection.Tracks.Where(t => t.CanUseCache &&
                currentTracks.RootElement.EnumerateArray().Any(c => Flag(c, "selected") && !Flag(c, "external") &&
                    c.GetProperty("type").GetString() == t.Type && c.GetProperty("id").GetInt32() == t.Id)).ToHashSet();
            var range = player.PlaybackGeneration == selection.Generation
                ? GetCompleteRange(player.GetPropertyString("demuxer-cache-state")) : null;
            if (range != null && cacheable.Count > 0)
            {
                var result = await player.CommandVAsync("dump-cache", Number(range.Value.Start), Number(range.Value.End + 1), cache);
                token.ThrowIfCancellationRequested();
                // A changed selection during dumping makes the cache untrustworthy, not the pinned selection.
                if (result < 0 || player.PlaybackGeneration != selection.Generation ||
                    player.GetPropertyString("track-list") != currentJson)
                    File.Delete(cache);
            }

            while (true)
            {
                var inputs = new List<(BufferedVideoTrack Track, string Filename)>();
                var missing = new List<BufferedVideoTrack>();
                var coverage = new Dictionary<BufferedVideoTrack, ExportCoverage>();
                foreach (var track in selection.Tracks)
                {
                    token.ThrowIfCancellationRequested();
                    var measured = cacheable.Contains(track) && File.Exists(cache)
                        ? await tools.MeasureAsync(cache, track, token) : null;
                    if (measured != null && measured.Continuous &&
                        measured.Duration >= selection.Duration - measured.Tolerance)
                    {
                        inputs.Add((track, cache));
                        coverage[track] = measured;
                    }
                    else missing.Add(track);
                }
                int download = 0;
                foreach (var source in missing.GroupBy(t => t.Source))
                {
                    await control.WaitAsync(token);
                    progress?.Report(_("Downloading complete selected streams..."));
                    string input = Path.Combine(directory, "download-" + download++ + ".mkv");
                    var sourceTracks = selection.Tracks.Where(t => t.Source == source.Key).ToArray();
                    VideoRangeSource? bytes = !freshSources && selection.ByteCache?.Url == source.Key ? selection.ByteCache : null;
                    bool ownsBytes = false;
                    try
                    {
                        if (bytes == null && PlaybackByteCache.IsDirectMedia(source.Key))
                        {
                            try
                            {
                                bytes = await VideoRangeSource.OpenAsync(source.Key, selection.Options,
                                    Path.Combine(directory, "bytes-" + download), token);
                                ownsBytes = bytes != null;
                            }
                            catch (System.Net.Http.HttpRequestException) { /* libmpv may support the transport. */ }
                        }
                        if (bytes != null)
                        {
                            PreparePartsDirectory(parts);
                            progress?.Report(_("Downloading missing byte chunks..."));
                            await bytes.DownloadAsync(Path.Combine(parts, (freshSources ? "repair-" : "") + (download - 1).ToString(CultureInfo.InvariantCulture)),
                                control, downloadProgress, token);
                            string raw = Path.Combine(directory, "source-" + download + ".media");
                            await bytes.AssembleAsync(raw, token);
                            sourceTracks = sourceTracks.Select(t => t with { Source = raw }).ToArray();
                        }
                        else progress?.Report(_("This stream requires a single connection. Pause and resume are available while this window stays open."));
                        for (int attempt = 0; ; attempt++)
                        {
                            try
                            {
                                // Record the other selected track from the same source as an EOF/duration witness.
                                double sourceDuration = await DownloadAsync(sourceTracks, selection.Options, input, control, token);
                                var measured = new Dictionary<BufferedVideoTrack, ExportCoverage>();
                                foreach (var track in sourceTracks)
                                {
                                    var value = await tools.MeasureAsync(input, track, token);
                                    if (value == null) throw new IOException(_("The selected stream contained no complete media packets."));
                                    measured[track] = value;
                                }
                                double expected = sourceDuration > 0 ? sourceDuration : selection.Duration;
                                if (!measured.Values.Any(c => c.Duration >= expected - c.Tolerance))
                                    throw new IOException(_("The source ended before the complete selected stream was received. Reconnect and save again."));
                                // A genuine shorter audio track or intentional source gap must remain unchanged.
                                foreach (var track in source) coverage[track] = measured[sourceTracks.First(t => t.Type == track.Type)];
                                break;
                            }
                            catch (IOException) when (attempt < 2)
                            {
                                token.ThrowIfCancellationRequested();
                                progress?.Report(_("Connection interrupted. Retrying the selected streams..."));
                                await Task.Delay(500 * (attempt + 1), token);
                            }
                        }
                    }
                    finally { if (ownsBytes) bytes?.Dispose(); }
                    foreach (var track in source) inputs.Add((track, input));
                }
                inputs = inputs.OrderBy(i => i.Track.Type == "video" ? 0 : 1).ToList();

                progress?.Report(_("Verifying and saving video..."));
                await control.WaitAsync(token);
                string temporary = Path.Combine(directory, "output" + Path.GetExtension(filename));
                try
                {
                    await tools.MuxAsync(inputs, selection.AudioDelay, temporary, token);
                    foreach (var track in selection.Tracks)
                    {
                        var saved = await tools.MeasureAsync(temporary, track, token);
                        var original = coverage[track];
                        if (saved == null || saved.Packets != original.Packets ||
                            Math.Abs(saved.Duration - original.Duration) > Math.Max(saved.Tolerance, original.Tolerance))
                            throw new IOException(_("The saved file did not contain the complete selected video and audio. Please save again."));
                    }
                    await tools.VerifyDecodeAsync(temporary, token);
                }
                catch (IOException) when (!freshSources && (inputs.Any(i => i.Filename == cache) || selection.ByteCache != null))
                {
                    // Corrupt cached payloads also need a fresh source pass, even if their timestamps look complete.
                    File.Delete(cache);
                    freshSources = true;
                    continue;
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, filename, true);
                success = true;
                break;
            }
        }
        finally
        {
            player.Shutdown -= Shutdown;
            // A cleanup error must not turn an already verified, saved output into a failed job.
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (success && File.Exists(Path.Combine(parts, ".mpvnet-download")))
                try { Directory.Delete(parts, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static void PreparePartsDirectory(string directory)
    {
        string marker = Path.Combine(directory, ".mpvnet-download");
        if (Directory.Exists(directory) && !File.Exists(marker))
            throw new IOException(_("The download parts folder already exists and is not owned by mpv.net. Choose another destination."));
        Directory.CreateDirectory(directory);
        if (!File.Exists(marker)) File.WriteAllText(marker, "mpv.net download parts v1");
    }

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static async Task<double> DownloadAsync(BufferedVideoTrack[] tracks, Dictionary<string, string> options, string filename,
        VideoDownloadControl control, CancellationToken token)
    {
        var worker = new MpvClient { Handle = mpv_create() };
        if (worker.Handle == 0) throw new IOException(_("Could not start the stream download."));
        Task? loop = null;
        try
        {
            void Option(string name, string value)
            {
                if (mpv_set_option_string(worker.Handle, GetUtf8Bytes(name), GetUtf8Bytes(value)) < 0)
                    throw new IOException(_("Could not configure stream download: ") + name);
            }
            Option("config", "no");
            Option("load-scripts", "no");
            Option("ytdl", "no");
            Option("terminal", "no");
            Option("vo", "null");
            Option("ao", "null");
            Option("ao-null-untimed", "yes");
            Option("untimed", "yes");
            Option("idle", "yes");
            Option("cache", "no");
            Option("pause", "yes");
            Option("resume-playback", "no");
            Option("save-position-on-quit", "no");
            Option("start", "0");
            Option("network-timeout", "30");
            foreach (var pair in options) Option(pair.Key, pair.Value);
            Option("vid", tracks.FirstOrDefault(t => t.Type == "video")?.Id.ToString(CultureInfo.InvariantCulture) ?? "no");
            Option("aid", tracks.FirstOrDefault(t => t.Type == "audio")?.Id.ToString(CultureInfo.InvariantCulture) ?? "no");
            Option("sid", "no");
            Option("stream-record", filename);
            if (mpv_initialize(worker.Handle) < 0) throw new IOException(_("Could not start the stream download."));
            var finished = new TaskCompletionSource<mpv_end_file_reason>(TaskCreationOptions.RunContinuationsAsynchronously);
            double duration = 0;
            worker.FileLoaded += () => {
                duration = worker.GetPropertyDouble("duration");
                worker.SetPropertyBool("pause", control.IsPaused);
            };
            worker.EndFile += reason => finished.TrySetResult(reason);
            mpv_request_log_messages(worker.Handle, "error");
            worker.LogMessage += (_, _) => finished.TrySetException(new IOException(
                _("The selected stream could not be read or written completely. Check the connection and free disk space.")));
            loop = Task.Run(worker.EventLoop);
            worker.CommandV("loadfile", tracks[0].Source);
            while (!finished.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                worker.SetPropertyBool("pause", control.IsPaused);
                await Task.WhenAny(finished.Task, Task.Delay(150, token));
            }
            var reason = await finished.Task.WaitAsync(token);
            if (reason != mpv_end_file_reason.MPV_END_FILE_REASON_EOF || !File.Exists(filename))
                throw new IOException(_("The selected stream could not be downloaded. Check the connection and save again."));
            return duration;
        }
        finally
        {
            if (loop != null)
            {
                worker.CommandV("quit");
                await loop;
            }
            mpv_destroy(worker.Handle);
        }
    }
}
