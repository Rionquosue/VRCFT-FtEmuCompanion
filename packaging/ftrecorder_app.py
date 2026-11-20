"""The compiled app's entry point: with no arguments it opens the Recorder; with arguments it is the companion command
line (process, replay, recorder), as python -m ftrecorder."""

import os
import sys


def _hide_own_console() -> None:
    """Windows: the .exe is a console program so the command line can print. Started on its own (double-clicked, or by
    SteamVR) it has a console of its own, which is closed; started from a terminal, the terminal is left alone."""
    if os.name != "nt":
        return
    import ctypes
    kernel32 = ctypes.WinDLL("kernel32")
    processes = (ctypes.c_uint * 2)()
    if kernel32.GetConsoleProcessList(processes, 2) == 1:
        kernel32.FreeConsole()


def main() -> None:
    if len(sys.argv) > 1:
        from ftrecorder.__main__ import main as command_line
        sys.exit(command_line(sys.argv[1:]))
    _hide_own_console()
    from ftrecorder.recorder import main as recorder
    recorder()


if __name__ == "__main__":
    main()

# ============================================================================
# GLOSSARY: compiled app entry point
# ============================================================================
#
# ftrecorder_app.py (this file)  PyInstaller's entry script (packaging/build.py).
#   main()                   Arguments -> ftrecorder.__main__.main (process, replay, recorder); none -> the Recorder.
#   _hide_own_console()      Windows: FreeConsole when this process is the only one on its console (not started from a
#                            terminal), so the Recorder opens without a console window behind it.
