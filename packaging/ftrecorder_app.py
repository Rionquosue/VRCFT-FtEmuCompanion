"""The compiled app's entry point: with no arguments it opens the Recorder; with arguments it is the companion command
line (process, replay, recorder), as python -m ftrecorder."""

import sys


def main() -> None:
    if len(sys.argv) > 1:
        from ftrecorder.__main__ import main as command_line
        sys.exit(command_line(sys.argv[1:]))
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
#                            Windows: the console window is hidden by the bootloader (packaging/build.py).
