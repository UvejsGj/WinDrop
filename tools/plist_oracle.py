"""Differential oracle for the binary plist codec.

Python's plistlib is an independent implementation of the same format. Testing our
reader only against our own writer would prove self-consistency and nothing more —
a codec can be wrong in a way that round-trips perfectly. So plistlib generates the
fixtures our reader is checked against, and reads back what our writer produces.

  generate <dir>   write fixture .plist files for the C# tests to read
  dump <file>      print a canonical JSON view of a binary plist, for comparing our
                   writer's output against plistlib's understanding of it
"""

import base64
import datetime
import json
import plistlib
import sys
from pathlib import Path


# A realistic AirDrop /Ask body. Doubles as documentation of the payload shape we
# will need in milestone 4, kept here so the codec is exercised on the real thing
# rather than on synthetic data alone.
ASK = {
    "SenderComputerName": "WinDrop",
    "BundleID": "com.apple.finder",
    "SenderModelName": "Windows",
    "SenderID": "0F8A3C21-4E7B-4A9D-9C2E-1B5D7E3F6A80",
    "ConvertMediaFormats": False,
    "Files": [
        {
            "FileName": "photo.jpg",
            "FileType": "public.jpeg",
            "FileBomPath": "./photo.jpg",
            "FileIsDirectory": False,
            "ConvertMediaFormats": 0,
        },
        {
            "FileName": "notes.txt",
            "FileType": "public.plain-text",
            "FileBomPath": "./notes.txt",
            "FileIsDirectory": False,
            "ConvertMediaFormats": 0,
        },
    ],
}

# Edge cases that exercise every width decision in the encoder. The integer values sit
# exactly on the 1/2/4/8-byte boundaries, because off-by-one width selection is the
# classic bug in this format and only shows up at the boundary.
TYPES = {
    "bool_true": True,
    "bool_false": False,
    "int_zero": 0,
    "int_255": 255,
    "int_256": 256,
    "int_65535": 65535,
    "int_65536": 65536,
    "int_max_u32": 4294967295,
    "int_over_u32": 4294967296,
    "int_negative": -1,
    "int_int64_min": -9223372036854775808,
    "real": 3.141592653589793,
    "real_negative": -0.5,
    "ascii": "plain ascii string",
    "unicode": "Përshëndetje 世界 \U0001F4E1",
    "empty_string": "",
    "data": b"\x00\x01\x02\xfe\xff",
    "empty_data": b"",
    # plistlib rejects tz-aware datetimes: it subtracts a naive 2001 epoch internally.
    # Naive values are treated as UTC, which is what the format stores.
    "date": datetime.datetime(2026, 9, 2, 14, 20, 32),
    "nested": {"a": [1, 2, {"b": "c"}], "d": {"e": [True, False]}},
    "empty_array": [],
    "empty_dict": {},
    # 15 elements forces the 0xF count escape, since the low nibble tops out at 14.
    "long_array": list(range(20)),
    "repeated": ["same", "same", "same"],
}


def canonical(value):
    """Stable JSON view so C# and Python can be compared without format noise."""
    if isinstance(value, bool):
        return {"bool": value}
    if isinstance(value, int):
        return {"int": str(value)}
    if isinstance(value, float):
        return {"real": repr(value)}
    if isinstance(value, str):
        return {"string": value}
    if isinstance(value, bytes):
        return {"data": base64.b64encode(value).decode("ascii")}
    if isinstance(value, datetime.datetime):
        if value.tzinfo is None:
            value = value.replace(tzinfo=datetime.timezone.utc)
        return {"date": value.astimezone(datetime.timezone.utc).isoformat()}
    if isinstance(value, list):
        return {"array": [canonical(v) for v in value]}
    if isinstance(value, dict):
        return {"dict": {k: canonical(value[k]) for k in sorted(value)}}
    raise TypeError(f"unhandled type {type(value)!r}")


def generate(target: Path) -> None:
    target.mkdir(parents=True, exist_ok=True)

    for name, payload in (("ask", ASK), ("types", TYPES)):
        plist_path = target / f"{name}.plist"
        plist_path.write_bytes(plistlib.dumps(payload, fmt=plistlib.FMT_BINARY))

        # The expected values travel with the fixture so the C# test asserts against
        # plistlib's own reading, not against a transcription of it by hand.
        json_path = target / f"{name}.json"
        json_path.write_text(
            json.dumps(canonical(payload), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
        print(f"wrote {plist_path.name} ({plist_path.stat().st_size} bytes) and {json_path.name}")


def dump(path: Path) -> None:
    value = plistlib.loads(path.read_bytes())
    print(json.dumps(canonical(value), indent=2, ensure_ascii=False))


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    command, argument = sys.argv[1], Path(sys.argv[2])

    if command == "generate":
        generate(argument)
    elif command == "dump":
        dump(argument)
    else:
        print(f"unknown command {command!r}")
        return 2

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
