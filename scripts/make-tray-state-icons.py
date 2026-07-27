#!/usr/bin/env python3
"""Derive the tray state-variant icons from the canonical app icon.

Generates src/Winpepper.App/Assets/AppIcon-{Recording,Loading,Error}.ico from
src/Winpepper.App/Assets/AppIcon.ico by compositing a corner status badge onto
EVERY frame of the source .ico (16/20/24/32/40/48/64/128/256 px). The outputs
keep the exact same frame inventory as the source; the script re-opens each
output and fails loudly if any frame is missing or if a badge did not actually
change the pixels of a frame.

Badge design (per frame, scaled to the frame):
  - Recording: solid red (#E53935) filled circle, bottom-right.
  - Loading:   amber (#FFB300) filled circle, bottom-right.
  - Error:     red (#D32F2F) circle with a thick white X inside.
  All badges carry a thin white contrast ring so they read on both dark and
  light taskbars. Badge diameter is ~45% of the frame edge.

Requirements: python3 + Pillow (`python3 -c "import PIL"` to check; if missing,
create a venv in /tmp and `pip install pillow` there -- do NOT install
system-wide). Run from anywhere; paths are resolved relative to this file.

Usage: python3 scripts/make-tray-state-icons.py
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw, IcoImagePlugin

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS_DIR = REPO_ROOT / "src" / "Winpepper.App" / "Assets"
SOURCE_ICO = ASSETS_DIR / "AppIcon.ico"

# variant name -> (badge fill RGBA, draw white X inside)
VARIANTS: dict[str, tuple[tuple[int, int, int, int], bool]] = {
    "Recording": ((0xE5, 0x39, 0x35, 0xFF), False),
    "Loading": ((0xFF, 0xB3, 0x00, 0xFF), False),
    "Error": ((0xD3, 0x2F, 0x2F, 0xFF), True),
}

WHITE = (0xFF, 0xFF, 0xFF, 0xFF)
SUPERSAMPLE = 4  # draw badges at 4x then downscale for smooth edges


def load_frames(path: Path) -> dict[tuple[int, int], Image.Image]:
    """Load every frame of an .ico as RGBA, keyed by (w, h)."""
    with open(path, "rb") as f:
        ico = IcoImagePlugin.IcoFile(f)
        return {size: ico.getimage(size).convert("RGBA") for size in ico.sizes()}


def make_badge(
    frame_px: int, fill: tuple[int, int, int, int], draw_x: bool
) -> tuple[Image.Image, int]:
    """Render a badge for a frame of edge length frame_px.

    Returns (badge RGBA image, badge diameter in frame pixels).
    """
    d = max(7, round(frame_px * 0.45))  # badge diameter; floor keeps 16px legible
    s = d * SUPERSAMPLE
    badge = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    dr = ImageDraw.Draw(badge)

    # Thin contrast ring: white outer disc, coloured inner disc inset by ring width.
    ring = max(1, round(d / 12)) * SUPERSAMPLE
    dr.ellipse((0, 0, s - 1, s - 1), fill=WHITE)
    dr.ellipse((ring, ring, s - 1 - ring, s - 1 - ring), fill=fill)

    if draw_x:
        # Thick white X so it survives downscale. At tiny frames the strokes
        # merge toward a solid mark, which is acceptable -- the badge stays a
        # visually distinct red-with-white blob (verified below by pixel diff
        # and a white-pixel presence check).
        stroke = max(1, round(d * 0.20)) * SUPERSAMPLE
        inset = round(s * 0.30)
        dr.line((inset, inset, s - 1 - inset, s - 1 - inset), fill=WHITE, width=stroke)
        dr.line((s - 1 - inset, inset, inset, s - 1 - inset), fill=WHITE, width=stroke)

    return badge.resize((d, d), Image.Resampling.LANCZOS), d


def composite_variant(
    frames: dict[tuple[int, int], Image.Image],
    fill: tuple[int, int, int, int],
    draw_x: bool,
) -> dict[tuple[int, int], Image.Image]:
    out: dict[tuple[int, int], Image.Image] = {}
    for size, base in frames.items():
        frame_px = size[0]
        badge, d = make_badge(frame_px, fill, draw_x)
        margin = 0 if frame_px <= 24 else round(frame_px * 0.02)
        composed = base.copy()
        composed.alpha_composite(badge, (frame_px - d - margin, frame_px - d - margin))
        out[size] = composed
    return out


def save_ico(path: Path, frames: dict[tuple[int, int], Image.Image]) -> None:
    sizes = sorted(frames.keys())
    base = frames[sizes[-1]]  # largest frame is the base image
    append = [frames[s] for s in sizes[:-1]]
    # Pass sizes= explicitly; Pillow matches provided frames by exact size, so
    # every frame written is our per-size composite (no re-derived downscales).
    base.save(path, format="ICO", sizes=sizes, append_images=append)


def badge_region(frame_px: int) -> tuple[int, int, int, int]:
    d = max(7, round(frame_px * 0.45))
    margin = 0 if frame_px <= 24 else round(frame_px * 0.02)
    return (
        frame_px - d - margin,
        frame_px - d - margin,
        frame_px - margin,
        frame_px - margin,
    )


def verify(
    path: Path, source_frames: dict[tuple[int, int], Image.Image], has_x: bool
) -> list[str]:
    """Re-open an output .ico and assert frame inventory + badge visibility.

    Returns the human-readable frame inventory for reporting. Raises on failure.
    """
    written = load_frames(path)
    src_sizes = set(source_frames.keys())
    out_sizes = set(written.keys())
    if out_sizes != src_sizes:
        raise SystemExit(
            f"FAIL {path.name}: frame inventory mismatch. "
            f"missing={sorted(src_sizes - out_sizes)} extra={sorted(out_sizes - src_sizes)}"
        )

    for size in sorted(src_sizes):
        frame_px = size[0]
        region = badge_region(frame_px)
        base_bytes = source_frames[size].crop(region).tobytes()
        out_bytes = written[size].crop(region).tobytes()
        if out_bytes == base_bytes:
            raise SystemExit(
                f"FAIL {path.name} {frame_px}px: badge region identical to base icon"
            )
        if has_x:
            # The white mark inside the Error badge must survive downscale.
            rgba = [tuple(out_bytes[i : i + 4]) for i in range(0, len(out_bytes), 4)]
            white_ish = sum(
                1 for r, g, b, a in rgba if a > 200 and r > 200 and g > 200 and b > 200
            )
            if white_ish == 0:
                raise SystemExit(
                    f"FAIL {path.name} {frame_px}px: no white mark pixels in badge"
                )

    return [f"{w}x{h}" for (w, h) in sorted(out_sizes)]


def main() -> int:
    if not SOURCE_ICO.exists():
        raise SystemExit(f"FAIL: source icon not found: {SOURCE_ICO}")
    source_frames = load_frames(SOURCE_ICO)
    print(
        f"source {SOURCE_ICO.name}: {len(source_frames)} frames "
        f"({', '.join(f'{w}x{h}' for (w, h) in sorted(source_frames))})"
    )

    for name, (fill, draw_x) in VARIANTS.items():
        out_path = ASSETS_DIR / f"AppIcon-{name}.ico"
        frames = composite_variant(source_frames, fill, draw_x)
        save_ico(out_path, frames)
        inventory = verify(out_path, source_frames, draw_x)
        print(f"OK {out_path.name}: {len(inventory)} frames ({', '.join(inventory)})")

    print("all tray state icons generated and verified")
    return 0


if __name__ == "__main__":
    sys.exit(main())
