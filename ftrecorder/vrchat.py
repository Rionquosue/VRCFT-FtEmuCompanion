"""VRChat on this computer: whether it is running, where its OVRLipSync library is, and a listener that records the
Viseme and Voice values it sends over OSC."""

from __future__ import annotations

import json
import os
import re
import socket
import struct
import sys
import threading
import time
import uuid

from . import dns, osc

APP_ID = "438100"
_LIBRARY_PATH = re.compile(r'"path"\s+"((?:[^"\\]|\\.)*)"')
_INSTALL_DIR = re.compile(r'"installdir"\s+"((?:[^"\\]|\\.)*)"')


def is_running() -> bool:
    """True while a VRChat process runs here (Windows: VRChat.exe; Linux: VRChat.exe under Proton or Wine)."""
    if os.name == "nt":
        return "vrchat.exe" in _windows_processes()
    try:
        for entry in os.listdir("/proc"):
            if not entry.isdigit():
                continue
            try:
                with open("/proc/" + entry + "/comm", encoding="utf-8", errors="replace") as comm:
                    if comm.read().strip() in ("VRChat.exe", "VRChat"):
                        return True
            except OSError:
                continue
    except OSError:
        pass
    return False


def _windows_processes() -> set[str]:
    import ctypes
    from ctypes import wintypes

    class Entry(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                    ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
                    ("szExeFile", ctypes.c_wchar * 260)]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel32.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
    names = set()
    if snapshot in (None, wintypes.HANDLE(-1).value):
        return names
    try:
        entry = Entry()
        entry.dwSize = ctypes.sizeof(Entry)
        more = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            names.add(entry.szExeFile.lower())
            more = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snapshot)
    return names


def find_ovrlipsync() -> tuple[str | None, list[str]]:
    """The OVRLipSync library of the first VRChat install found (through Steam), or None; and the VRChat folders
    looked in. Only files are read; nothing in VRChat's folder is changed."""
    searched = []
    for vrchat in _vrchat_folders():
        searched.append(vrchat)
        plugins = os.path.join(vrchat, "VRChat_Data", "Plugins")
        for candidate in (os.path.join(plugins, "x86_64", "OVRLipSync.dll"), os.path.join(plugins, "OVRLipSync.dll")):
            if os.path.isfile(candidate):
                return candidate, searched
        for folder, _, files in os.walk(plugins):
            for name in files:
                if "lipsync" in name.lower() and name.lower().endswith(".dll"):
                    return os.path.join(folder, name), searched
    return None, searched


def _vrchat_folders():
    seen = set()
    for steam in _steam_folders():
        for library in _libraries(steam):
            apps = os.path.join(library, "steamapps")
            folder = "VRChat"
            text = _read(os.path.join(apps, "appmanifest_" + APP_ID + ".acf"))
            if text:
                match = _INSTALL_DIR.search(text)
                if match:
                    folder = match.group(1).replace("\\\\", "\\")
            vrchat = os.path.join(apps, "common", folder)
            key = os.path.normcase(os.path.abspath(vrchat))
            if os.path.isdir(vrchat) and key not in seen:
                seen.add(key)
                yield vrchat


def _steam_folders():
    folders = []
    if os.name == "nt":
        import winreg
        for root, key in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"), (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam"),
                          (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam")):
            for name in ("SteamPath", "InstallPath"):
                try:
                    with winreg.OpenKey(root, key) as handle:
                        value, _ = winreg.QueryValueEx(handle, name)
                    if isinstance(value, str) and value:
                        folders.append(value.replace("/", "\\"))
                except OSError:
                    pass
        for variable in ("ProgramFiles(x86)", "ProgramFiles"):
            if os.environ.get(variable):
                folders.append(os.path.join(os.environ[variable], "Steam"))
    else:
        home = os.path.expanduser("~")
        folders += [os.path.join(home, ".steam", "steam"), os.path.join(home, ".local", "share", "Steam"),
                    os.path.join(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")]
    seen = set()
    for folder in folders:
        key = os.path.normcase(os.path.abspath(folder))
        if os.path.isdir(folder) and key not in seen:
            seen.add(key)
            yield folder


def _libraries(steam: str):
    yield steam
    text = _read(os.path.join(steam, "steamapps", "libraryfolders.vdf"))
    if text:
        for match in _LIBRARY_PATH.finditer(text):
            yield match.group(1).replace("\\\\", "\\")


def _read(path: str) -> str | None:
    try:
        with open(path, encoding="utf-8", errors="replace") as file:
            return file.read()
    except OSError:
        return None


class Listener:
    """Receives the avatar parameters VRChat sends. VRChat sends avatar parameters to every OSCQuery service it finds
    that lists /avatar; this one lists /avatar/change and /avatar/parameters (with Viseme and Voice), so VRChat sends
    them here. It is announced over multicast DNS as "_oscjson._tcp" and "_osc._udp" at 127.0.0.1, answers HTTP on
    127.0.0.1 only, and keeps /avatar/parameters/ values from loopback senders: the names in kept, or every one when
    kept is None."""

    _JSON_SERVICE = "_oscjson._tcp.local"
    _OSC_SERVICE = "_osc._udp.local"
    _MDNS_ADDRESS = "224.0.0.251"
    _MDNS_PORT = 5353
    VISEMES = ("Viseme", "Voice")

    def __init__(self, clock, sink, log=None, kept=VISEMES):
        """clock() gives each value's time in seconds; sink(time, name, type, value) gets each kept value (type 'i',
        'f' or 'b'). log gets problems with the network. kept: the parameter names kept, or None for every one."""
        self._kept = kept
        self._clock = clock
        self._sink = sink
        self._log = log or (lambda text: print(text, file=sys.stderr))
        self.name = "RiryTools-FTRecorder-" + uuid.uuid4().hex[:6]
        self.received = 0
        self._stopping = False
        self._osc = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._osc.bind(("127.0.0.1", 0))
        self.osc_port = self._osc.getsockname()[1]
        # Sockets wake twice a second to see whether to stop: a blocked receive would outlive close().
        self._osc.settimeout(0.5)
        self._http = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._http.bind(("127.0.0.1", 0))
        self._http.listen(8)
        self.http_port = self._http.getsockname()[1]
        self._http.settimeout(0.5)
        self._senders = []
        self._mdns = self._open_mdns()
        self._threads = []
        self._start(self._receive_osc, "OSC")
        self._start(self._serve_http, "OSCQuery")
        if self.discoverable:
            self._start(self._discovery, "discovery")

    @property
    def discoverable(self) -> bool:
        """False when no network interface could carry the announcement: VRChat cannot find the listener."""
        return self._mdns is not None and bool(self._senders)

    def _start(self, work, label):
        def body():
            try:
                work()
            except Exception as error:  # noqa: BLE001 - reported, the listener keeps the rest running
                if not self._stopping:
                    self._log("VRChat listener (%s) stopped: %s" % (label, error))
        thread = threading.Thread(target=body, name="FTRecorder " + label, daemon=True)
        self._threads.append(thread)
        thread.start()

    def _receive_osc(self):
        while not self._stopping:
            try:
                packet, (address, _) = self._osc.recvfrom(65535)
            except (socket.timeout, ConnectionResetError):
                continue
            except OSError:
                return
            if not address.startswith("127."):
                continue
            try:
                messages = osc.decode(packet)
            except osc.OscError:
                continue
            now = self._clock()
            for message in messages:
                if not message.address.startswith(osc.PARAMETER_PREFIX) or len(message.arguments) != 1:
                    continue
                parameter = message.address[len(osc.PARAMETER_PREFIX):]
                if self._kept is not None and parameter not in self._kept:
                    continue
                tag, value = message.arguments[0]
                if tag == "i":
                    self._sink(now, parameter, "i", float(value))
                elif tag == "f":
                    self._sink(now, parameter, "f", float(value))
                elif tag in "TF":
                    self._sink(now, parameter, "b", 1.0 if value else 0.0)
                else:
                    continue
                self.received += 1

    # A minimal HTTP server: one GET per connection, JSON back, closed after. Requests over 8 KB or slower than two
    # seconds are dropped.
    def _serve_http(self):
        while not self._stopping:
            try:
                client, _ = self._http.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            with client:
                try:
                    client.settimeout(2)
                    request = self._read_request(client)
                    if request is None:
                        continue
                    parts = request.split(" ")
                    status, body = (self._respond(parts[1]) if len(parts) >= 2 and parts[0] == "GET" else (405, "{}"))
                    content = body.encode("utf-8")
                    head = ("HTTP/1.1 %d %s\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n"
                            % (status, "OK" if status == 200 else "Error", len(content)))
                    client.sendall(head.encode("ascii") + content)
                except OSError:
                    pass

    @staticmethod
    def _read_request(client):
        data = b""
        while len(data) < 8192:
            chunk = client.recv(8192 - len(data))
            if not chunk:
                return None
            data += chunk
            if b"\r\n\r\n" in data:
                return data.split(b"\r\n", 1)[0].decode("ascii", "replace")
        return None

    def _respond(self, target):
        path, _, query = target.partition("?")
        if "HOST_INFO" in query:
            return 200, json.dumps({
                "NAME": self.name,
                "EXTENSIONS": {"ACCESS": True, "CLIPMODE": False, "RANGE": True, "TYPE": True, "VALUE": True},
                "OSC_IP": "127.0.0.1", "OSC_PORT": self.osc_port, "OSC_TRANSPORT": "UDP",
            })
        node = _tree()
        for part in [p for p in path.rstrip("/").split("/") if p]:
            node = node.get("CONTENTS", {}).get(part)
            if node is None:
                return 404, "{}"
        return 200, json.dumps(node)

    def _open_mdns(self):
        addresses = _ipv4_addresses()
        try:
            receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            receiver.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            if hasattr(socket, "SO_REUSEPORT"):
                try:
                    receiver.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
                except OSError:
                    pass
            receiver.bind(("", self._MDNS_PORT))
        except OSError as error:
            self._log("UDP port 5353 could not be shared (%s): VRChat cannot find the listener." % error)
            return None
        for address in addresses:
            try:
                receiver.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                                    socket.inet_aton(self._MDNS_ADDRESS) + socket.inet_aton(address))
                sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                sender.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                if hasattr(socket, "SO_REUSEPORT"):
                    try:
                        sender.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
                    except OSError:
                        pass
                # Answers come from port 5353, as multicast DNS expects of a responder.
                sender.bind((address, self._MDNS_PORT))
                sender.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(address))
                sender.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 1)
                self._senders.append(sender)
            except OSError as error:
                self._log("Multicast DNS on %s: %s" % (address, error))
        return receiver

    # Announces both services at start and every 15 seconds, and answers any question for either type at once.
    def _discovery(self):
        self._mdns.settimeout(0.5)
        host = self.name + ".local"
        loopback = socket.inet_aton("127.0.0.1")
        json_answer = dns.announce(self._JSON_SERVICE, self.name, host, self.http_port, loopback)
        udp_answer = dns.announce(self._OSC_SERVICE, self.name, host, self.osc_port, loopback)
        following = 0.0
        while not self._stopping:
            if time.monotonic() >= following:
                self._send(json_answer)
                self._send(udp_answer)
                following = time.monotonic() + 15
            try:
                data, _ = self._mdns.recvfrom(9000)
            except (socket.timeout, ConnectionResetError):
                continue
            except OSError:
                return
            for name, kind in dns.questions(data) or []:
                if kind not in (dns.TYPE_PTR, dns.TYPE_ANY):
                    continue
                asked = name.rstrip(".").lower()
                if asked == self._JSON_SERVICE.lower():
                    self._send(json_answer)
                elif asked == self._OSC_SERVICE.lower():
                    self._send(udp_answer)

    def _send(self, packet):
        for sender in self._senders:
            try:
                sender.sendto(packet, (self._MDNS_ADDRESS, self._MDNS_PORT))
            except OSError:
                pass

    def close(self):
        self._stopping = True
        for thread in self._threads:
            thread.join(2)
        for sock in [self._osc, self._http, self._mdns] + self._senders:
            if sock is not None:
                try:
                    sock.close()
                except OSError:
                    pass


def _tree():
    """The OSCQuery tree: /avatar/change, /avatar/parameters/Viseme (int) and Voice (float), all writable."""
    def leaf(path, kind):
        return {"FULL_PATH": path, "ACCESS": 2, "TYPE": kind}

    def branch(path, contents):
        return {"FULL_PATH": path, "ACCESS": 0, "CONTENTS": contents}

    parameters = branch("/avatar/parameters", {"Viseme": leaf("/avatar/parameters/Viseme", "i"), "Voice": leaf("/avatar/parameters/Voice", "f")})
    return branch("/", {"avatar": branch("/avatar", {"change": leaf("/avatar/change", "s"), "parameters": parameters})})


def _ipv4_addresses() -> list[str]:
    """One IPv4 address per network interface that is up (loopback included), for the multicast announcements."""
    found = []
    if os.name != "nt":
        try:
            import fcntl
            for _, name in socket.if_nameindex():
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                    try:
                        packed = fcntl.ioctl(probe.fileno(), 0x8915, struct.pack("256s", name.encode()[:15]))  # SIOCGIFADDR
                    except OSError:
                        continue
                    found.append(socket.inet_ntoa(packed[20:24]))
        except (ImportError, OSError):
            pass
    else:
        try:
            for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
                found.append(info[4][0])
        except OSError:
            pass
        found.append("127.0.0.1")
    unique = []
    for address in found:
        if address not in unique:
            unique.append(address)
    return unique

# ============================================================================
# GLOSSARY: VRChat
# ============================================================================
#
# vrchat (this file)       VRChat on this computer.
#   is_running()             Windows: a VRChat.exe process (Toolhelp snapshot). Linux: /proc/*/comm "VRChat.exe" (Proton,
#                            Wine) or "VRChat".
#   find_ovrlipsync()        (path or None, VRChat folders searched): Steam's folder (registry on Windows, the usual
#                            places on Linux), its libraryfolders.vdf libraries, appmanifest_438100.acf's installdir, then
#                            VRChat_Data/Plugins/x86_64/OVRLipSync.dll (or any *LipSync*.dll in Plugins). Read only.
#   Listener(clock, sink, log, kept)  OSCQuery service (HTTP and OSC on 127.0.0.1, random ports) listing /avatar/change
#                            and /avatar/parameters/ Viseme (i) and Voice (f), announced over multicast DNS (_oscjson._tcp,
#                            _osc._udp; every 15 s and on PTR/ANY questions) from every interface's IPv4 address. Keeps
#                            the /avatar/parameters/ values named in kept (default VISEMES: Viseme and Voice; None: every
#                            one) from loopback senders as sink(time, name, 'i'|'f'|'b', value); received counts them;
#                            discoverable is False when multicast DNS could not be opened. close() stops it.
