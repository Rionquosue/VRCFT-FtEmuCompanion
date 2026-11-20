"""Sits between VRCFaceTracking and VRChat: VRCFaceTracking's OSC output port is set to in_port, and every packet that
arrives there from this computer is passed on unchanged to VRChat on out_port for as long as the relay runs. While a
writer is attached (set_writer), each packet is also recorded as it passes."""

from __future__ import annotations

import socket
import threading


class Relay:
    def __init__(self, in_port: int, out_port: int, log=None):
        if in_port == out_port:
            raise ValueError("The relay's in and out ports must differ.")
        self.in_port = in_port
        self.out_port = out_port
        self.packets = 0
        self._log = log or (lambda text: None)
        self._writer = None
        self._gate = threading.Lock()
        self._stopping = False
        self._receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            self._receiver.bind(("127.0.0.1", in_port))
        except OSError as error:
            self._receiver.close()
            raise RuntimeError("UDP port %d is in use (%s)." % (in_port, error.strerror or error)) from error
        # Woken twice a second to see whether it should stop: a blocked receive would keep the port after close().
        self._receiver.settimeout(0.5)
        self._sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._thread = threading.Thread(target=self._loop, name="FTRecorder relay", daemon=True)
        self._thread.start()

    def set_writer(self, writer) -> None:
        """Starts (a RecordingWriter) or stops (None) recording the packets; they are passed on either way."""
        # Under the lock, so a packet is never written to a writer that is being closed.
        with self._gate:
            self._writer = writer

    def _loop(self) -> None:
        target = ("127.0.0.1", self.out_port)
        while not self._stopping:
            try:
                packet, (address, _) = self._receiver.recvfrom(65535)
            except (socket.timeout, ConnectionResetError):
                continue
            except OSError as error:
                if not self._stopping:
                    self._log("Relay stopped: %s" % error)
                return
            if not address.startswith("127."):
                continue
            try:
                self._sender.sendto(packet, target)
            except OSError:
                pass  # VRChat not running: the packet is still recorded.
            with self._gate:
                if self._writer is not None:
                    self._writer.write_osc(self._writer.now, packet)
                self.packets += 1

    def close(self) -> None:
        self._stopping = True
        self._thread.join(2)
        self._receiver.close()
        self._sender.close()

# ============================================================================
# GLOSSARY: relay
# ============================================================================
#
# relay (this file)        Relay(in_port, out_port, log): 127.0.0.1:in_port to 127.0.0.1:out_port, unchanged, loopback
#                            senders only, for as long as it runs. packets counts them. set_writer(writer) records each as
#                            an OSC entry until set_writer(None). RuntimeError when in_port is in use. close() stops it.
