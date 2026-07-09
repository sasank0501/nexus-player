from PIL import Image, ImageDraw

SS = 8  # supersample factor

def draw_icon(size, detailed=True):
    """Design space is 256; draw at size*SS then downscale."""
    W = 256 * SS
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = SS  # 1 design unit -> s px

    # rounded square: pure black #050505, hairline #2A2A2A border
    m = 10 * s
    d.rounded_rectangle([m, m, W - m, W - m], radius=58 * s,
                        fill=(5, 5, 5, 255), outline=(42, 42, 42, 255), width=2 * s)

    white = (242, 242, 242, 255)
    if detailed:
        # play triangle (rounded joints), optically centered above the seek bar
        tri = [(96 * s, 58 * s), (96 * s, 170 * s), (192 * s, 114 * s)]
        d.polygon(tri, fill=white)
        d.line(tri + [tri[0]], fill=white, width=10 * s, joint="curve")

        # thin seek bar: played (white) + remainder (dim) + position dot
        y = 200 * s
        h = 3 * s          # half-thickness
        d.rounded_rectangle([64 * s, y - h, 140 * s, y + h], radius=h, fill=white)
        d.rounded_rectangle([140 * s, y - h, 192 * s, y + h], radius=h, fill=(255, 255, 255, 64))
        r = 9 * s
        d.ellipse([140 * s - r, y - r, 140 * s + r, y + r], fill=(255, 255, 255, 255))
    else:
        # tiny sizes: just a big triangle, no bar
        tri = [(92 * s, 62 * s), (92 * s, 194 * s), (194 * s, 128 * s)]
        d.polygon(tri, fill=white)
        d.line(tri + [tri[0]], fill=white, width=10 * s, joint="curve")

    return img.resize((size, size), Image.LANCZOS)

sizes = [(256, True), (64, True), (48, True), (32, False), (24, False), (16, False)]
imgs = [draw_icon(sz, det) for sz, det in sizes]

out = r"C:\Users\Sasank\mpv-frontend\nexus.ico"
imgs[0].save(out, format="ICO", append_images=imgs[1:])
print("wrote", out)

# preview sheet: icon at several sizes on a Win11-taskbar-ish dark backdrop
prev = Image.new("RGBA", (720, 360), (32, 32, 32, 255))
prev.alpha_composite(imgs[0], (40, 52))
prev.alpha_composite(imgs[1], (360, 60))
prev.alpha_composite(imgs[2], (360, 160))
prev.alpha_composite(imgs[3].resize((32, 32)), (360, 250))
prev.alpha_composite(draw_icon(16, False), (360, 310))
# also on light backdrop strip
light = Image.new("RGBA", (240, 360), (240, 240, 240, 255))
prev.paste(light, (480, 0))
prev.alpha_composite(draw_icon(96, True), (520, 60))
prev.alpha_composite(draw_icon(32, True), (520, 200))
prev.convert("RGB").save(r"C:\Users\Sasank\AppData\Local\Temp\claude\C--Users-Sasank\d342180d-ec5f-4b78-b43c-2682312bf317\scratchpad\icon_preview.png")
print("preview written")
