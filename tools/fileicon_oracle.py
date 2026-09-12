#!/usr/bin/env python3
"""Produce an /Ask preview image the way opendrop does, from opendrop's own code path.

Run inside the opendrop virtualenv, so the Pillow build and plistlib are the ones
opendrop itself would use:

    ~/od/bin/python tools/fileicon_oracle.py /mnt/c/path/to/out

Writes three files:

  opendrop-icon.jp2              the FileIcon bytes (util.generate_file_icon)
  control.jpg                    the same thumbnail as JPEG, a positive control for decoders
  opendrop-ask-with-icon.bplist  an /Ask body shaped like client.send_ask, icon included

The last one is the test fixture tests/WinDrop.Protocol.Tests/fixtures uses.

generate_file_icon is replicated rather than imported because it no longer runs:
it calls Image.ANTIALIAS, which Pillow 10 removed, so on a current install
`opendrop send` fails for every image. LANCZOS is the same filter under its
surviving name, and everything else is copied as-is.
"""

import io
import os
import plistlib
import sys

from PIL import Image, ImageDraw


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)

    # Something with structure, so a decode that "succeeds" into garbage is visible.
    image = Image.new("RGB", (1200, 800), (30, 30, 30))
    draw = ImageDraw.Draw(image)
    draw.rectangle((100, 100, 700, 600), fill=(230, 230, 230))
    draw.ellipse((600, 250, 1100, 750), fill=(120, 120, 120))

    image.thumbnail((540, 540), Image.LANCZOS)

    buffer = io.BytesIO()
    image.save(buffer, format="JPEG2000")
    icon = buffer.getvalue()

    with open(os.path.join(out, "opendrop-icon.jp2"), "wb") as handle:
        handle.write(icon)

    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=85)
    with open(os.path.join(out, "control.jpg"), "wb") as handle:
        handle.write(buffer.getvalue())

    ask = {
        "SenderComputerName": "opendrop-oracle",
        "BundleID": "com.apple.finder",
        "SenderModelName": "OpenDrop",
        "SenderID": "0123456789ab",
        "ConvertMediaFormats": False,
        "Files": [{
            "FileName": "photo.jpg",
            "FileType": "public.jpeg",
            "FileBomPath": "./photo.jpg",
            "FileIsDirectory": False,
            "ConvertMediaFormats": 0,
        }],
        "FileIcon": icon,
    }

    body = plistlib.dumps(ask, fmt=plistlib.FMT_BINARY)
    with open(os.path.join(out, "opendrop-ask-with-icon.bplist"), "wb") as handle:
        handle.write(body)

    print(f"thumbnail {image.size}, icon {len(icon)} bytes, ask body {len(body)} bytes")
    print(f"icon head {icon[:12].hex(' ')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
