"""Selected external/internal audio, missing buffers, cancellation and failed-source tests."""
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time

project = Path(__file__).resolve().parent / "StreamCompletion.csproj"
dotnet = os.environ.get("DOTNET", "dotnet")
subprocess.run([dotnet, "build", str(project), "--nologo", "-v:q"], check=True)


def run(*args):
    return subprocess.check_output(["ffmpeg", "-v", "error", *map(str, args)])


def frames(path):
    return [line.split(b",")[-1].strip() for line in run("-i", path, "-map", "0:v:0", "-f", "framemd5", "-").splitlines()
            if not line.startswith(b"#")]


def audio(path, index=0):
    data = run("-i", path, "-map", f"0:a:{index}", "-c", "copy", "-f", "adts", "-")
    packets = []
    while data:
        length = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5)
        assert 7 < length <= len(data)
        packets.append(data[7:length])
        data = data[length:]
    return packets


with tempfile.TemporaryDirectory(prefix="mpvnet-completion-") as directory:
    root = Path(directory)
    run("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24", "-f", "lavfi", "-i",
        "sine=frequency=440:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
        "-t", "4", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast",
        "-g", "24", "-c:a", "aac", "-movflags", "+faststart", root / "dual.mp4")
    run("-i", root / "dual.mp4", "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", root / "video.mp4")
    for index in [1, 2]:
        run("-i", root / "dual.mp4", "-map", f"0:a:{index-1}", "-c", "copy", "-movflags", "+faststart", root / f"audio{index}.m4a")
    run("-i", root / "audio2.m4a", "-t", "3", "-c", "copy", root / "short.m4a")
    run("-i", root / "dual.mp4", "-map", "0:v", "-map", "0:a:0", "-map", "0:a:1", "-c", "copy",
        "-f", "hls", "-hls_time", "1", "-hls_playlist_type", "vod", "-master_pl_name", "master.m3u8",
        "-var_stream_map", "v:0,agroup:aud a:0,agroup:aud,name:one,language:eng,default:yes a:1,agroup:aud,name:two,language:fas",
        "-hls_segment_filename", root / "segment_%v_%03d.ts", root / "stream_%v.m3u8")
    run("-i", root / "dual.mp4", "-i", root / "short.m4a", "-map", "0:v", "-map", "0:a:0", "-map", "1:a:0",
        "-c", "copy", "-movflags", "+faststart", root / "short-dual.mp4")
    run("-f", "lavfi", "-i", "color=c=red:size=160x90:rate=24", "-t", "4", "-c:v", "libx264",
        "-preset", "ultrafast", "-movflags", "+faststart", root / "other.mp4")
    packet_info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_packets", "-show_entries", "packet=pos,size,pts_time", "-of", "json", str(root / "video.mp4")]))
    packet = next(p for p in packet_info["packets"] if float(p["pts_time"]) >= 2)
    damaged = bytearray((root / "video.mp4").read_bytes())
    offset, end = int(packet["pos"]), int(packet["pos"]) + int(packet["size"])
    while offset + 5 < end:
        length = int.from_bytes(damaged[offset:offset+4], "big")
        kind = damaged[offset+4] & 31
        if kind in [1, 5] and length > 100:
            damaged[offset+5:offset+4+length] = b"\x00" * (length-1)
            break
        offset += length + 4
    else:
        raise AssertionError("Expected a slice NAL in the fixture")
    bad = root / "damaged.mp4"
    bad.write_bytes(damaged)
    check_damage = subprocess.run(["ffmpeg", "-v", "error", "-xerror", "-err_detect", "explode", "-i", str(bad), "-f", "null", "-"],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    assert check_damage.returncode != 0 or check_damage.stderr.strip(), "Fixture corruption must cause a real decoding failure"
    original_frames = frames(root / "dual.mp4")
    expected_audio = audio(root / "dual.mp4", 1)
    repaired = False
    online = True
    truncate = False
    slow = False
    requests = []
    failures = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append((self.path, self.headers.get("X-Export")))
            if not online:
                failures.append(self.path)
                if mode == "retry":
                    def reconnect():
                        global online
                        online = True
                    threading.Timer(0.1, reconnect).start()
                self.send_error(503)
                return
            if self.headers.get("X-Export") != "selected-audio":
                self.send_error(403)
                return
            data = bytes(damaged) if mode == "corrupt" and not repaired and self.path == "/video.mp4" else (root / self.path.removeprefix("/")).read_bytes()
            start, end = 0, len(data) - 1
            partial = self.headers.get("Range", "")
            if partial:
                first, last = partial.removeprefix("bytes=").split("-", 1)
                start, end = int(first), int(last) if last else end
                self.send_response(206)
                self.send_header("Content-Range", f"bytes {start}-{end}/{len(data)}")
            else:
                self.send_response(200)
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Type", "video/mp4")
            self.send_header("Content-Length", str(end - start + 1))
            self.end_headers()
            try:
                for offset in range(start, end + 1, 4096):
                    if truncate and offset > len(data) // 2:
                        return
                    self.wfile.write(data[offset:min(offset + 4096, end + 1)])
                    if slow:
                        time.sleep(0.05)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def log_message(self, *args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        for mode in os.environ.get("COMPLETION_TEST_MODES", "external,external-video,short-audio,short-internal,corrupt,internal,source-change,edl,hls,watch-later,holes,delay,negative-delay,cancel,shutdown,retry,failure,truncated").split(","):
            repaired = False
            online, truncate, slow = True, False, mode in ["cancel", "shutdown"]
            output = root / mode
            process = subprocess.Popen([dotnet, str(project.parent / "bin/Debug/net10.0/StreamCompletion.dll"),
                mode, f"http://127.0.0.1:{server.server_port}", str(output)],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            watchdog = threading.Timer(45, process.kill)
            watchdog.start()
            start = len(requests)
            failed_before = len(failures)
            try:
                for line in process.stdout:
                    print(mode + ": " + line.rstrip(), flush=True)
                    if "DOWNLOAD_READY" in line:
                        repaired = mode == "corrupt"
                        online, truncate = mode not in ["failure", "retry"], mode == "truncated"
                        process.stdin.write("ready\n")
                        process.stdin.flush()
                assert process.wait(timeout=5) == 0, f"{mode} failed"
            finally:
                watchdog.cancel()
                if process.poll() is None:
                    process.kill()
                    process.wait()
            assert all(header == "selected-audio" for _, header in requests[start:]), "Every download must keep authentication headers"
            if mode == "retry":
                assert len(failures) > failed_before, "Retry must recover from an actual failed HTTP request"
            if mode == "corrupt":
                assert sum(path == "/video.mp4" for path, _ in requests[start:]) > 1, f"Corrupt cached video must be repaired from the source: {requests[start:]}"
            if mode == "external":
                assert sum(path == "/video.mp4" for path, _ in requests[start:]) == 1, "Complete cached video must not be downloaded again"
            if mode in ["cancel", "shutdown", "failure", "truncated"]:
                continue
            saved = output / "saved.mkv"
            metadata = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(saved)]))
            assert [s["codec_type"] for s in metadata["streams"]] == ["video", "audio"]
            if mode in ["delay", "negative-delay"]:
                starts = [float(s["start_time"]) for s in metadata["streams"]]
                assert abs(starts[1] - starts[0] - (0.37 if mode == "delay" else -0.37)) < 0.05, "Selected audio delay must be preserved"
            assert frames(saved) == (frames(root / "other.mp4") if mode == "external-video" else original_frames), "All video frames must match, including the missing prefix"
            packets = audio(saved)
            selected_audio = audio(root / "short.m4a") if mode in ["short-audio", "short-internal"] else expected_audio
            assert packets == selected_audio or packets == selected_audio[1:], "Every audio packet must come from selected audio 2"
            print(f"PASS: {mode}: complete video frames and audio 2 packets match the source", flush=True)
    finally:
        server.shutdown()
        server.server_close()
