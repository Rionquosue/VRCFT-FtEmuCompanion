"""The companion command line: python -m ftrecorder process|replay|recorder."""

from __future__ import annotations

import argparse
import os
import signal
import sys
import threading

from . import VERSION, captions, lipsync, matroska, vrchat
from . import ffmpeg as ff

DEFAULT_CABLE = "CABLE Input"

DESCRIPTION = """RiryTools FTRecorder companion.

  recorder   Opens the Recorder window (also: Recorder.pyw).
  process    Runs a recorded video through the face models (MediaPipe; Babble too with --babble-model) into one
             Matroska file: the video itself, its sound as audio, the visemes as a subtitle track, and the whole
             recording (face data and visemes) attached. Until it is written the data is kept in OUT.recording.ftrec.jsonl.
             Visemes: while VRChat is running (Windows), the video's sound is played into VRChat through a virtual audio
             cable (--cable; set VRChat's microphone to the cable's output) and the Viseme and Voice VRChat sends over OSC
             are recorded, at the video's own speed. When VRChat is not running, the sound goes through OVRLipSync
             (Windows): --ovrlipsync, else your VRChat install; when it cannot be found or loaded, you are asked for its
             path. --visemes ovrlipsync or classifier picks an engine whether VRChat runs or not; classifier is
             FTRecorder's own. On Linux, visemes come only from --visemes classifier.
  replay     Sends recorded OSC, VRChat parameters and visemes to 127.0.0.1:PORT at their times. Expression frames are
             played by the VRCFaceTracking module's playback source instead.

Every command that uses ffmpeg checks for it first and says how to install it when it is missing."""


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="python -m ftrecorder", description=DESCRIPTION, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version="RiryTools FTRecorder " + VERSION)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("recorder", help="open the Recorder window")
    process = commands.add_parser("process", help="a recorded video into one .mkv")
    process.add_argument("--video", required=True)
    process.add_argument("--out", required=True, help="the .mkv to write")
    process.add_argument("--ffmpeg")
    process.add_argument("--babble-model")
    process.add_argument("--babble-labels")
    process.add_argument("--babble-crop", type=float, default=1.0)
    process.add_argument("--visemes", choices=["ovrlipsync", "classifier"])
    process.add_argument("--ovrlipsync")
    process.add_argument("--cable", default=DEFAULT_CABLE)
    process.add_argument("--fps", type=float, default=30.0)
    process.add_argument("--size", type=int, default=640)
    process.add_argument("--threads", type=int, default=max(1, (os.cpu_count() or 2) // 2))
    replay = commands.add_parser("replay", help="send a recording over OSC")
    replay.add_argument("--in", dest="input", required=True)
    replay.add_argument("--port", type=int, default=9000)
    replay.add_argument("--loop", action="store_true")
    replay.add_argument("--ffmpeg")
    arguments = parser.parse_args(argv)

    if arguments.command == "recorder":
        from .recorder import main as recorder
        recorder()
        return 0
    cancel = threading.Event()
    signal.signal(signal.SIGINT, lambda *_: cancel.set())
    try:
        if arguments.command == "process":
            return _process(arguments, cancel)
        return _replay(arguments, cancel)
    except ff.Cancelled:
        print("Stopped.", file=sys.stderr)
        return 1
    except (ValueError, RuntimeError, OSError, lipsync.LipSyncError) as error:
        print(error, file=sys.stderr)
        return 1


def _check_range(name, value, low, high):
    if not low <= value <= high:
        raise ValueError("%s must be a number from %s to %s." % (name, low, high))


def _process(arguments, cancel) -> int:
    from . import process as processing
    from .recording import Recording

    output = arguments.out
    data = output + ".recording.ftrec.jsonl"
    _check_range("--babble-crop", arguments.babble_crop, 0.1, 10)
    _check_range("--fps", arguments.fps, 1, 240)
    _check_range("--size", arguments.size, 128, 1920)
    _check_range("--threads", arguments.threads, 1, 64)
    if not os.path.isfile(arguments.video):
        raise FileNotFoundError("The video was not found: " + arguments.video)
    for path in (output, data):
        if os.path.exists(path):
            raise FileExistsError("Already exists: " + path)
    ff.check(arguments.ffmpeg)
    options = processing.Options(
        video=arguments.video, output=data, ffmpeg=arguments.ffmpeg, babble_model=arguments.babble_model,
        babble_labels=arguments.babble_labels, babble_crop=arguments.babble_crop, ovrlipsync=arguments.ovrlipsync,
        engine=arguments.visemes, ask_for_ovrlipsync=_ask_path if sys.stdin.isatty() else None,
        fps=arguments.fps, size=arguments.size, threads=arguments.threads)
    # An engine named with --visemes is used whether VRChat runs or not; otherwise VRChat, else OVRLipSync (Windows).
    if arguments.visemes is None:
        if os.name != "nt":
            print("No visemes: OVRLipSync and playing into VRChat need Windows. --visemes classifier makes FTRecorder's own.")
        elif vrchat.is_running():
            print("VRChat is running: its visemes are recorded while the video's sound plays into it.")
            options.vrchat_visemes = processing.capture_vrchat_visemes(arguments.ffmpeg, arguments.video, arguments.cable, print, cancel)
        else:
            options.engine = "ovrlipsync"
    try:
        print(processing.process(options, print, cancel))
    except ff.Cancelled:
        print("Stopped; the recording so far is in " + data if os.path.exists(data) else "Stopped.", file=sys.stderr)
        return 1
    print("Writing " + output)
    try:
        matroska.write_with_video(arguments.ffmpeg, data, arguments.video, captions.build(Recording.load(data)), output)
    except (RuntimeError, OSError) as error:
        raise RuntimeError("%s The recording is kept in %s." % (error, data)) from error
    os.remove(data)
    print("Saved " + output)
    return 0


def _ask_path(reason: str):
    """The error, then a typed path; an empty line gives up."""
    print("Error: " + reason, file=sys.stderr)
    try:
        return input("Path to OVRLipSync.dll (empty to stop): ")
    except EOFError:
        return None


def _replay(arguments, cancel) -> int:
    from . import replay
    from .recording import Recording

    if os.path.isfile(arguments.input) and matroska.is_matroska(arguments.input):
        ff.check(arguments.ffmpeg)
    recording = Recording.load(arguments.input, arguments.ffmpeg)
    _check_range("--port", arguments.port, 1, 65535)
    print("Replaying %d entries (%.1f s) to 127.0.0.1:%d. Ctrl+C stops." % (len(recording.entries), recording.duration, arguments.port))
    replay.run(recording, arguments.port, arguments.loop, cancel)
    return 0


if __name__ == "__main__":
    sys.exit(main())

# ============================================================================
# GLOSSARY: command line
# ============================================================================
#
# __main__ (this file)     python -m ftrecorder ...
#   recorder                 recorder.main(): the Recorder window.
#   process --video FILE --out FILE.mkv [--ffmpeg] [--babble-model] [--babble-labels] [--babble-crop 1]
#           [--visemes ovrlipsync|classifier] [--ovrlipsync] [--cable "CABLE Input"] [--fps 30] [--size 640]
#           [--threads half the cores]
#                            ffmpeg checked first. Visemes: --visemes when given; else Windows: VRChat running ->
#                            process.capture_vrchat_visemes, not running -> OVRLipSync (asked for in the console when
#                            missing); Linux: none (said so). Working file OUT.recording.ftrec.jsonl, then
#                            matroska.write_with_video with captions; deleted once written, kept if not.
#   replay --in FILE [--port 9000] [--loop] [--ffmpeg]  replay.run (an .mkv read through ffmpeg).
#   Exit codes               0 done; 1 bad arguments, a fault, or stopped (Ctrl+C).
