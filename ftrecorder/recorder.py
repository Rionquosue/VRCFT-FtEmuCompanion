"""The Recorder window (Tkinter). Between Start recording and Stop and save it records every avatar parameter VRChat
sends over OSC, found through OSCQuery."""

from __future__ import annotations

import os
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from . import ffmpeg as ff
from . import session as sessions

TITLE = "RiryTools FTRecorder Recorder"
_WINDOWS = os.name == "nt"


class RecorderWindow:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.calls: queue.Queue = queue.Queue()
        self.session: sessions.Session | None = None
        self.busy = False
        self.close_when_saved = False

        root.title(TITLE)
        root.minsize(560, 360)
        frame = ttk.Frame(root, padding=16)
        frame.grid(sticky="nsew")
        root.columnconfigure(0, weight=1)
        root.rowconfigure(0, weight=1)
        frame.columnconfigure(1, weight=1)

        self.output = tk.StringVar(value=os.path.join(os.path.expanduser("~"), "Documents", "RiryTools FTRecorder"))
        self.ffmpeg = tk.StringVar(value="ffmpeg")
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
        label("ffmpeg")
        self.fields.append(ttk.Entry(frame, textvariable=self.ffmpeg))
        self.fields[-1].grid(row=row, column=1, sticky="ew")
        self.fields.append(ttk.Button(frame, text="Browse…", command=self.choose_ffmpeg))
        self.fields[-1].grid(row=row, column=2, sticky="w", padx=(8, 0))
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

        root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.after(50, self.pump)
        self.tick()
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
        self.status.set(self.session.status() if self.session else "Not recording")
        self.root.after(1000, self.tick)

    def ffmpeg_path(self):
        return self.ffmpeg.get().strip() or None

    def set_fields(self, enabled: bool):
        for field in self.fields:
            field.configure(state="normal" if enabled else "disabled")

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
        settings = sessions.Settings(self.output.get().strip(), self.ffmpeg_path())
        self.set_fields(False)

        def work():
            # Without ffmpeg (it writes the .mkv) or VRChat: a popup and nothing else (no recording, no folder, no file).
            try:
                session = sessions.Session.start(settings, self.log)
            except ff.FfmpegNotFound:
                self.post(self.ffmpeg_missing)
                return
            except sessions.VRChatNotRunning:
                self.post(self.vrchat_missing)
                return
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

    def vrchat_missing(self):
        messagebox.showerror(sessions.VRCHAT_NOT_RUNNING, sessions.VRCHAT_NOT_RUNNING, parent=self.root)
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
        self.status.set("Not recording")
        self.set_fields(True)
        self.done()

    # Closing while recording stops and saves first.
    def on_close(self):
        if self.session is None and not self.busy:
            self.root.destroy()
            return
        if self.close_when_saved:
            return
        self.close_when_saved = True
        self.log("Saving the recording before closing…")
        if self.session is not None and not self.busy:
            self.start_or_stop()

    def choose_output(self):
        path = filedialog.askdirectory(parent=self.root, title="Output", initialdir=self.output.get() or None)
        if path:
            self.output.set(path)

    def choose_ffmpeg(self):
        path = filedialog.askopenfilename(parent=self.root, title="ffmpeg")
        if path:
            self.ffmpeg.set(path)

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
#                            when it starts), ffmpeg, Start with SteamVR (Windows). Fields are locked while recording.
#   Start recording          session.Session.start off the window's thread: ffmpeg missing -> the "FFmpeg not found"
#                            popup, VRChat not running -> the "VRChat is not running!" popup, and nothing else. The status
#                            line counts the VRChat values every second ("Not recording" otherwise).
#   Stop and save            session.Session.stop off the window's thread; errors in the log.
#   Closing                  While recording: stops and saves, then closes; stays open when saving fails.
#   Threads                  Background work posts calls; pump runs them on the window's thread every 50 ms.
