"""The few multicast DNS packets OSCQuery discovery needs: the answer that announces one service (PTR, TXT, SRV and A
records) and a reader for the questions other programs send. Names are written in full (no compression); compressed
names are read, with a bound on the jumps.
"""

from __future__ import annotations

import struct

TYPE_A = 1
TYPE_PTR = 12
TYPE_TXT = 16
TYPE_SRV = 33
TYPE_ANY = 255
_CLASS_IN = 1
_MAX_RECORDS = 64
_MAX_JUMPS = 16


def announce(service_type: str, instance: str, host: str, port: int, address: bytes, ttl: int = 120) -> bytes:
    """The answer announcing one service: service_type points to "instance.service_type", which is at host:port, and
    host has address (four bytes). instance must be one label."""
    full = instance + "." + service_type
    out = bytearray(struct.pack(">HHHHHH", 0, 0x8400, 0, 1, 0, 3))
    out += _record(service_type, TYPE_PTR, ttl, _name(full))
    out += _record(full, TYPE_TXT, ttl, bytes([9]) + b"txtvers=1")
    out += _record(full, TYPE_SRV, ttl, struct.pack(">HHH", 0, 0, port) + _name(host))
    out += _record(host, TYPE_A, ttl, address)
    return bytes(out)


def _record(name: str, kind: int, ttl: int, data: bytes) -> bytes:
    return _name(name) + struct.pack(">HHIH", kind, _CLASS_IN, ttl, len(data)) + data


def _name(name: str) -> bytes:
    out = bytearray()
    for label in name.split("."):
        raw = label.encode("utf-8")
        if not raw:
            continue
        if len(raw) > 63:
            raise ValueError("A DNS label is longer than 63 bytes: " + label)
        out += bytes([len(raw)]) + raw
    return bytes(out + b"\0")


def questions(data: bytes) -> list[tuple[str, int]] | None:
    """The (name, type) questions of a query; None for a response or anything cut short or malformed."""
    if len(data) < 12 or data[2] & 0x80:
        return None
    count = struct.unpack_from(">H", data, 4)[0]
    if count > _MAX_RECORDS:
        return None
    result = []
    at = 12
    for _ in range(count):
        parsed = _read_name(data, at)
        if parsed is None:
            return None
        name, at = parsed
        if at + 4 > len(data):
            return None
        result.append((name, struct.unpack_from(">H", data, at)[0]))
        at += 4
    return result


def _read_name(data: bytes, at: int) -> tuple[str, int] | None:
    labels = []
    position, jumps, after = at, 0, -1
    while True:
        if position >= len(data):
            return None
        size = data[position]
        if size == 0:
            position += 1
            break
        if size & 0xC0 == 0xC0:
            jumps += 1
            if position + 1 >= len(data) or jumps > _MAX_JUMPS:
                return None
            if after < 0:
                after = position + 2
            position = (size & 0x3F) << 8 | data[position + 1]
            continue
        if size & 0xC0 or position + 1 + size > len(data):
            return None
        labels.append(data[position + 1:position + 1 + size].decode("utf-8", "replace"))
        position += 1 + size
        if sum(len(label) + 1 for label in labels) > 256:
            return None
    return ".".join(labels), after if after >= 0 else position

# ============================================================================
# GLOSSARY: DNS
# ============================================================================
#
# dns (this file)          Multicast DNS for OSCQuery discovery (as FTEmulator's FTEmulatorDnsPacket does it).
#   announce(type, instance, host, port, address, ttl=120)  An authoritative response: PTR type -> instance.type, TXT
#                            "txtvers=1", SRV host:port, A address.
#   questions(data)          A query's (name, type) list; None for responses and malformed packets. Compressed names
#                            are followed (at most 16 jumps).
#   TYPE_PTR, TYPE_ANY       The question types the Recorder answers.
