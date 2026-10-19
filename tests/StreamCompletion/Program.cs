using System.Runtime.InteropServices;
using MpvNet;
using static MpvNet.Global;
using static MpvNet.Native.LibMpv;

Translator.Current = new IdentityTranslator();
string mode = args[0], url = args[1], output = args[2];
Directory.CreateDirectory(output);
string config = Path.Combine(output, "config");
Directory.CreateDirectory(config);
Environment.SetEnvironmentVariable("MPVNET_HOME", config);
File.WriteAllText(Path.Combine(config, "mpv.conf"), "vo=null\nao=null\nhwdec=no\nnetwork-timeout=2\nhttp-header-fields=X-Export: selected-audio\n");
string? native = Environment.GetEnvironmentVariable("MPVNET_TEST_LIBMPV");
if (native != null)
    NativeLibrary.SetDllImportResolver(typeof(MpvClient).Assembly, (name, _, _) =>
        name == "libmpv-2.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);
Player.Init(IntPtr.Zero, false);
var loop = Task.Run(Player.EventLoop);
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
async Task WaitFor(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    Check(condition(), "Playback state ready");
}
try
{
    string source = url + (mode is "external" or "short-audio" or "corrupt" or "external-video" ? "/video.mp4" : "/dual.mp4");
    if (mode == "short-internal") source = url + "/short-dual.mp4";
    if (mode == "hls") source = url + "/master.m3u8";
    if (mode == "edl")
    {
        string Escape(string value) => "%" + System.Text.Encoding.UTF8.GetByteCount(value) + "%" + value;
        source = "edl://!new_stream;!no_clip;" + Escape(url + "/video.mp4") +
            ";!new_stream;!no_clip;" + Escape(url + "/audio1.m4a") +
            ";!new_stream;!no_clip;" + Escape(url + "/audio2.m4a");
    }
    Player.CommandV("loadfile", source);
    await WaitFor(() => Player.GetPropertyString("path") != "" && Player.GetPropertyDouble("duration") > 0);
    if (mode is "external" or "short-audio" or "corrupt" or "external-video")
    {
        Player.CommandV("audio-add", url + "/audio1.m4a", "select");
        await WaitFor(() => Player.GetPropertyInt("aid") == 1);
        await WaitFor(() => Player.GetPropertyBool("eof-reached"));
        if (mode == "external-video")
        {
            Player.CommandV("video-add", url + "/other.mp4", "select");
            await WaitFor(() => Player.GetPropertyInt("vid") == 2);
        }
        Player.CommandV("audio-add", url + (mode == "short-audio" ? "/short.m4a" : "/audio2.m4a"), "select");
        await WaitFor(() => Player.GetPropertyInt("aid") == 2);
    }
    else
    {
        Player.SetPropertyBool("pause", true);
        Player.CommandV("seek", "2", "absolute+exact");
        await WaitFor(() => !Player.GetPropertyBool("seeking"));
        Player.SetPropertyInt("aid", 2);
        await Task.Delay(150);
        if (mode == "holes")
        {
            Player.CommandV("drop-buffers");
            Player.SetPropertyLong("demuxer-max-back-bytes", 0);
            Player.CommandV("seek", "3", "absolute+exact");
            await WaitFor(() => !Player.GetPropertyBool("seeking"));
        }
    }
    Player.SetPropertyBool("pause", true);
    if (mode == "holes") Check(BufferedVideo.GetCompleteRange(Player.GetPropertyString("demuxer-cache-state")) == null,
        "Missing cache ranges require completion");
    if (mode is "delay" or "negative-delay") Player.SetPropertyDouble("audio-delay", mode == "delay" ? 0.37 : -0.37);
    var selection = BufferedVideo.Capture(Player, Player.PlaybackGeneration);
    Check(selection.Tracks.Single(t => t.Type == "audio").Source == (mode is "external" or "corrupt" or "external-video" ? url + "/audio2.m4a" : mode == "short-audio" ? url + "/short.m4a" : source),
        "Selected audio source captured");
    double position = Player.GetPropertyDouble("time-pos");
    if (mode is not ("external" or "short-audio" or "corrupt" or "external-video")) Player.SetPropertyInt("aid", 1);
    if (mode == "source-change")
    {
        Player.CommandV("loadfile", url + "/other.mp4");
        await WaitFor(() => Player.PlaybackGeneration != selection.Generation &&
            Player.GetPropertyString("path") == url + "/other.mp4" && Player.GetPropertyDouble("duration") > 0);
        Player.SetPropertyBool("pause", true);
        position = Player.GetPropertyDouble("time-pos");
    }
    int foregroundAudio = Player.GetPropertyInt("aid");
    if (mode == "watch-later")
    {
        string bookmarks = Path.Combine(config, "bookmarks");
        Directory.CreateDirectory(bookmarks);
        Player.SetPropertyString("watch-later-directory", bookmarks);
        Player.CommandV("write-watch-later-config");
        Check(Directory.GetFiles(bookmarks).Length > 0, "Native resume bookmark exists before download");
        selection.Options["watch-later-directory"] = bookmarks;
    }
    string destination = Path.Combine(output, "saved.mkv");
    File.WriteAllText(destination, "existing destination");
    using var canceled = new CancellationTokenSource();
    Task shutdown = Task.CompletedTask;
    var progress = new InlineProgress(message => {
        if (!message.StartsWith("Downloading")) return;
        if (mode == "cancel") canceled.CancelAfter(100);
        if (mode == "shutdown") shutdown = Task.Run(async () => {
            await Task.Delay(100);
            Player.CommandV("quit");
        });
    });
    if (mode is "failure" or "truncated" or "retry" or "corrupt")
    {
        Console.WriteLine("DOWNLOAD_READY");
        Check(Console.ReadLine() == "ready", "Server failure enabled");
    }
    try
    {
        await BufferedVideo.SaveAsync(Player, destination, selection, canceled.Token, progress).WaitAsync(TimeSpan.FromSeconds(35));
        Check(mode is not ("cancel" or "shutdown" or "failure" or "truncated"), "Expected download success");
        Check(new FileInfo(destination).Length > 1000, "Complete video and pinned selected audio saved");
    }
    catch (OperationCanceledException) when (mode is "cancel" or "shutdown")
    {
        Check(File.ReadAllText(destination) == "existing destination", "Cancellation preserves the destination");
    }
    catch (IOException error) when (mode is "failure" or "truncated")
    {
        Console.WriteLine("EXPECTED SOURCE ERROR: " + error.Message);
        Check(File.ReadAllText(destination) == "existing destination", "Failed or truncated download preserves the destination");
    }
    await shutdown;
    if (mode != "shutdown")
    {
        Check(Player.GetPropertyInt("aid") == foregroundAudio, "Export leaves foreground audio selection alone");
        Check(Math.Abs(Player.GetPropertyDouble("time-pos") - position) < 0.2, "Export leaves playback position alone");
        Check(Player.GetPropertyBool("pause"), "Export leaves pause state alone");
    }
    Check(!Directory.GetDirectories(output, ".mpvnet-save-*").Any(), "Export temporary files cleaned up");
}
finally
{
    if (Player.IsQuitNeeded) Player.CommandV("quit");
    await loop.WaitAsync(TimeSpan.FromSeconds(5));
    Player.Destroy();
}
class IdentityTranslator : ITranslator
{
    public string Gettext(string text) => text;
    public string GetParticularString(string context, string text) => text;
}

class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string message) => report(message);
}
