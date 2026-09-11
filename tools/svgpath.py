"""Converts Lucide SVG markup into WPF geometry data.

Lucide draws with the full SVG vocabulary: <rect> with corner radii, <circle>,
<line>, relative commands, cubic and quadratic curves, and shorthand (H, V, S,
T). WPF's geometry mini-language covers most of the path commands but knows
nothing about the shape elements, and its tolerance for SVG's compressed number
packing ("a2 2 0 0 1 3.008-1.728") is not something to bet the icon set on.

So everything is parsed properly and re-emitted as absolute M/L/C/Q/A/Z with
explicit separators. Shape elements become paths on the way through. The output
is deliberately verbose rather than clever - it is generated, and being able to
read it is worth more than being able to shorten it.
"""
import re

NUMBER = re.compile(r"[+-]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][+-]?\d+)?")
ELEMENT = re.compile(r"<(path|rect|circle|ellipse|line|polyline|polygon)\b([^>]*)>")
ATTR = re.compile(r"([\w:-]+)\s*=\s*\"([^\"]*)\"")

# How many decimals survive into the XAML. Lucide authors to 2-3 places and the
# icons are drawn at 16-26px, so more than this is noise in the diff.
PLACES = 3


def num(v):
    """Format a coordinate, dropping the trailing zeros a fixed format leaves."""
    s = f"{v:.{PLACES}f}"
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


def point(x, y):
    return f"{num(x)},{num(y)}"


class _Reader:
    """Tokenizer for one SVG path 'd' attribute.

    Arc flags are read as single characters rather than numbers. SVG permits
    "0 0 1" to be written "001", and a number-first tokenizer silently reads that
    as the single value 1 and then runs off the end of the command.
    """

    def __init__(self, d):
        self.d = d
        self.i = 0

    def _skip(self):
        while self.i < len(self.d) and self.d[self.i] in " ,\t\r\n":
            self.i += 1

    def more(self):
        self._skip()
        return self.i < len(self.d)

    def number(self):
        self._skip()
        m = NUMBER.match(self.d, self.i)
        if not m:
            raise ValueError(f"expected a number at {self.i} in {self.d!r}")
        self.i = m.end()
        return float(m.group())

    def flag(self):
        self._skip()
        c = self.d[self.i]
        if c not in "01":
            raise ValueError(f"expected an arc flag at {self.i} in {self.d!r}")
        self.i += 1
        return c

    def command(self):
        """Next command letter, or None when the previous one repeats implicitly."""
        self._skip()
        c = self.d[self.i]
        if c.isalpha():
            self.i += 1
            return c
        return None


def path_to_wpf(d):
    """Absolute M/L/C/Q/A/Z equivalent of an SVG path."""
    r = _Reader(d)
    out = []
    x = y = 0.0          # current point
    sx = sy = 0.0        # start of the current subpath, for Z
    cx = cy = 0.0        # reflected control point for S / T
    prev = ""
    cmd = None

    while r.more():
        c = r.command()
        if c is None:
            if cmd is None:
                raise ValueError(f"path starts without a command: {d!r}")
            # An implicit repeat of M continues as L, per the SVG grammar.
            c = {"M": "L", "m": "l"}.get(cmd, cmd)
        cmd = c
        rel = c.islower()
        u = c.upper()

        if u == "Z":
            out.append("Z")
            x, y = sx, sy
        elif u == "M":
            px, py = r.number(), r.number()
            x, y = (x + px, y + py) if rel else (px, py)
            sx, sy = x, y
            out.append(f"M {point(x, y)}")
        elif u == "L":
            px, py = r.number(), r.number()
            x, y = (x + px, y + py) if rel else (px, py)
            out.append(f"L {point(x, y)}")
        elif u == "H":
            px = r.number()
            x = x + px if rel else px
            out.append(f"L {point(x, y)}")
        elif u == "V":
            py = r.number()
            y = y + py if rel else py
            out.append(f"L {point(x, y)}")
        elif u == "C":
            a, b, cc, dd, ee, ff = (r.number() for _ in range(6))
            if rel:
                a, b, cc, dd, ee, ff = x + a, y + b, x + cc, y + dd, x + ee, y + ff
            out.append(f"C {point(a, b)} {point(cc, dd)} {point(ee, ff)}")
            cx, cy = cc, dd
            x, y = ee, ff
        elif u == "S":
            cc, dd, ee, ff = (r.number() for _ in range(4))
            if rel:
                cc, dd, ee, ff = x + cc, y + dd, x + ee, y + ff
            a, b = (2 * x - cx, 2 * y - cy) if prev in "CS" else (x, y)
            out.append(f"C {point(a, b)} {point(cc, dd)} {point(ee, ff)}")
            cx, cy = cc, dd
            x, y = ee, ff
        elif u == "Q":
            a, b, ee, ff = (r.number() for _ in range(4))
            if rel:
                a, b, ee, ff = x + a, y + b, x + ee, y + ff
            out.append(f"Q {point(a, b)} {point(ee, ff)}")
            cx, cy = a, b
            x, y = ee, ff
        elif u == "T":
            ee, ff = r.number(), r.number()
            if rel:
                ee, ff = x + ee, y + ff
            a, b = (2 * x - cx, 2 * y - cy) if prev in "QT" else (x, y)
            out.append(f"Q {point(a, b)} {point(ee, ff)}")
            cx, cy = a, b
            x, y = ee, ff
        elif u == "A":
            rx, ry, rot = r.number(), r.number(), r.number()
            large, sweep = r.flag(), r.flag()
            ee, ff = r.number(), r.number()
            if rel:
                ee, ff = x + ee, y + ff
            # WPF's sweep flag carries the same meaning as SVG's: 1 is clockwise.
            out.append(f"A {num(rx)},{num(ry)} {num(rot)} {large} {sweep} {point(ee, ff)}")
            x, y = ee, ff
        else:
            raise ValueError(f"unhandled path command {c!r} in {d!r}")

        prev = u

    return " ".join(out)


def rect_to_wpf(x, y, w, h, rx=0.0, ry=0.0):
    """Rounded rectangle as four lines and four corner arcs.

    SVG's rule that a missing ry mirrors rx (and vice versa) is what makes
    Lucide's rx="2" corners come out round rather than square.
    """
    if rx <= 0 and ry <= 0:
        return (f"M {point(x, y)} L {point(x + w, y)} "
                f"L {point(x + w, y + h)} L {point(x, y + h)} Z")
    rx = rx or ry
    ry = ry or rx
    rx, ry = min(rx, w / 2), min(ry, h / 2)
    r = f"{num(rx)},{num(ry)}"
    return (
        f"M {point(x + rx, y)} L {point(x + w - rx, y)} A {r} 0 0 1 {point(x + w, y + ry)} "
        f"L {point(x + w, y + h - ry)} A {r} 0 0 1 {point(x + w - rx, y + h)} "
        f"L {point(x + rx, y + h)} A {r} 0 0 1 {point(x, y + h - ry)} "
        f"L {point(x, y + ry)} A {r} 0 0 1 {point(x + rx, y)} Z"
    )


def ellipse_to_wpf(cx, cy, rx, ry):
    """Two half arcs. A single arc back to its own start point draws nothing."""
    r = f"{num(rx)},{num(ry)}"
    return (f"M {point(cx - rx, cy)} A {r} 0 1 1 {point(cx + rx, cy)} "
            f"A {r} 0 1 1 {point(cx - rx, cy)} Z")


def points_to_wpf(raw, close):
    vals = [float(v) for v in NUMBER.findall(raw)]
    pts = list(zip(vals[0::2], vals[1::2]))
    if not pts:
        return ""
    body = " ".join(f"L {point(*q)}" for q in pts[1:])
    return f"M {point(*pts[0])} {body}{' Z' if close else ''}".strip()


def svg_to_wpf(markup):
    """Convert a fragment of Lucide SVG markup into one WPF geometry string."""
    parts = []
    for m in ELEMENT.finditer(markup):
        tag = m.group(1)
        a = dict(ATTR.findall(m.group(2)))

        def g(k, dflt=0.0):
            return float(a[k]) if a.get(k) else dflt

        if tag == "path":
            parts.append(path_to_wpf(a["d"]))
        elif tag == "rect":
            parts.append(rect_to_wpf(g("x"), g("y"), g("width"), g("height"),
                                     g("rx"), g("ry")))
        elif tag == "circle":
            parts.append(ellipse_to_wpf(g("cx"), g("cy"), g("r"), g("r")))
        elif tag == "ellipse":
            parts.append(ellipse_to_wpf(g("cx"), g("cy"), g("rx"), g("ry")))
        elif tag == "line":
            parts.append(f"M {point(g('x1'), g('y1'))} L {point(g('x2'), g('y2'))}")
        elif tag in ("polyline", "polygon"):
            parts.append(points_to_wpf(a.get("points", ""), tag == "polygon"))
    if not parts:
        raise ValueError(f"no drawable elements in {markup!r}")
    return " ".join(p for p in parts if p)


def bounds(wpf):
    """Rough extent of a converted path, from its on-path points only.

    Control points and arc bulges are ignored, so this is a sanity check for
    'did this land on the 24x24 grid', not a true bounding box.
    """
    xs, ys = [], []
    r = _Reader(wpf)
    while r.more():
        c = r.command()
        if c is None or c == "Z":
            continue
        if c == "A":
            r.number(), r.number(), r.number(), r.flag(), r.flag()
        elif c == "C":
            for _ in range(4):
                r.number()
        elif c == "Q":
            for _ in range(2):
                r.number()
        xs.append(r.number())
        ys.append(r.number())
    return min(xs), min(ys), max(xs), max(ys)
