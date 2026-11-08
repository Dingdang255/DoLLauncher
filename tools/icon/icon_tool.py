"""图标帧提取 / 重建。

用法（在 E:\\h\\DoL\\dev 下）：
  python tools\\icon\\icon_tool.py extract   # 把 Assets\\dol.ico 的每一帧导到 tools\\icon\\icon-frames\\<size>.png
  python tools\\icon\\icon_tool.py build     # 重建 dol.ico：<=32 用 16px 原图最近邻放大，>32 用 Vrelicon.png 缩放
"""
import os
import sys
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ICO = os.path.join(ROOT, "src", "DoLLauncher", "Assets", "dol.ico")
BIG = os.path.join(ROOT, "tools", "icon", "Vrelicon.png")
FRAMES = os.path.join(ROOT, "tools", "icon", "icon-frames")
SMALL_SIZES = (16, 24, 32)
BIG_SIZES = (48, 64, 128, 256)


def extract():
    os.makedirs(FRAMES, exist_ok=True)
    im = Image.open(ICO)
    sizes = sorted(im.info.get("sizes", [im.size]))
    for size in sizes:
        im.size = size          # ICO 按地址取值：先改 size 再 load
        im.load()
        im.convert("RGBA").save(os.path.join(FRAMES, f"{size[0]}.png"))
    print("导出到", FRAMES, sizes)


def build():
    small = Image.open(os.path.join(FRAMES, "16.png")).convert("RGBA")
    big = Image.open(BIG).convert("RGBA")
    frames = []
    for size in SMALL_SIZES:
        frames.append(small.resize((size, size), Image.NEAREST))
    for size in BIG_SIZES:
        frames.append(big.resize((size, size), Image.LANCZOS))
    frames[-1].save(ICO, format="ICO", sizes=[f.size for f in frames], append_images=frames[:-1])
    print("已写入", ICO, os.path.getsize(ICO), "字节")


if __name__ == "__main__":
    {"extract": extract, "build": build}[sys.argv[1]]()
