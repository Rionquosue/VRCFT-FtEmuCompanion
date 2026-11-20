"""The Recorder window (Tkinter). While it is open it relays VRCFaceTracking's OSC output to VRChat; it records only
between Start recording and Stop and save."""

from __future__ import annotations

import os
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from . import ffmpeg as ff
from . import session as sessions
from .relay import Relay

TITLE = "RiryTools FTRecorder Recorder"
_WINDOWS = os.name == "nt"


class RecorderWindow:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.calls: queue.Queue = queue.Queue()
        self.session: sessions.Session | None = None
        self.relay: Relay | None = None
        self.busy = False
        self.close_when_saved = False
        self.microphones: list[ff.Microphone] = []
        self._restart_pending = None

        root.title(TITLE)
        root.minsize(560, 460)
        frame = ttk.Frame(root, padding=16)
        frame.grid(sticky="nsew")
        root.columnconfigure(0, weight=1)
        root.rowconfigure(0, weight=1)
        frame.columnconfigure(1, weight=1)

        self.output = tk.StringVar(value=os.path.join(os.path.expanduser("~"), "Documents", "RiryTools FTRecorder"))
        self.in_port = tk.StringVar(value="9010")
        self.out_port = tk.StringVar(value="9000")
        self.microphone = tk.StringVar()
        self.record_audio = tk.BooleanVar(value=True)
        self.ffmpeg = tk.StringVar(value="ffmpeg")
        self.ovrlipsync = tk.StringVar()
        self.start_with_steamvr = tk.BooleanVar(value=False)
        self.status = tk.StringVar()

        row = 0
        self.fields = []

        def label(text):
            ttk.Label(frame, text=text).grid(row=row, column=0, sticky="w", padx=(0, 8), pady=4)

        def hint(text, column=2):
            widget = ttk.Label(frame, text=text, foreground="#666666", wraplength=360)
            widget.grid(row=row, column=column, sticky="w", padx=(8, 0))
            return widget

        label("Output")
        self.fields.append(ttk.Entry(frame, textvariable=self.output))
        self.fields[-1].grid(row=row, column=1, sticky="ew")
        self.fields.append(ttk.Button(frame, text="Browse…", command=self.choose_output))
        self.fields[-1].grid(row=row, column=2, sticky="w", padx=(8, 0))
        row += 1
        label("VRCFaceTracking OSC output port")
        self.fields.append(ttk.Spinbox(frame, from_=1, to=65535, textvariable=self.in_port, width=8))
        self.fields[-1].grid(row=row, column=1, sticky="w")
        hint("Set VRCFaceTracking's OSC output port to this.")
        row += 1
        label("Pass on to port")
        self.fields.append(ttk.Spinbox(frame, from_=1, to=65535, textvariable=self.out_port, width=8))
        self.fields[-1].grid(row=row, column=1, sticky="w")
        hint("VRChat's OSC input port (9000).")
        row += 1
        label("Microphone")
        self.microphone_box = ttk.Combobox(frame, textvariable=self.microphone, state="readonly", values=[])
        self.microphone_box.grid(row=row, column=1, sticky="ew")
        self.fields.append(self.microphone_box)
        self.no_microphone = ttk.Label(frame, text="! " + sessions.NO_MIC_VISEMES, foreground="#b45309", wraplength=360)
        self.no_microphone.grid(row=row, column=2, sticky="w", padx=(8, 0))
        self.no_microphone.grid_remove()
        self.microphone.set("Looking for microphones…")
        row += 1
        label("Audio")
        self.fields.append(ttk.Checkbutton(frame, text="Record audio", variable=self.record_audio))
        self.fields[-1].grid(row=row, column=1, sticky="w")
        hint("Off: the microphone only makes visemes (Windows, VRChat not running).")
        row += 1
        label("ffmpeg")
        self.fields.append(ttk.Entry(frame, textvariable=self.ffmpeg))
        self.fields[-1].grid(row=row, column=1, sticky="ew")
        self.fields.append(ttk.Button(frame, text="Browse…", command=self.choose_ffmpeg))
        self.fields[-1].grid(row=row, column=2, sticky="w", padx=(8, 0))
        row += 1
        label("OVRLipSync.dll")
        lipsync_entry = ttk.Entry(frame, textvariable=self.ovrlipsync)
        lipsync_entry.grid(row=row, column=1, sticky="ew")
        if _WINDOWS:
            self.fields.append(lipsync_entry)
            self.fields.append(ttk.Button(frame, text="Browse…", command=self.choose_ovrlipsync))
            self.fields[-1].grid(row=row, column=2, sticky="w", padx=(8, 0))
        else:
            lipsync_entry.configure(state="disabled")
            hint("! " + sessions.LINUX_VISEMES).configure(foreground="#b45309")
        row += 1
        label("SteamVR")
        self.steamvr_box = ttk.Checkbutton(frame, text="Start with SteamVR", variable=self.start_with_steamvr, command=self.toggle_steamvr)
        self.steamvr_box.grid(row=row, column=1, sticky="w")
        if not _WINDOWS:
            self.steamvr_box.configure(state="disabled")
        hint("SteamVR opens the Recorder when it starts.")
        row += 1
        controls = ttk.Frame(frame)
        controls.grid(row=row, column=0, columnspan=3, sticky="ew", pady=(8, 8))
        self.start_stop = ttk.Button(controls, text="Start recording", command=self.start_or_stop, width=18)
        self.start_stop.pack(side="left")
        ttk.Label(controls, textvariable=self.status).pack(side="left", padx=(12, 0))
        row += 1
        frame.rowconfigure(row, weight=1)
        self.log_view = ScrolledText(frame, height=10, wrap="word", state="disabled", font="TkDefaultFont")
        self.log_view.grid(row=row, column=0, columnspan=3, sticky="nsew")

        self.in_port.trace_add("write", lambda *_: self.schedule_relay_restart())
        self.out_port.trace_add("write", lambda *_: self.schedule_relay_restart())
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.restart_relay()
        self.root.after(50, self.pump)
        self.root.after(1000, self.tick)
        self.list_microphones()
        if _WINDOWS:
            self.in_background(self.load_steamvr)

    # Work off the window's thread posts back through calls; pump runs them on it.
    def pump(self):
        try:
            while True:
                self.calls.get_nowait()()
        except queue.Empty:
            pass
        self.root.after(50, self.pump)

    def post(self, call):
        self.calls.put(call)

    def in_background(self, work):
        threading.Thread(target=work, daemon=True).start()

    def log(self, text: str):
        """From any thread."""
        def write():
            self.log_view.configure(state="normal")
            if self.log_view.index("end-1c") != "1.0":
                self.log_view.insert("end", "\n")
            self.log_view.insert("end", text)
            self.log_view.see("end")
            self.log_view.configure(state="disabled")
        if threading.current_thread() is threading.main_thread():
            write()
        else:
            self.post(write)

    def tick(self):
        self.status.set(self.session.status() if self.session else self.relay_status())
        self.root.after(1000, self.tick)

    def relay_status(self) -> str:
        relay = self.relay
        return "Not relaying" if relay is None else "Relaying %d → %d: %d packets" % (relay.in_port, relay.out_port, relay.packets)

    # The relay runs whenever the window is open: restarted when a port changes (the ports are locked while recording).
    def schedule_relay_restart(self):
        if self._restart_pending is not None:
            self.root.after_cancel(self._restart_pending)
        self._restart_pending = self.root.after(400, self.restart_relay)

    def restart_relay(self):
        self._restart_pending = None
        if self.session is not None:
            return
        if self.relay is not None:
            self.relay.close()
            self.relay = None
        try:
            in_port, out_port = int(self.in_port.get()), int(self.out_port.get())
            if not (1 <= in_port <= 65535 and 1 <= out_port <= 65535):
                raise ValueError("Ports are numbers from 1 to 65535.")
            self.relay = Relay(in_port, out_port, self.log)
            self.log("Relaying VRCFaceTracking's output from port %d to %d (set VRCFaceTracking's OSC output port to %d)." % (in_port, out_port, in_port))
        except (ValueError, RuntimeError) as error:
            self.log("Error: not relaying: %s" % error)

    def ffmpeg_path(self):
        return self.ffmpeg.get().strip() or None

    # The microphones ffmpeg lists; the first is chosen. None: the "!" notice, and recording without a microphone.
    def list_microphones(self):
        path = self.ffmpeg_path()

        def work():
            try:
                found = ff.list_microphones(path)
            except ff.FfmpegNotFound as error:
                self.log(str(error))
                found = []
            self.post(lambda: self.show_microphones(found))
        self.in_background(work)

    def show_microphones(self, found):
        chosen = self.microphone.get()
        self.microphones = found
        labels = [m.label for m in found]
        self.microphone_box.configure(values=labels)
        if labels:
            self.microphone.set(chosen if chosen in labels else labels[0])
            self.no_microphone.grid_remove()
        else:
            self.microphone.set("No microphone found")
            self.no_microphone.grid()

    def chosen_microphone(self):
        return next((m for m in self.microphones if m.label == self.microphone.get()), None)

    def set_fields(self, enabled: bool):
        for field in self.fields:
            field.configure(state=("readonly" if field is self.microphone_box else "normal") if enabled else "disabled")

    def start_or_stop(self):
        if self.busy:
            return
        self.busy = True
        self.start_stop.configure(state="disabled")
        if self.session is None:
            self.start()
        else:
            self.stop()

    def done(self):
        self.busy = False
        self.start_stop.configure(state="normal")
        if self.close_when_saved and self.session is None:
            self.root.destroy()

    def start(self):
        path = self.ffmpeg_path()
        settings = sessions.Settings(self.output.get().strip(), self.chosen_microphone(), self.record_audio.get(), path, self.ovrlipsync.get().strip() or None)
        self.set_fields(False)

        def work():
            # ffmpeg writes the .mkv: without it, a popup and nothing else (no recording, no folder, no file).
            try:
                ff.check(path)
            except ff.FfmpegNotFound:
                self.post(self.ffmpeg_missing)
                return
            try:
                session = sessions.Session.start(settings, self.relay, self.ask_ovrlipsync, self.log)
            except (ValueError, RuntimeError, OSError) as error:
                self.log("Error: %s" % error)
                self.post(lambda: (self.set_fields(True), self.done()))
                return
            self.post(lambda: self.started(session))
        self.in_background(work)

    def ffmpeg_missing(self):
        messagebox.showerror("FFmpeg not found", "FFmpeg not found", parent=self.root,
                             detail='Looked for "%s". Install ffmpeg from https://ffmpeg.org/download.html and add it to PATH, '
                                    "or set its full path in the ffmpeg field." % (self.ffmpeg_path() or "ffmpeg"))
        self.set_fields(True)
        self.done()

    def started(self, session):
        self.session = session
        self.start_stop.configure(text="Stop and save")
        self.done()

    def stop(self):
        stopping = self.session
        self.status.set("Saving…")

        def work():
            try:
                stopping.stop()
            except (RuntimeError, OSError) as error:
                # The window stays open so the error (and where the recording was kept) can be read.
                self.log("Error: %s" % error)
                self.close_when_saved = False
            self.post(self.stopped)
        self.in_background(work)

    def stopped(self):
        self.session = None
        self.start_stop.configure(text="Start recording")
        self.status.set(self.relay_status())
        self.set_fields(True)
        self.done()

    # Closing while recording stops and saves first.
    def on_close(self):
        if self.session is None and not self.busy:
            if self.relay is not None:
                self.relay.close()
            self.root.destroy()
            return
        if self.close_when_saved:
            return
        self.close_when_saved = True
        self.log("Saving the recording before closing…")
        if self.session is not None and not self.busy:
            self.start_or_stop()

    def ask_ovrlipsync(self, reason: str):
        """From the session's thread when OVRLipSync cannot be found or loaded: the error, then a file picker;
        cancelling it records without visemes."""
        answer = {}
        ready = threading.Event()

        def ask():
            self.log("Error: " + reason)
            self.log("Choose OVRLipSync.dll (cancel to record without visemes).")
            path = filedialog.askopenfilename(parent=self.root, title="OVRLipSync.dll", filetypes=[("OVRLipSync.dll", "*.dll")])
            if path:
                self.ovrlipsync.set(path)
            answer["path"] = path or None
            ready.set()
        self.post(ask)
        ready.wait()
        return answer["path"]

    def choose_output(self):
        path = filedialog.askdirectory(parent=self.root, title="Output", initialdir=self.output.get() or None)
        if path:
            self.output.set(path)

    def choose_ffmpeg(self):
        path = filedialog.askopenfilename(parent=self.root, title="ffmpeg")
        if path:
            self.ffmpeg.set(path)
            self.list_microphones()

    def choose_ovrlipsync(self):
        path = filedialog.askopenfilename(parent=self.root, title="OVRLipSync.dll", filetypes=[("OVRLipSync.dll", "*.dll")])
        if path:
            self.ovrlipsync.set(path)

    # SteamVR's own setting while it runs; otherwise whether the Recorder's manifest is there (it is removed when
    # unchecked), so SteamVR is not started just to look.
    def load_steamvr(self):
        from . import steamvr
        try:
            on = steamvr.is_auto_launch()
        except RuntimeError as error:
            self.log("Error: SteamVR: %s" % error)
            on = None
        if on is None:
            on = os.path.isfile(steamvr.manifest_path())
        self.post(lambda: self.start_with_steamvr.set(on))

    def toggle_steamvr(self):
        from . import steamvr
        on = self.start_with_steamvr.get()
        self.steamvr_box.configure(state="disabled")

        def work():
            try:
                steamvr.set_auto_launch(on)
                self.log("SteamVR will start the Recorder (registered %s)." % steamvr.manifest_path() if on else "SteamVR will no longer start the Recorder.")
            except (RuntimeError, OSError) as error:
                self.log("Error: SteamVR: %s" % error)
                self.post(lambda: self.start_with_steamvr.set(not on))
            self.post(lambda: self.steamvr_box.configure(state="normal"))
        self.in_background(work)


def main() -> None:
    if _WINDOWS:
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)  # sharp text on scaled displays
        except (AttributeError, OSError):
            pass
    root = tk.Tk()
    RecorderWindow(root)
    root.mainloop()

# ============================================================================
# GLOSSARY: Recorder window
# ============================================================================
#
# recorder (this file)     The Recorder's only window (Tkinter); main() opens it.
#   Fields                   Output (folder, Documents/RiryTools FTRecorder; each recording named by session.output_name
#                            when it starts), VRCFaceTracking OSC output port (9010), Pass on to port (9000), Microphone
#                            (ffmpeg.list_microphones, first chosen; none: "! Visemes will not be recorded without a mic"
#                            beside it, and recording is still allowed), Record audio (on), ffmpeg, OVRLipSync.dll (empty:
#                            VRChat's copy; Linux: disabled, "! Visemes are only available when VRChat is running."),
#                            Start with SteamVR (Windows). Fields are locked while recording.
#   Relay                    relay.Relay from the first port to the second whenever the window is open (restarted 0.4 s
#                            after a port changes; closed with the window). The status line shows its packets.
#   Start recording          ffmpeg checked first: missing -> the "FFmpeg not found" popup and nothing else. Then
#                            session.Session.start off the window's thread; the counts every second.
#   Stop and save            session.Session.stop off the window's thread; errors in the log.
#   ask_ovrlipsync(reason)   The error in the log, then a file picker; cancel records without visemes.
#   Closing                  While recording: stops and saves, then closes; stays open when saving fails.
#   Threads                  Background work posts calls; pump runs them on the window's thread every 50 ms.
