using System.Globalization;
using System.Runtime.InteropServices;
using MpvNet;
using static MpvNet.Global;

Translator.Current = new IdentityTranslator();

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

async Task Until(Func<bool> condition)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
    while (!condition() && DateTime.UtcNow < deadline)
        await Task.Delay(25);
    if (!condition())
        Console.WriteLine($"STATE: time={Player.GetPropertyString("time-pos")}, pause={Player.GetPropertyString("pause")}, seeking={Player.GetPropertyString("seeking")}, eof={Player.GetPropertyString("eof-reached")}, buffering={Player.GetPropertyString("paused-for-cache")}, generation={Player.PlaybackGeneration}");
    Check(condition(), "Wait completed");
}

Check(InputHelp.GetDefaults().First().Command == InputHelp.ContinueCommand, "Continue is the first root menu item");
var legacy = new InputConf("") { Content = "o ignore #menu: File > Open\nCtrl+c script-message-to mpvnet continue #menu: Playback > Continue\n" };
var bindings = legacy.GetBindings().menuBindings;
Check(bindings.First().Comment == "Continue" && bindings.First().Input == "Ctrl+c" &&
    bindings.Count(binding => binding.Command == InputHelp.ContinueCommand) == 1,
    "Legacy menu places Continue first without losing its key binding");

if (args.Length == 0)
{
    string directory = Path.Combine(Path.GetTempPath(), "mpvnet-recovery-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string checkpoint = Path.Combine(directory, "continue.json");
    try
    {
        var first = new PlaybackCheckpoint { Path = "https://example.test/فیلم?token=a,b", Position = 12.125 };
        first.Options["http-header-fields"] = "X-Title: فارسی, X-Token: a%b";
        PlaybackRecovery.WriteCheckpoint(checkpoint, first);
        Check(PlaybackRecovery.ReadCheckpoint(checkpoint)?.Options["http-header-fields"] == first.Options["http-header-fields"],
            "Unicode, punctuation and request settings round-trip");
        PlaybackRecovery.WriteCheckpoint(checkpoint, new PlaybackCheckpoint { Path = first.Path, Position = 15.25 });
        File.WriteAllText(checkpoint, "{interrupted");
        Check(PlaybackRecovery.ReadCheckpoint(checkpoint)?.Position == 12.125, "Corrupt checkpoint falls back to the previous complete file");
        File.WriteAllText(checkpoint + ".bak", "null");
        Check(PlaybackRecovery.ReadCheckpoint(checkpoint) == null, "Invalid primary and backup are rejected safely");
        File.Delete(checkpoint + ".bak");
        File.Delete(checkpoint);
        PlaybackRecovery.WriteCheckpoint(checkpoint, first);
        byte[] intact = File.ReadAllBytes(checkpoint);
        Directory.CreateDirectory(checkpoint + ".bak");
        try
        {
            PlaybackRecovery.WriteCheckpoint(checkpoint, new PlaybackCheckpoint { Path = first.Path, Position = 20 });
            throw new Exception("Expected checkpoint replacement to fail");
        }
        catch (IOException) { }
        Check(File.ReadAllBytes(checkpoint).SequenceEqual(intact), "Failed replacement preserves the previous checkpoint");
        Check(!Directory.GetFiles(directory, "*.tmp").Any(), "Atomic writes leave no temporary files");
    }
    finally { Directory.Delete(directory, true); }
    return;
}

string mode = args[0];
string config = Path.GetFullPath(args[1]);
Directory.CreateDirectory(config);
Environment.SetEnvironmentVariable("MPVNET_HOME", config);
string? native = Environment.GetEnvironmentVariable("MPVNET_TEST_LIBMPV");
if (native != null)
    NativeLibrary.SetDllImportResolver(typeof(MpvClient).Assembly, (name, assembly, path) =>
        name == "libmpv-2.dll" ? NativeLibrary.Load(native) : IntPtr.Zero);

Player.Init(IntPtr.Zero, false);
var loop = Task.Run(Player.EventLoop);
long restartedGeneration = 0;
Player.PlaybackRestart += () => Interlocked.Exchange(ref restartedGeneration, Player.PlaybackGeneration);
var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
Player.Recovery.Failed += message => { Console.WriteLine("RECOVERY: " + message); failure.TrySetResult(message); };
Player.LogMessage += (level, message) => {
    if (level <= MpvNet.Native.LibMpv.mpv_log_level.MPV_LOG_LEVEL_WARN)
        Console.WriteLine("MPV: " + message.Trim());
};

try
{
    if (mode is "record" or "disconnect")
    {
        Player.SetPropertyString("http-header-fields", "X-Recovery: checkpoint-test");
        Player.CommandV("loadfile", args[2]);
        await Until(() => Player.GetPropertyDouble("time-pos") > 0.1);
        Player.SetPropertyBool("pause", true);
        Player.CommandV("write-watch-later-config"); // Deliberately stale engine resume point.
        Player.CommandV("sub-add", args[3]);
        Player.SetPropertyDouble("speed", 1.25);
        Player.SetPropertyDouble("volume", 37);
        Player.SetPropertyBool("mute", true);
        Player.SetPropertyDouble("audio-delay", 0.25);
        Player.SetPropertyDouble("sub-delay", -0.4);
        Player.SetPropertyDouble("video-zoom", 0.25);
        Player.CommandV("seek", "9.125", "absolute+exact");
        await Until(() => Math.Abs(Player.GetPropertyDouble("time-pos") - 9.125) < 0.1 && Player.Recovery.SaveCheckpoint(true));
        Check(!failure.Task.IsCompleted, "Checkpoint writes succeed during playback");
        Check(PlaybackRecovery.ReadCheckpoint(Path.Combine(config, "continue.json"))!.ExternalTracks.Any(), "External subtitles are checkpointed");
        if (mode == "record")
        {
            Console.WriteLine("CHECKPOINT_READY");
            await Task.Delay(Timeout.Infinite); // Runner kills this process without quit/finally.
        }
        else
        {
            Console.WriteLine("DISCONNECT_READY");
            Check(Console.ReadLine() == "offline", "Server is disconnected during active playback");
            Player.CommandV("drop-buffers"); // Force a new range read instead of consuming downloaded packets.
            Player.SetPropertyBool("pause", false);
        }
    }
    if (mode != "record")
    {
        string filename = Path.Combine(config, "continue.json");
        var saved = PlaybackRecovery.ReadCheckpoint(filename)!;
        byte[] previous = File.ReadAllBytes(filename);
        if (mode == "missing")
        {
            await Player.Recovery.ContinueAsync();
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(Player.GetPropertyBool("pause"), "Incomplete external-track recovery pauses playback");
            Check(File.ReadAllBytes(filename).SequenceEqual(previous), "Missing external tracks preserve the checkpoint");
            return;
        }
        if (mode is "retry" or "disconnect")
        {
            await Player.Recovery.ContinueAsync();
            previous = File.ReadAllBytes(filename);
            saved = PlaybackRecovery.ReadCheckpoint(filename)!;
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(File.ReadAllBytes(filename).SequenceEqual(previous), "Failed reconnect preserves the durable checkpoint");
            Console.WriteLine("RETRY_FAILED");
            Check(Console.ReadLine() == "online", "Network is restored before retry");
        }

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        await Player.Recovery.ContinueAsync();
        await Until(() => Player.GetPropertyDouble("time-pos") >= saved.Position - 0.05 &&
            Interlocked.Read(ref restartedGeneration) == Player.PlaybackGeneration && !Player.GetPropertyBool("seeking"));
        Player.SetPropertyBool("pause", true);
        Check(Player.GetPropertyDouble("time-pos") < saved.Position + 0.5, "Resume starts at the fractional saved position");
        Check(Player.GetPropertyString("path") == saved.Path, "Original URL is reopened");
        Check(Player.GetPropertyDouble("speed") == 1.25 && Player.GetPropertyDouble("volume") == 37 &&
            Player.GetPropertyBool("mute"), "Speed, volume and mute are restored after a crash");
        double audioDelay = Player.GetPropertyDouble("audio-delay");
        double subDelay = Player.GetPropertyDouble("sub-delay");
        double zoom = Player.GetPropertyDouble("video-zoom");
        Check(Math.Abs(audioDelay - 0.25) < 0.000001 && Math.Abs(subDelay + 0.4) < 0.000001 &&
            Math.Abs(zoom - 0.25) < 0.000001, $"Sync delays and video zoom are restored ({audioDelay}, {subDelay}, {zoom})");
        Check(Player.GetPropertyString("http-header-fields").Contains("X-Recovery: checkpoint-test"), "Request headers are restored before reconnecting");
        Check(Player.GetPropertyInt("sid") > 0 && Player.GetPropertyString("track-list").Contains("external-filename"),
            "External subtitles and their selected track are restored");
        long generation = Player.PlaybackGeneration;
        await Player.Recovery.ContinueAsync();
        Check(!Player.GetPropertyBool("pause") && Player.PlaybackGeneration == generation,
            "Continue unpauses healthy playback without discarding its buffer");
    }
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
