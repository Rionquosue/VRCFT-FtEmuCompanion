"""Sends a recording back out over OSC at its recorded times: recorded packets unchanged, recorded parameters as
/avatar/parameters messages, and visemes made from audio as Viseme (int) and Voice (float). Expression frames are for the
module's playback source and are not sent. VRChat ignores Viseme and Voice sent to it (it makes its own); FTEmulator in
Unity takes them."""

from __future__ import annotations

import socket
import threading
import time

from . import osc
from . import recording as rec


def run(recording: rec.Recording, port: int, loop: bool, cancel: threading.Event, log=print) -> None:
    entries = sorted((e for e in recording.entries if e.kind != rec.EXPRESSIONS), key=lambda e: e.time)
    if not entries:
        log("Nothing to send: the recording is empty." if not recording.entries else
            "Nothing to send: the recording has only expression frames (play those with the module's playback source).")
        return
    target = ("127.0.0.1", port)
    start = entries[0].time
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        def send(packet: bytes) -> None:
            try:
                sender.sendto(packet, target)
            except OSError:
                pass

        while True:
            began = time.perf_counter()
            for entry in entries:
                wait = entry.time - start - (time.perf_counter() - began)
                if wait > 0 and cancel.wait(wait):
                    return
                if cancel.is_set():
                    return
                if entry.kind == rec.OSC:
                    send(entry.packet)
                elif entry.kind == rec.PARAMETER:
                    value = int(entry.value) if entry.type == "i" else (entry.value != 0) if entry.type == "b" else float(entry.value)
                    send(osc.encode(osc.PARAMETER_PREFIX + entry.name, value))
                elif entry.kind == rec.VISEME:
                    send(osc.encode(osc.PARAMETER_PREFIX + "Viseme", int(entry.viseme)))
                    send(osc.encode(osc.PARAMETER_PREFIX + "Voice", float(entry.voice)))
            if not loop or cancel.is_set():
                return

# ============================================================================
# GLOSSARY: replay
# ============================================================================
#
# replay (this file)       run(recording, port, loop, cancel, log): every non-expression entry to 127.0.0.1:port at its
#                            time (from the first entry): OSC packets unchanged, parameters as /avatar/parameters/<name>
#                            (int, bool or float), viseme entries as Viseme (int) and Voice (float). loop restarts it.
