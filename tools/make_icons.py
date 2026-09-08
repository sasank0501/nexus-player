"""Generates Icons.xaml — the stroked outline icon set.

The reference player uses thin stroked icons rather than filled glyphs, which is
most of why it reads lighter than v1's Segoe Fluent look. These are authored as
plain geometry on a 24x24 grid so they stay crisp at any size and keep the source
free of private-use-area characters (which silently break exact-match editing).

Arcs are computed rather than eyeballed: guessing sweep flags and endpoints by
hand is how you end up with a rewind icon that curls the wrong way.

Run:  python tools/make_icons.py
"""
import math

CX = CY = 12.0


def pt(angle_deg, r, cx=CX, cy=CY):
    """Point on a circle. 0 deg = right, angles increase clockwise on screen
    (WPF y grows downward), which matches how the arcs read visually."""
    a = math.radians(angle_deg)
    return (cx + r * math.cos(a), cy + r * math.sin(a))


def f(v):
    return f"{v:.2f}".rstrip("0").rstrip(".")


def p(point):
    return f"{f(point[0])},{f(point[1])}"


def arc(start_deg, end_deg, r, cx=CX, cy=CY, clockwise=True):
    """Arc path fragment from start_deg to end_deg."""
    s, e = pt(start_deg, r, cx, cy), pt(end_deg, r, cx, cy)
    sweep = (end_deg - start_deg) % 360 if clockwise else (start_deg - end_deg) % 360
    large = 1 if sweep > 180 else 0
    return f"M {p(s)} A {f(r)},{f(r)} 0 {large} {1 if clockwise else 0} {p(e)}"


def circular_arrow(clockwise):
    """A ~300 degree ring with a chevron arrowhead at the open end — the
    skip-back / skip-forward icons."""
    r = 8.0
    if clockwise:
        start, end, tip_deg = -70.0, 210.0, -70.0
    else:
        start, end, tip_deg = 250.0, -30.0, 250.0
    ring = arc(start, end, r, clockwise=clockwise)
    tip = pt(tip_deg, r)
    # tangent at the tip; barbs trail back along it
    tangent = tip_deg + (-90 if clockwise else 90)
    back = math.radians(tangent + 180)
    bx, by = math.cos(back), math.sin(back)
    perp_x, perp_y = -by, bx
    L, W = 4.4, 3.0
    b1 = (tip[0] + bx * L + perp_x * W, tip[1] + by * L + perp_y * W)
    b2 = (tip[0] + bx * L - perp_x * W, tip[1] + by * L - perp_y * W)
    return f"{ring} M {p(b1)} L {p(tip)} L {p(b2)}"


def gear():
    """Inner circle plus eight radial teeth."""
    parts = [arc(0, 180, 6.6), arc(180, 360, 6.6)]
    for i in range(8):
        a = i * 45.0 + 22.5
        parts.append(f"M {p(pt(a, 6.2))} L {p(pt(a, 9.4))}")
    return " ".join(parts)


def speaker(muted=False, arcs=2):
    body = f"M 3.5,9.5 L 7,9.5 L 11.5,5.5 L 11.5,18.5 L 7,14.5 L 3.5,14.5 Z"
    out = [body]
    if muted:
        out.append("M 15.5,9.5 L 20.5,14.5 M 20.5,9.5 L 15.5,14.5")
    else:
        for i in range(arcs):
            r = 4.0 + i * 3.2
            out.append(arc(-52, 52, r, cx=11.5, cy=12.0))
    return " ".join(out)


ICONS = {
    # transport
    "Play": "M 8,4.8 L 19.2,12 L 8,19.2 Z",
    "Pause": "M 9,4.8 L 9,19.2 M 15,4.8 L 15,19.2",
    "Replay": circular_arrow(clockwise=False),
    "Forward": circular_arrow(clockwise=True),
    "SkipNext": "M 6,5 L 15,12 L 6,19 Z M 18,4.5 L 18,19.5",
    "SkipPrev": "M 18,5 L 9,12 L 18,19 Z M 6,4.5 L 6,19.5",

    # audio
    "Volume": speaker(arcs=2),
    "VolumeLow": speaker(arcs=1),
    "VolumeMuted": speaker(muted=True),
    "AudioTrack": ("M 4,10 L 4,14 M 8,7 L 8,17 M 12,4.5 L 12,19.5 "
                   "M 16,7.5 L 16,16.5 M 20,10.5 L 20,13.5"),

    # panels
    "Subtitles": ("M 3,5 L 21,5 A 2,2 0 0 1 21,5 L 21,19 L 3,19 Z "
                  "M 6,14 L 11,14 M 14,14 L 18,14 M 6,10 L 9,10 M 12,10 L 18,10"),
    "Episodes": "M 3,6 L 14,6 M 3,12 L 14,12 M 3,18 L 10,18 M 16,15 L 22,18.5 L 16,22 Z",
    "Settings": gear(),
    "More": ("M 5,12 A 1.4,1.4 0 1 1 5,11.99 Z M 12,12 A 1.4,1.4 0 1 1 12,11.99 Z "
             "M 19,12 A 1.4,1.4 0 1 1 19,11.99 Z"),
    "Pip": "M 3,5 L 21,5 L 21,19 L 3,19 Z M 12.5,12 L 20,12 L 20,17.5 L 12.5,17.5 Z",
    "Fullscreen": ("M 3,9 L 3,3 L 9,3 M 15,3 L 21,3 L 21,9 "
                   "M 21,15 L 21,21 L 15,21 M 9,21 L 3,21 L 3,15"),
    "FullscreenExit": ("M 9,3 L 9,9 L 3,9 M 21,9 L 15,9 L 15,3 "
                       "M 15,21 L 15,15 L 21,15 M 3,15 L 9,15 L 9,21"),

    # settings rows
    "VideoFit": ("M 3,7 L 3,4 L 6,4 M 18,4 L 21,4 L 21,7 "
                 "M 21,17 L 21,20 L 18,20 M 6,20 L 3,20 L 3,17 M 7,8 L 17,8 L 17,16 L 7,16 Z"),
    "Speed": (arc(200, 340, 8.0) + " M 12,12 L 16.5,8.2 "
              "M 12,12 A 1.2,1.2 0 1 1 12,11.99 Z"),
    "Quality": ("M 4,19.5 L 4,15 M 9.3,19.5 L 9.3,11 "
                "M 14.7,19.5 L 14.7,7.5 M 20,19.5 L 20,4.5"),
    "Filters": ("M 7,4 L 7,20 M 12,4 L 12,20 M 17,4 L 17,20 "
                "M 7,9 A 1.9,1.9 0 1 1 7,8.99 Z M 12,15 A 1.9,1.9 0 1 1 12,14.99 Z "
                "M 17,7.5 A 1.9,1.9 0 1 1 17,7.49 Z"),
    "Upscale": ("M 4,9 L 4,4 L 9,4 M 15,4 L 20,4 L 20,9 M 20,15 L 20,20 L 15,20 "
                "M 9,20 L 4,20 L 4,15 M 12,8.5 L 13.2,11 L 15.5,12 L 13.2,13 "
                "L 12,15.5 L 10.8,13 L 8.5,12 L 10.8,11 Z"),
    "Preferences": ("M 5,6 L 19,6 M 5,12 L 19,12 M 5,18 L 19,18 "
                    "M 9,6 A 2,2 0 1 1 9,5.99 Z M 15,12 A 2,2 0 1 1 15,11.99 Z "
                    "M 11,18 A 2,2 0 1 1 11,17.99 Z"),

    # more-options menu
    "Stats": "M 3,15 L 7,15 L 10,7 L 14,19 L 17,12 L 21,12",
    "Screenshot": ("M 3,7.5 L 7.5,7.5 L 9,5 L 15,5 L 16.5,7.5 L 21,7.5 L 21,19 L 3,19 Z "
                   + arc(0, 180, 3.6, cy=13.2) + " " + arc(180, 360, 3.6, cy=13.2)),
    "Keyboard": ("M 2.5,6 L 21.5,6 L 21.5,18 L 2.5,18 Z M 6,9.5 L 6,9.5 M 9.5,9.5 L 9.5,9.5 "
                 "M 13,9.5 L 13,9.5 M 16.5,9.5 L 16.5,9.5 M 7.5,14 L 16.5,14"),
    "Reset": circular_arrow(clockwise=False),

    # window caption
    "Minimize": "M 5,12 L 19,12",
    "Maximize": "M 5,5 L 19,5 L 19,19 L 5,19 Z",
    "Restore": "M 8,8 L 8,5 L 19,5 L 19,16 L 16,16 M 5,8 L 16,8 L 16,19 L 5,19 Z",

    # misc chrome
    "ChevronRight": "M 9,5 L 16,12 L 9,19",
    "ChevronLeft": "M 15,5 L 8,12 L 15,19",
    "ChevronDown": "M 5,9 L 12,16 L 19,9",
    "Check": "M 4.5,12.5 L 9.5,17.5 L 19.5,6.5",
    "Close": "M 5.5,5.5 L 18.5,18.5 M 18.5,5.5 L 5.5,18.5",
    "Plus": "M 12,4.5 L 12,19.5 M 4.5,12 L 19.5,12",
    "Trash": ("M 4,6.5 L 20,6.5 M 9,6.5 L 9,4 L 15,4 L 15,6.5 "
              "M 6,6.5 L 7,20.5 L 17,20.5 L 18,6.5 M 10,10 L 10,17 M 14,10 L 14,17"),
    "Link": ("M 10,14 A 4,4 0 0 0 15.5,14 L 18.5,11 A 4,4 0 0 0 13,5.5 L 11.5,7 "
             "M 14,10 A 4,4 0 0 0 8.5,10 L 5.5,13 A 4,4 0 0 0 11,18.5 L 12.5,17"),
    "ArrowLeft": "M 20,12 L 4,12 M 10,6 L 4,12 L 10,18",
    "Folder": "M 3,19 L 3,5.5 L 9.5,5.5 L 11.5,8.5 L 21,8.5 L 21,19 Z",
    "Cast": ("M 3,17.5 A 3.5,3.5 0 0 1 6.5,21 M 3,13 A 8,8 0 0 1 11,21 "
             "M 3,8.5 A 12.5,12.5 0 0 1 15.5,21 M 3,7 L 3,4 L 21,4 L 21,21 L 18,21"),
}

HEADER = """<!--
    Generated by tools/make_icons.py — do not edit by hand.

    Stroked outline icons on a 24x24 grid, matching the reference player's
    thin-line look. Draw one with the IconPath style:

        <Path Style="{StaticResource IconPath}" Data="{StaticResource IconPlay}"/>
-->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Style x:Key="IconPath" TargetType="Path">
        <Setter Property="StrokeThickness" Value="1.5"/>
        <Setter Property="StrokeStartLineCap" Value="Round"/>
        <Setter Property="StrokeEndLineCap" Value="Round"/>
        <Setter Property="StrokeLineJoin" Value="Round"/>
        <Setter Property="Stretch" Value="Uniform"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
    </Style>

"""


def main():
    lines = [HEADER]
    for name in sorted(ICONS):
        lines.append(f'    <Geometry x:Key="Icon{name}">{ICONS[name]}</Geometry>\n')
    lines.append("\n</ResourceDictionary>\n")
    with open("Icons.xaml", "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write("".join(lines))
    print(f"Icons.xaml written with {len(ICONS)} icons")


if __name__ == "__main__":
    main()
