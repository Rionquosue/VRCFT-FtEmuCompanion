"""OSC 1.0 packets: messages and bundles carrying floats, ints, bools, strings and blobs.

A packet that is not exactly right is refused whole: every length, the padding to four bytes, the nesting of bundles
and every float are checked before anything is given out, and nothing is read past the end of the packet. A bundle's
time tag is not used: its messages apply when they arrive.
"""

from __future__ import annotations

import math
import struct

PARAMETER_PREFIX = "/avatar/parameters/"
MAX_BUNDLE_DEPTH = 4
MAX_MESSAGES_PER_PACKET = 1024
MAX_ARGUMENTS = 64
MAX_PACKET_LENGTH = 65507


class OscError(ValueError):
    """A packet that is not valid OSC."""


class Message:
    """One OSC message: its address and its arguments as (tag, value) pairs; tag is f, i, T, F, s or b."""

    __slots__ = ("address", "arguments")

    def __init__(self, address: str, arguments: list):
        self.address = address
        self.arguments = arguments


def decode(data: bytes) -> list[Message]:
    """The packet's messages, in order. Raises OscError, with the reason, when any part of it is not valid OSC."""
    if not data or len(data) > MAX_PACKET_LENGTH:
        raise OscError("empty or oversized packet")
    if len(data) & 3:
        raise OscError("packet length is not a multiple of four")
    messages: list[Message] = []
    _element(data, 0, len(data), 0, messages)
    return messages


def _element(data: bytes, start: int, end: int, depth: int, messages: list) -> None:
    if end - start >= 16 and data[start] == 0x23:  # '#'
        header, at = _text(data, start, end)
        if header != "#bundle":
            raise OscError("malformed bundle header")
        if depth >= MAX_BUNDLE_DEPTH:
            raise OscError("bundles nested deeper than %d" % MAX_BUNDLE_DEPTH)
        at += 8  # the time tag
        if at > end:
            raise OscError("bundle cut short")
        while at < end:
            if end - at < 4:
                raise OscError("bundle element size cut short")
            size = struct.unpack_from(">i", data, at)[0]
            at += 4
            if size <= 0 or size & 3 or size > end - at:
                raise OscError("bundle element size out of range")
            _element(data, at, at + size, depth + 1, messages)
            at += size
        return
    _message(data, start, end, messages)


def _message(data: bytes, start: int, end: int, messages: list) -> None:
    if len(messages) >= MAX_MESSAGES_PER_PACKET:
        raise OscError("more than %d messages in one packet" % MAX_MESSAGES_PER_PACKET)
    address, at = _text(data, start, end)
    if not address.startswith("/"):
        raise OscError("malformed address")
    tags, at = _text(data, at, end)
    if not tags.startswith(","):
        raise OscError("malformed type tags")
    if len(tags) - 1 > MAX_ARGUMENTS:
        raise OscError("more than %d arguments" % MAX_ARGUMENTS)
    arguments = []
    for tag in tags[1:]:
        if tag == "f":
            if end - at < 4:
                raise OscError("float cut short")
            value = struct.unpack_from(">f", data, at)[0]
            if not math.isfinite(value):
                raise OscError("float is not finite")
            at += 4
        elif tag == "i":
            if end - at < 4:
                raise OscError("int cut short")
            value = struct.unpack_from(">i", data, at)[0]
            at += 4
        elif tag in "TF":
            value = tag == "T"
        elif tag == "s":
            value, at = _text(data, at, end, "string cut short")
        elif tag == "b":
            if end - at < 4:
                raise OscError("blob size cut short")
            size = struct.unpack_from(">i", data, at)[0]
            at += 4
            padded = (size + 3) & ~3
            if size < 0 or padded > end - at:
                raise OscError("blob size out of range")
            value = bytes(data[at:at + size])
            at += padded
        else:
            raise OscError("unsupported type tag '%s'" % tag)
        arguments.append((tag, value))
    if at != end:
        raise OscError("bytes left over after the arguments")
    messages.append(Message(address, arguments))


def _text(data: bytes, at: int, end: int, cut: str = "malformed string") -> tuple[str, int]:
    """An OSC string: ended by a zero, padded with zeros to four bytes. Its text and the position after it."""
    stop = data.find(b"\0", at, end)
    if stop < 0:
        raise OscError(cut)
    following = at + ((stop - at + 4) & ~3)
    if following > end or any(data[stop:following]):
        raise OscError(cut)
    return data[at:stop].decode("utf-8", "replace"), following


def encode(address: str, argument) -> bytes:
    """One message with one argument: a str, bool, float, int or bytes."""
    out = bytearray(_pad(address.encode("utf-8")))
    if isinstance(argument, str):
        out += _pad(b",s") + _pad(argument.encode("utf-8"))
    elif isinstance(argument, bool):
        out += _pad(b",T" if argument else b",F")
    elif isinstance(argument, float):
        out += _pad(b",f") + struct.pack(">f", argument)
    elif isinstance(argument, int):
        out += _pad(b",i") + struct.pack(">i", argument)
    elif isinstance(argument, (bytes, bytearray)):
        out += _pad(b",b") + struct.pack(">i", len(argument)) + bytes(argument)
        out += b"\0" * (-len(out) & 3)
    else:
        raise TypeError("Unsupported OSC argument.")
    return bytes(out)


def _pad(text: bytes) -> bytes:
    text += b"\0"
    return text + b"\0" * (-len(text) & 3)

# ============================================================================
# GLOSSARY: OSC
# ============================================================================
#
# osc (this file)          OSC 1.0, as the C# module's FTRecorderOscCodec reads and writes it.
#   decode(data)             Messages of a packet (bundles flattened, at most 4 deep, 1024 messages, 64 arguments,
#                            65507 bytes); OscError with the reason for anything malformed, including non-finite floats.
#   encode(address, value)   One message: str (s), bool (T/F), float (f), int (i) or bytes (b).
#   Message                  address and arguments, a list of (tag, value).
#   PARAMETER_PREFIX         "/avatar/parameters/".
