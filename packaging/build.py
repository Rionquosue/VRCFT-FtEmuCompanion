"""Builds the compiled app with PyInstaller: dist/RiryTools-FTRecorder.exe on Windows (one file), and on Linux
dist/RiryTools-FTRecorder-x86_64.AppImage (PyInstaller's folder in an AppDir, packed by appimagetool).

    python packaging/build.py [--appimagetool PATH]

Run from anywhere with PyInstaller, numpy and onnxruntime installed; ffmpeg is not bundled (the user's own)."""

import argparse
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKAGING = os.path.join(ROOT, "packaging")
NAME = "RiryTools-FTRecorder"
BUILD = os.path.join(ROOT, "build")
DIST = os.path.join(ROOT, "dist")


def pyinstaller(one_file: bool) -> None:
    separator = os.pathsep
    data = [
        (os.path.join(ROOT, "Models"), "Models"),
        (os.path.join(ROOT, "NOTICE.md"), "."),
        (os.path.join(ROOT, "LICENSE"), "."),
    ]
    if os.name == "nt":
        data.append((os.path.join(ROOT, "ThirdParty", "OpenVR"), os.path.join("ThirdParty", "OpenVR")))
    arguments = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--name", NAME,
                 "--distpath", DIST, "--workpath", BUILD, "--specpath", BUILD, "--paths", ROOT,
                 "--collect-submodules", "ftrecorder", "--collect-binaries", "onnxruntime",
                 # readline (GPL) is not needed: the one console question works without it.
                 "--exclude-module", "readline",
                 "--onefile" if one_file else "--onedir"]
    if os.name == "nt":
        # Started on its own (double-clicked, or by SteamVR) the console window is hidden as the bootloader starts;
        # started from a terminal, the terminal is left alone and the command line prints there.
        arguments += ["--hide-console", "hide-early"]
    for source, target in data:
        arguments += ["--add-data", source + separator + target]
    arguments.append(os.path.join(PACKAGING, "ftrecorder_app.py"))
    subprocess.run(arguments, check=True)


def appimage(appimagetool: str) -> str:
    """dist/NAME (PyInstaller's folder) into an AppDir, then appimagetool. Returns the AppImage's path."""
    appdir = os.path.join(BUILD, NAME + ".AppDir")
    shutil.rmtree(appdir, ignore_errors=True)
    os.makedirs(os.path.join(appdir, "usr", "bin"))
    shutil.copytree(os.path.join(DIST, NAME), os.path.join(appdir, "usr", "bin", NAME))
    shutil.copy(os.path.join(PACKAGING, "ftrecorder.png"), os.path.join(appdir, "ftrecorder.png"))
    shutil.copy(os.path.join(PACKAGING, "ftrecorder.png"), os.path.join(appdir, ".DirIcon"))
    with open(os.path.join(appdir, "ftrecorder.desktop"), "w", encoding="utf-8") as desktop:
        desktop.write("[Desktop Entry]\nType=Application\nName=RiryTools FTRecorder\n"
                      "Comment=Records the avatar parameters VRChat sends over OSC\n"
                      "Exec=%s\nIcon=ftrecorder\nCategories=AudioVideo;Utility;\nTerminal=false\n" % NAME)
    app_run = os.path.join(appdir, "AppRun")
    with open(app_run, "w", encoding="utf-8", newline="\n") as script:
        script.write('#!/bin/sh\nHERE="$(dirname "$(readlink -f "$0")")"\nexec "$HERE/usr/bin/%s/%s" "$@"\n' % (NAME, NAME))
    os.chmod(app_run, 0o755)
    output = os.path.join(DIST, NAME + "-x86_64.AppImage")
    environment = dict(os.environ, ARCH="x86_64", APPIMAGE_EXTRACT_AND_RUN="1")
    subprocess.run([appimagetool, "--no-appstream", appdir, output], check=True, env=environment)
    return output


def main() -> None:
    parser = argparse.ArgumentParser(description="Builds the compiled RiryTools FTRecorder.")
    parser.add_argument("--appimagetool", default="appimagetool", help="appimagetool's path (Linux)")
    arguments = parser.parse_args()
    if os.name == "nt":
        pyinstaller(one_file=True)
        print(os.path.join(DIST, NAME + ".exe"))
    elif sys.platform.startswith("linux"):
        pyinstaller(one_file=False)
        print(appimage(arguments.appimagetool))
    else:
        sys.exit("Builds are made on Windows (.exe) and Linux (.AppImage).")


if __name__ == "__main__":
    main()

# ============================================================================
# GLOSSARY: build
# ============================================================================
#
# build.py (this file)     The compiled app, built by .github/workflows/build.yml (and by hand).
#   pyinstaller(one_file)    PyInstaller on packaging/ftrecorder_app.py with every ftrecorder module, onnxruntime's libraries, and the
#                            data Models/, NOTICE.md, LICENSE (and ThirdParty/OpenVR on Windows), laid out as in the
#                            repository so the code finds them; readline (GPL) left out. Windows: one file, its own
#                            console window hidden as it starts (--hide-console hide-early); Linux: a folder.
#   appimage(appimagetool)   AppDir: usr/bin/RiryTools-FTRecorder/ (the folder), AppRun, ftrecorder.desktop,
#                            ftrecorder.png and .DirIcon; packed (no AppStream data) into
#                            dist/RiryTools-FTRecorder-x86_64.AppImage.
#   Outputs                  dist/RiryTools-FTRecorder.exe (Windows), dist/RiryTools-FTRecorder-x86_64.AppImage (Linux).
