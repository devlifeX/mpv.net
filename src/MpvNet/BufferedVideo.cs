using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

using static MpvNet.Native.LibMpv;

namespace MpvNet;

public static class BufferedVideo
{
    public static bool HasExternalMedia(string json)
    {
        using var tracks = JsonDocument.Parse(json);
        return tracks.RootElement.EnumerateArray().Any(track =>
            track.TryGetProperty("external", out var external) && external.GetBoolean() &&
            track.GetProperty("selected").GetBoolean() &&
            track.GetProperty("type").GetString() is "audio" or "video");
    }

    public static (double Start, double End)? GetCompleteRange(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        using var document = JsonDocument.Parse(json);
        var state = document.RootElement;

        if (!state.TryGetProperty("bof-cached", out var bof) || !bof.GetBoolean() ||
            !state.TryGetProperty("eof-cached", out var eof) || !eof.GetBoolean() ||
            !state.TryGetProperty("seekable-ranges", out var ranges) || ranges.GetArrayLength() != 1)
            return null;

        double start = ranges[0].GetProperty("start").GetDouble();
        double end = ranges[0].GetProperty("end").GetDouble();
        return double.IsFinite(start) && double.IsFinite(end) && end > start ? (start, end) : null;
    }

    public static async Task SaveAsync(MpvClient player, string filename, long playbackGeneration)
    {
        // A sibling temporary file keeps an existing destination intact if mpv fails or playback changes.
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filename)!,
            Guid.NewGuid().ToString("N") + ".partial" + System.IO.Path.GetExtension(filename));

        try
        {
            if (player.PlaybackGeneration != playbackGeneration)
                throw new InvalidOperationException(_("The playing file changed. Please try again."));

            var range = GetCompleteRange(player.GetPropertyString("demuxer-cache-state"));

            if (range == null)
                throw new InvalidOperationException(_("The complete video is not buffered. Let it buffer from beginning to end before saving."));

            if (HasExternalMedia(player.GetPropertyString("track-list")))
                throw new InvalidOperationException(_("Saving videos with separate external audio or video streams is not supported."));

            mpv_error error = await player.CommandVAsync("dump-cache",
                range.Value.Start.ToString("R", CultureInfo.InvariantCulture),
                (range.Value.End + 1).ToString("R", CultureInfo.InvariantCulture), temporary);

            if (error < 0)
                throw new IOException(_("Could not save the buffered video.") + BR + GetError(error));

            if (player.PlaybackGeneration != playbackGeneration)
                throw new InvalidOperationException(_("The playing file changed. Please try again."));

            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException(_("No video data was saved."));

            File.Move(temporary, filename, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
