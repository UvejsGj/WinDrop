#!/usr/bin/env python3
"""What FileType opendrop puts in an /Ask entry, decided by opendrop's own classifier.

Run inside the opendrop virtualenv, since it imports opendrop and fleep:

    ~/od/bin/python tools/uti_oracle.py tests/WinDrop.Protocol.Tests/fixtures

opendrop never looks at the file name. `client.send_ask` reads the first 128 bytes of
the first selected file, hands them to fleep, and maps fleep's answer to a UTI in
`AirDropUtil.get_uti_type`. Rather than keep a sample file per format around, this
synthesises one header per entry in fleep's signature table — the signature bytes at the
offset fleep expects them, zero padding elsewhere — so every format fleep recognises goes
through the real classifier rather than through a reading of its source.

The `extension` in each row is the label of the signature that was fed in, not fleep's
conclusion. fleep returns *lists* of candidates ordered by signature length, and
get_uti_type reads only the first, so a header can be labelled `cr2` here and still be
classified from another format's MIME type. That is the point of running it rather than
reading it.

Writes opendrop-uti.json, which tests/WinDrop.Protocol.Tests reads to check WinDrop's
table against a classification somebody else made.
"""

import json
import sys
from pathlib import Path

import fleep
from opendrop.util import AirDropUtil

# The amount client.send_ask reads before classifying. Long enough for every signature
# in fleep's table, which is why a short read is not a source of disagreement here.
HEADER_BYTES = 128


def header_for(signature: str, offset: int) -> bytes:
    """A header fleep will match against this signature, and nothing shorter matches."""
    raw = bytes(int(byte, 16) for byte in signature.split())
    return raw.rjust(len(raw) + offset, b"\x00").ljust(HEADER_BYTES, b"\x00")


def classify() -> list[dict]:
    """One row per signature, not per format.

    A format's signatures do not always classify alike: a zip starting `PK\\x03\\x04`
    lands on fleep's Office-document entries, which share that signature and sort ahead
    of the archive one, while an empty zip starting `PK\\x05\\x06` does not.
    """
    rows = []

    for entry in fleep.data:
        for signature in entry["signature"]:
            info = fleep.get(header_for(signature, entry["offset"]))
            rows.append({
                "extension": entry["extension"],
                "signature": signature,
                "mime": info.mime[0] if info.mime else None,
                "type": info.type[0] if info.type else None,
                "uti": AirDropUtil.get_uti_type(info),
            })

    return sorted(rows, key=lambda row: (row["extension"], row["signature"]))


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    rows = classify()

    target = Path(sys.argv[1])
    target.mkdir(parents=True, exist_ok=True)
    path = target / "opendrop-uti.json"
    path.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")

    print(f"wrote {path} ({len(rows)} formats)")
    for uti in sorted({row["uti"] for row in rows}):
        print(f"  {uti:26} {' '.join(r['extension'] for r in rows if r['uti'] == uti)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
