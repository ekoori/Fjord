#!/usr/bin/env python3
"""Assembles the README animation from the frames capture-screenshots.ps1 wrote.

Kept beside the capture script so the pair can be re-run when the UI changes: a
screenshot in a README is the first thing to go stale, and one nobody can regenerate
stays stale.
"""

import os
import sys

from PIL import Image

# Wide enough to read a filename at GitHub's rendered width, small enough that the file
# is not the largest thing in the repository.
WIDTH = 900

# Long enough to read a frame, short enough that the loop does not feel stalled.
HOLD_MS = 2300

# The last frame is the help page, which is dense; give it longer before the loop.
LAST_MS = 3200


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: make-gif.py <frames-directory> <output.gif>", file=sys.stderr)
        return 2

    source, target = sys.argv[1], sys.argv[2]
    names = sorted(n for n in os.listdir(source) if n.endswith(".png"))

    if not names:
        print(f"no frames in {source}", file=sys.stderr)
        return 1

    frames = []
    for name in names:
        image = Image.open(os.path.join(source, name)).convert("RGB")

        # The captured rect includes a pixel or two of whatever was behind the window.
        image = image.crop((2, 2, image.width - 2, image.height - 2))

        height = round(image.height * WIDTH / image.width)
        frames.append(image.resize((WIDTH, height), Image.LANCZOS))

    # One palette for every frame, taken from the first. Per-frame palettes make the
    # background shift between frames, which reads as flicker on a UI this flat.
    palette = frames[0].quantize(colors=128, method=Image.MEDIANCUT)
    quantized = [f.quantize(palette=palette, dither=Image.NONE) for f in frames]

    durations = [HOLD_MS] * (len(quantized) - 1) + [LAST_MS]

    quantized[0].save(
        target,
        save_all=True,
        append_images=quantized[1:],
        duration=durations,
        loop=0,
        optimize=True,
    )

    print(f"{target}  {len(quantized)} frames  {os.path.getsize(target) / 1e6:.1f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
