using System.Globalization;
using System.Runtime.InteropServices;
using MpvNet;
using static MpvNet.Global;
using static MpvNet.Native.LibMpv;

Translator.Current = new IdentityTranslator();

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

Check(BufferedVideo.GetCompleteRange("") == null, "No cache is rejected");
Check(BufferedVideo.GetCompleteRange("{}") == null, "Unavailable cache is rejected");
Check(BufferedVideo.GetCompleteRange("""
    {"bof-cached":false,"eof-cached":true,"seekable-ranges":[{"start":1,"end":10}]}
    """) == null, "Discarded beginning is rejected");
Check(BufferedVideo.GetCompleteRange("""
    {"bof-cached":true,"eof-cached":false,"seekable-ranges":[{"start":0,"end":10}]}
    """) == null, "Unbuffered ending is rejected");
Check(BufferedVideo.GetCompleteRange("""
    {"bof-cached":true,"eof-cached":true,"seekable-ranges":[{"start":0,"end":2},{"start":4,"end":10}]}
    """) == null, "Seek gaps are rejected");
Check(BufferedVideo.GetCompleteRange("""
    {"bof-cached":true,"eof-cached":true,"seekable-ranges":[]}
    """) == null, "Empty ranges are rejected");
Check(BufferedVideo.GetCompleteRange("""
    {"bof-cached":true,"eof-cached":true,"seekable-ranges":[{"start":0,"end":0}]}
    """) == null, "Empty duration is rejected");
Check(BufferedVideo.GetCompleteRange("""
    {"bof-cached":true,"eof-cached":true,"seekable-ranges":[{"start":42.5,"end":46.75}]}
    """) == (42.5, 46.75), "Complete non-zero timestamp range is accepted");
Check(InputHelp.GetDefaults().Any(binding => binding.Command == "script-message-to mpvnet save-buffered-video"),
    "Default context menu includes Save Buffered Video");
Check(BufferedVideo.HasExternalMedia("""
    [{"type":"audio","external":true,"selected":true}]
    """), "Separate selected audio is rejected");
Check(!BufferedVideo.HasExternalMedia("""
    [{"type":"sub","external":true,"selected":true},{"type":"audio","external":true,"selected":false}]
    """), "External subtitles and unselected audio do not block export");

// Optional end-to-end test: serve a short video over HTTP and pass its URL and an output directory.
if (args.Length == 0)
    return;

string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
string config = Path.Combine(output, "config");
Directory.CreateDirectory(config);
Environment.SetEnvironmentVariable("MPVNET_HOME", config);
File.WriteAllText(Path.Combine(config, "mpv.conf"), "vo=null\nao=null\nhwdec=no\n");

string? native = Environment.GetEnvironmentVariable("MPVNET_TEST_LIBMPV");
if (native != null)
    NativeLibrary.SetDllImportResolver(typeof(MpvClient).Assembly, (name, assembly, path) =>
        name == "libmpv-2.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);

Player.Init(IntPtr.Zero, false);
var loop = Task.Run(Player.EventLoop);

try
{
    Check(Player.GetPropertyBool("cache-on-disk"), "Disk caching is enabled by default");
    Check(Player.GetPropertyLong("demuxer-max-back-bytes") == 512L * 1024 * 1024,
        "Back buffer retains 512 MiB of packet metadata");

    Player.CommandV("loadfile", args[0]);
    DateTime deadline = DateTime.UtcNow.AddSeconds(20);
    while (!Player.GetPropertyBool("eof-reached") && DateTime.UtcNow < deadline)
        await Task.Delay(50);

    Check(Player.GetPropertyBool("eof-reached"), "Playback reaches EOF without unloading the video");
    Check(Player.GetPropertyString("keep-open") == "always", "Network video stays open at EOF");
    Check(BufferedVideo.GetCompleteRange(Player.GetPropertyString("demuxer-cache-state")) != null,
        "The complete watched network video remains cached");
    if (Environment.GetEnvironmentVariable("MPVNET_TEST_OFFLINE") == "1")
        Check(Console.ReadLine() == "offline", "HTTP server is offline before saving");

    long generation = Player.PlaybackGeneration;
    string destination = Path.Combine(output, "saved فیلم with spaces.mkv");
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    await BufferedVideo.SaveAsync(Player, destination, generation).WaitAsync(TimeSpan.FromSeconds(10));
    Check(new FileInfo(destination).Length > 1000, "Cached video is exported using locale-independent timestamps");

    string protectedFile = Path.Combine(output, "existing.mkv");
    File.WriteAllText(protectedFile, "existing destination");
    try
    {
        await BufferedVideo.SaveAsync(Player, protectedFile, generation - 1);
        throw new Exception("Expected changed-file rejection");
    }
    catch (InvalidOperationException) { }
    Check(File.ReadAllText(protectedFile) == "existing destination", "Changing files preserves the destination");

    string invalidFormat = Path.Combine(output, "existing.unsupported-format");
    File.WriteAllText(invalidFormat, "existing destination");
    try
    {
        await BufferedVideo.SaveAsync(Player, invalidFormat, generation);
        throw new Exception("Expected muxer failure");
    }
    catch (IOException) { }
    Check(File.ReadAllText(invalidFormat) == "existing destination", "Muxer failure preserves the destination");
    Check(!Directory.GetFiles(output, "*.partial.*").Any(), "Failed exports leave no temporary files");

    var invalidCommand = await Player.CommandVAsync("not-a-real-command").WaitAsync(TimeSpan.FromSeconds(5));
    Check(invalidCommand < 0, "Asynchronous command errors reach the caller");

    Player.CommandV("stop");
    deadline = DateTime.UtcNow.AddSeconds(5);
    while (Player.GetPropertyString("path") != "" && DateTime.UtcNow < deadline)
        await Task.Delay(50);
    Check(Player.PlaybackGeneration != generation, "Stopping invalidates an open save dialog");
    try
    {
        await BufferedVideo.SaveAsync(Player, protectedFile, Player.PlaybackGeneration);
        throw new Exception("Expected incomplete-cache rejection");
    }
    catch (InvalidOperationException) { }
    Check(File.ReadAllText(protectedFile) == "existing destination", "Incomplete cache preserves the destination");

    Check(Player.GetPropertyString("keep-open") == "no", "Network keep-open setting is restored after stop");
}
finally
{
    Player.CommandV("quit");
    await loop.WaitAsync(TimeSpan.FromSeconds(5));
    Player.Destroy();
}

class IdentityTranslator : ITranslator
{
    public string Gettext(string text) => text;
    public string GetParticularString(string context, string text) => text;
}
