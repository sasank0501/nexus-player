"""Generates Icons.xaml - the stroked outline icon set, drawn from Lucide.

The icons are stored here as verbatim Lucide SVG markup and converted to WPF
geometry by tools/svgpath.py at generation time. Keeping the source in its
original form means a future Lucide update is a paste, not a re-derivation, and
it leaves the provenance of every glyph readable in the diff.

Lucide's geometry contract is the reason this drops in without touching the
styling: viewBox 0 0 24 24, fill none, stroke-width 2, round cap and join. That
is what the IconPath style in App.xaml already applies, so only the path data
changes - not the sizing, stroke weight or alignment plumbing.

Icons.xaml is committed, so the build does not depend on Python. Regenerate with:

    python tools/make_icons.py
"""
import math
import os
import sys
from xml.etree import ElementTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import svgpath

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

CX = CY = 12.0


# ---------------------------------------------------------------------------
# Hand-drawn helpers, kept for the slots Lucide has not taken over.
# ---------------------------------------------------------------------------

def pt(angle_deg, r, cx=CX, cy=CY):
    """Point on a circle. 0 deg = right, angles increase clockwise on screen
    (WPF y grows downward), which matches how the arcs read visually."""
    a = math.radians(angle_deg)
    return (cx + r * math.cos(a), cy + r * math.sin(a))


def f(v):
    return f"{v:.2f}".rstrip("0").rstrip(".")


def p(pointpair):
    return f"{f(pointpair[0])},{f(pointpair[1])}"


def arc(start_deg, end_deg, r, cx=CX, cy=CY, clockwise=True):
    """Arc path fragment from start_deg to end_deg."""
    s, e = pt(start_deg, r, cx, cy), pt(end_deg, r, cx, cy)
    sweep = (end_deg - start_deg) % 360 if clockwise else (start_deg - end_deg) % 360
    large = 1 if sweep > 180 else 0
    return f"M {p(s)} A {f(r)},{f(r)} 0 {large} {1 if clockwise else 0} {p(e)}"


def mirror_x(path):
    """Reflect a path about x = 12. Only M/L/A commands are used here."""
    out, i = [], 0
    tokens = path.replace(",", " ").split()
    while i < len(tokens):
        t = tokens[i]
        if t in ("M", "L"):
            x, y = float(tokens[i + 1]), float(tokens[i + 2])
            out.append(f"{t} {f(24 - x)},{f(y)}")
            i += 3
        elif t == "A":
            rx, ry = float(tokens[i + 1]), float(tokens[i + 2])
            rot, large, sweep = tokens[i + 3], tokens[i + 4], tokens[i + 5]
            x, y = float(tokens[i + 6]), float(tokens[i + 7])
            # mirroring reverses the sweep direction
            out.append(f"A {f(rx)},{f(ry)} {rot} {large} "
                       f"{0 if sweep == '1' else 1} {f(24 - x)},{f(y)}")
            i += 8
        else:
            raise ValueError(f"unhandled path command {t}")
    return " ".join(out)


def seek_arrow(forward):
    """Ring with a gap at the top and an arrowhead at the open end.

    The two directions are built as exact mirror images of each other (x -> 24-x)
    so they cannot drift apart or accidentally read the same way, which is what
    happened when they were defined independently.
    """
    r = 8.0
    # clockwise from upper-right round to upper-left: 320 degrees, gap at the top
    start, end = 290.0, 250.0
    ring = arc(start, end, r, clockwise=True)

    tip = pt(end, r)
    # tangent at the tip for clockwise travel
    tangent = end + 90.0
    bx, by = math.cos(math.radians(tangent + 180)), math.sin(math.radians(tangent + 180))
    px, py = -by, bx
    L, W = 4.6, 3.2
    b1 = (tip[0] + bx * L + px * W, tip[1] + by * L + py * W)
    b2 = (tip[0] + bx * L - px * W, tip[1] + by * L - py * W)
    path = f"{ring} M {p(b1)} L {p(tip)} L {p(b2)}"

    return path if not forward else mirror_x(path)


# ---------------------------------------------------------------------------
# The settled mapping: name in the app -> (Lucide icon, its SVG markup).
# ---------------------------------------------------------------------------

LUCIDE = {
    # transport
    "Play": ("play", '<path d="M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z"/>'),
    "Pause": ("pause", '<rect x="14" y="3" width="5" height="18" rx="1"/> <rect x="5" y="3" width="5" height="18" rx="1"/>'),
    "SkipNext": ("skip-forward", '<path d="M21 4v16"/> <path d="M6.029 4.285A2 2 0 0 0 3 6v12a2 2 0 0 0 3.029 1.715l9.997-5.998a2 2 0 0 0 .003-3.432z"/>'),
    "SkipPrev": ("skip-back", '<path d="M17.971 4.285A2 2 0 0 1 21 6v12a2 2 0 0 1-3.029 1.715l-9.997-5.998a2 2 0 0 1-.003-3.432z"/> <path d="M3 20V4"/>'),

    # audio
    "Volume": ("volume-2", '<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/> <path d="M16 9a5 5 0 0 1 0 6"/> <path d="M19.364 18.364a9 9 0 0 0 0-12.728"/>'),
    "VolumeLow": ("volume-1", '<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/> <path d="M16 9a5 5 0 0 1 0 6"/>'),
    "VolumeMuted": ("volume-x", '<path d="M11 4.702a.7.7 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.7.7 0 0 0 11 19.298z"/> <path d="m16.5 14.5 5-5"/> <path d="m16.5 9.5 5 5"/>'),
    "AudioTrack": ("audio-lines", '<path d="M2 10v3"/> <path d="M6 6v11"/> <path d="M10 3v18"/> <path d="M14 8v7"/> <path d="M18 5v13"/> <path d="M22 10v3"/>'),

    # panels
    "Subtitles": ("captions", '<rect width="18" height="14" x="3" y="5" rx="2" ry="2"/> <path d="M7 15h4M15 15h2M7 11h2M13 11h4"/>'),
    "Episodes": ("list-video", '<path d="M21 5H3"/> <path d="M10 12H3"/> <path d="M10 19H3"/> <path d="M15 12.003a1 1 0 0 1 1.517-.859l4.997 2.997a1 1 0 0 1 0 1.718l-4.997 2.997a1 1 0 0 1-1.517-.86z"/>'),
    "Settings": ("settings", '<path d="M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915"/> <circle cx="12" cy="12" r="3"/>'),
    "More": ("ellipsis", '<circle cx="12" cy="12" r="1"/> <circle cx="19" cy="12" r="1"/> <circle cx="5" cy="12" r="1"/>'),
    "Fullscreen": ("maximize", '<path d="M8 3H5a2 2 0 0 0-2 2v3"/> <path d="M21 8V5a2 2 0 0 0-2-2h-3"/> <path d="M3 16v3a2 2 0 0 0 2 2h3"/> <path d="M16 21h3a2 2 0 0 0 2-2v-3"/>'),
    "FullscreenExit": ("minimize", '<path d="M8 3v3a2 2 0 0 1-2 2H3"/> <path d="M21 8h-3a2 2 0 0 1-2-2V3"/> <path d="M3 16h3a2 2 0 0 1 2 2v3"/> <path d="M16 21v-3a2 2 0 0 1 2-2h3"/>'),

    # settings rows
    "Preferences": ("sliders-horizontal", '<path d="M10 5H3"/> <path d="M12 19H3"/> <path d="M14 3v4"/> <path d="M16 17v4"/> <path d="M21 12h-9"/> <path d="M21 19h-5"/> <path d="M21 5h-7"/> <path d="M8 10v4"/> <path d="M8 12H3"/>'),
    "Speed": ("gauge", '<path d="m12 14 4-4"/> <path d="M3.34 19a10 10 0 1 1 17.32 0"/>'),
    "Filters": ("sliders-vertical", '<path d="M10 8h4"/> <path d="M12 21v-9"/> <path d="M12 8V3"/> <path d="M17 16h4"/> <path d="M19 12V3"/> <path d="M19 21v-5"/> <path d="M3 14h4"/> <path d="M5 10V3"/> <path d="M5 21v-7"/>'),

    # more-options menu
    "Stats": ("activity", '<path d="M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"/>'),
    "Screenshot": ("camera", '<path d="M13.997 4a2 2 0 0 1 1.76 1.05l.486.9A2 2 0 0 0 18.003 7H20a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h1.997a2 2 0 0 0 1.759-1.048l.489-.904A2 2 0 0 1 10.004 4z"/> <circle cx="12" cy="13" r="3"/>'),
    "Keyboard": ("keyboard", '<path d="M10 8h.01"/> <path d="M12 12h.01"/> <path d="M14 8h.01"/> <path d="M16 12h.01"/> <path d="M18 8h.01"/> <path d="M6 8h.01"/> <path d="M7 16h10"/> <path d="M8 12h.01"/> <rect width="20" height="16" x="2" y="4" rx="2"/>'),
    "Folder": ("folder", '<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/>'),
    # refresh-ccw rather than rotate-ccw on purpose: Reset sat next to Back-10 in
    # the menu and the two single-ring shapes were indistinguishable at 16px.
    "Reset": ("refresh-ccw", '<path d="M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/> <path d="M3 3v5h5"/> <path d="M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16"/> <path d="M16 16h5v5"/>'),

    # queue sidebar
    "Link": ("link", '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/> <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>'),
    "Plus": ("plus", '<path d="M5 12h14"/> <path d="M12 5v14"/>'),
    "Trash": ("trash", '<path d="M10 11v6"/> <path d="M14 11v6"/> <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/> <path d="M3 6h18"/> <path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>'),

    # window caption
    "Minimize": ("minus", '<path d="M5 12h14"/>'),
    "Maximize": ("square", '<rect width="18" height="18" x="3" y="3" rx="2"/>'),
    "Close": ("x", '<path d="M18 6 6 18"/> <path d="m6 6 12 12"/>'),

    # misc chrome
    "ChevronRight": ("chevron-right", '<path d="m9 18 6-6-6-6"/>'),
    "ChevronLeft": ("chevron-left", '<path d="m15 18-6-6 6-6"/>'),
    "ChevronDown": ("chevron-down", '<path d="m6 9 6 6 6-6"/>'),
    "Check": ("check", '<path d="M20 6 9 17l-5-5"/>'),
    "ArrowLeft": ("arrow-left", '<path d="m12 19-7-7 7-7"/> <path d="M19 12H5"/>'),
    "Cast": ("cast", '<path d="M2 8V6a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6"/> <path d="M2 12a9 9 0 0 1 8 8"/> <path d="M2 16a5 5 0 0 1 4 4"/> <line x1="2" x2="2.01" y1="20" y2="20"/>'),
}


# ---------------------------------------------------------------------------
# The contested slots.
#
# Six choices had no single obvious Lucide answer, so each keeps its candidates
# here alongside the one in force. Switching is a one-word edit to CHOSEN and
# nothing else moves; the rejected options stay readable so the decision does
# not have to be reconstructed later.
#
# A value starting with "<" is Lucide markup and gets converted; anything else
# is already WPF geometry.
# ---------------------------------------------------------------------------

ALTERNATIVES = {
    # One decision covers both rings, so Replay and Forward share option names.
    "Replay": {
        "rotate": '<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/> <path d="M3 3v5h5"/>',
        "chevrons": '<path d="M12 6a2 2 0 0 0-3.414-1.414l-6 6a2 2 0 0 0 0 2.828l6 6A2 2 0 0 0 12 18z"/> <path d="M22 6a2 2 0 0 0-3.414-1.414l-6 6a2 2 0 0 0 0 2.828l6 6A2 2 0 0 0 22 18z"/>',
        "hand-drawn": seek_arrow(forward=False),
    },
    "Forward": {
        "rotate": '<path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"/> <path d="M21 3v5h-5"/>',
        "chevrons": '<path d="M12 6a2 2 0 0 1 3.414-1.414l6 6a2 2 0 0 1 0 2.828l-6 6A2 2 0 0 1 12 18z"/> <path d="M2 6a2 2 0 0 1 3.414-1.414l6 6a2 2 0 0 1 0 2.828l-6 6A2 2 0 0 1 2 18z"/>',
        "hand-drawn": seek_arrow(forward=True),
    },
    "Pip": {
        "picture-in-picture-2": '<path d="M21 9V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v10c0 1.1.9 2 2 2h4"/> <rect width="10" height="7" x="12" y="13" rx="2"/>',
        "picture-in-picture": '<path d="M2 10h6V4"/> <path d="m2 4 6 6"/> <path d="M21 10V7a2 2 0 0 0-2-2h-7"/> <path d="M3 14v2a2 2 0 0 0 2 2h3"/> <rect x="12" y="14" width="10" height="7" rx="1"/>',
        "hand-drawn": "M 3,5 L 21,5 L 21,19 L 3,19 Z M 12.5,12 L 20,12 L 20,17.5 L 12.5,17.5 Z",
    },
    "VideoFit": {
        "proportions": '<rect width="20" height="16" x="2" y="4" rx="2"/> <path d="M12 9v11"/> <path d="M2 9h13a2 2 0 0 1 2 2v9"/>',
        "ratio": '<rect width="12" height="20" x="6" y="2" rx="2"/> <rect width="20" height="12" x="2" y="6" rx="2"/>',
        "scan": '<path d="M3 7V5a2 2 0 0 1 2-2h2"/> <path d="M17 3h2a2 2 0 0 1 2 2v2"/> <path d="M21 17v2a2 2 0 0 1-2 2h-2"/> <path d="M7 21H5a2 2 0 0 1-2-2v-2"/>',
        "hand-drawn": ("M 3,7 L 3,4 L 6,4 M 18,4 L 21,4 L 21,7 M 21,17 L 21,20 L 18,20 "
                       "M 6,20 L 3,20 L 3,17 M 7,8 L 17,8 L 17,16 L 7,16 Z"),
    },
    "Quality": {
        "signal": '<path d="M2 20h.01"/> <path d="M7 20v-4"/> <path d="M12 20v-8"/> <path d="M17 20V8"/> <path d="M22 4v16"/>',
        "chart-column": '<path d="M3 3v16a2 2 0 0 0 2 2h16"/> <path d="M18 17V9"/> <path d="M13 17V5"/> <path d="M8 17v-3"/>',
        "hand-drawn": ("M 4,19.5 L 4,15 M 9.3,19.5 L 9.3,11 "
                       "M 14.7,19.5 L 14.7,7.5 M 20,19.5 L 20,4.5"),
    },
    "Upscale": {
        "sparkles": '<path d="M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594z"/> <path d="M20 2v4"/> <path d="M22 4h-4"/> <circle cx="4" cy="20" r="2"/>',
        "wand-sparkles": '<path d="m21.64 3.64-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72"/> <path d="m14 7 3 3"/> <path d="M5 6v4"/> <path d="M19 14v4"/> <path d="M10 2v2"/> <path d="M7 8H3"/> <path d="M21 16h-4"/> <path d="M11 3H9"/>',
        "hand-drawn": ("M 4,9 L 4,4 L 9,4 M 15,4 L 20,4 L 20,9 M 20,15 L 20,20 L 15,20 "
                       "M 9,20 L 4,20 L 4,15 M 12,8.5 L 13.2,11 L 15.5,12 L 13.2,13 "
                       "L 12,15.5 L 10.8,13 L 8.5,12 L 10.8,11 Z"),
    },
    "Restore": {
        "copy": '<rect width="14" height="14" x="8" y="8" rx="2" ry="2"/> <path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/>',
        "hand-drawn": "M 8,8 L 8,5 L 19,5 L 19,16 L 16,16 M 5,8 L 16,8 L 16,19 L 5,19 Z",
    },
}

# The option in force for each contested slot.
CHOSEN = {
    "Replay": "chevrons",
    "Forward": "chevrons",
    "Pip": "picture-in-picture-2",
    "VideoFit": "hand-drawn",
    "Quality": "signal",
    "Upscale": "sparkles",
    "Restore": "hand-drawn",
}


HEADER = """<!--
    Generated by tools/make_icons.py - do not edit by hand.

    Stroked outline icons on a 24x24 grid. Draw one with the IconPath style:

        <Path Style="{StaticResource IconPath}" Data="{StaticResource IconPlay}"/>

    Icons are from Lucide (https://lucide.dev), converted from SVG to WPF
    geometry. A few slots remain hand-drawn; each is marked below.

    ===========================================================================
    Lucide License

    ISC License

    Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as
    part of Feather (MIT). All other copyright (c) for Lucide are held by
    Lucide Contributors 2022.

    Permission to use, copy, modify, and/or distribute this software for any
    purpose with or without fee is hereby granted, provided that the above
    copyright notice and this permission notice appear in all copies.

    THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
    WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
    MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
    SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
    WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
    ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR
    IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
    ===========================================================================
-->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Style x:Key="IconPath" TargetType="Path">
        <Setter Property="StrokeThickness" Value="1.7"/>
        <Setter Property="StrokeStartLineCap" Value="Round"/>
        <Setter Property="StrokeEndLineCap" Value="Round"/>
        <Setter Property="StrokeLineJoin" Value="Round"/>
        <Setter Property="Stretch" Value="Uniform"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
    </Style>

"""


def resolve(value):
    """SVG markup becomes WPF geometry; anything else is already geometry."""
    return svgpath.svg_to_wpf(value) if value.lstrip().startswith("<") else value


def build():
    """Every icon as (name, geometry, provenance comment)."""
    icons = {}
    for name, (lucide, markup) in LUCIDE.items():
        icons[name] = (svgpath.svg_to_wpf(markup), f"lucide {lucide}")
    for name, options in ALTERNATIVES.items():
        choice = CHOSEN[name]
        if choice not in options:
            raise SystemExit(f"{name}: no such option {choice!r}; "
                             f"pick one of {sorted(options)}")
        note = "hand-drawn" if choice == "hand-drawn" else f"lucide {choice}"
        rejected = sorted(o for o in options if o != choice)
        icons[name] = (resolve(options[choice]),
                        f"{note} (chosen over {', '.join(rejected)})")
    return icons


def main():
    icons = build()
    lines = [HEADER]
    for name in sorted(icons):
        data, note = icons[name]
        x0, y0, x1, y1 = svgpath.bounds(data)
        if not (-1 <= x0 and x1 <= 25 and -1 <= y0 and y1 <= 25):
            raise SystemExit(f"{name}: {(x0, y0, x1, y1)} is off the 24x24 grid")
        lines.append(f'    <!-- {note} -->\n')
        lines.append(f'    <Geometry x:Key="Icon{name}">{data}</Geometry>\n')
    lines.append("\n</ResourceDictionary>\n")
    text = "".join(lines)

    # XAML resources are parsed when the app loads, not when it builds, so a
    # malformed dictionary ships silently and crashes on startup. Catching it
    # here costs nothing. (The licence block earned this: a run of dashes is a
    # legal separator everywhere except inside an XML comment.)
    try:
        ElementTree.fromstring(text)
    except ElementTree.ParseError as exc:
        raise SystemExit(f"generated Icons.xaml is not well-formed XML: {exc}")

    out = os.path.join(REPO, "Icons.xaml")
    with open(out, "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write(text)

    hand_drawn = sum(1 for n in icons if icons[n][1].startswith("hand-drawn"))
    print(f"Icons.xaml written with {len(icons)} icons "
          f"({len(icons) - hand_drawn} from Lucide, {hand_drawn} hand-drawn)")


if __name__ == "__main__":
    main()
