from PIL import Image, ImageDraw

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 背景：圆角矩形 + 垂直渐变蓝
try:
    grad = Image.new("RGBA", (1, S))
    for y in range(S):
        t = y / S
        r = int(31 + (20 - 31) * t)
        g = int(78 + (90 - 78) * t)
        b = int(140 + (176 - 140) * t)
        grad.putpixel((0, y), (r, g, b, 255))
    grad = grad.resize((S, S))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([6, 6, S - 6, S - 6], radius=52, fill=255)
    img.paste(grad, (0, 0), mask)
except Exception:
    d.rounded_rectangle([6, 6, S - 6, S - 6], radius=52, fill=(31, 78, 140, 255))

# 前景：白色三角网络节点（代理/转发语义）
white = (255, 255, 255, 255)
line_w = 13
r = 26
d.line([(70, 186), (186, 70)], fill=white, width=line_w)
d.line([(70, 186), (186, 186)], fill=white, width=line_w)
d.line([(186, 70), (186, 186)], fill=white, width=line_w)
d.ellipse([(70 - r, 186 - r), (70 + r, 186 + r)], fill=white)
d.ellipse([(186 - r, 70 - r), (186 + r, 70 + r)], fill=white)
d.ellipse([(186 - r, 186 - r), (186 + r, 186 + r)], fill=white)

img.save("app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("app.ico saved")
