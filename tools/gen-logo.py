"""生成 CodecLoom 品牌资产：appicon.ico / appicon-256.png / logo-banner.png。

设计：暗色圆角底（UI 同色 #141518）+ 3 经 3 纬交织（over-under），
经线 #4F8CFF（应用主色）、纬线 #7CB8FF（浅一档），既是织机织纹也是像素阵列。
用法：python tools/gen-logo.py
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "assets"
OUT.mkdir(exist_ok=True)

BG = (20, 21, 24, 255)        # #141518 与 UI 窗口底一致
BORDER = (44, 48, 56, 255)    # #2C3038
VERT = (79, 140, 255, 255)    # #4F8CFF 应用主色（经线）
HOR = (124, 184, 255, 255)    # #7CB8FF（纬线）
TEXT = (232, 234, 237, 255)   # #E8EAED

# 256 逻辑坐标
XS = [72, 128, 184]           # 经线中心 x
YS = [72, 128, 184]           # 纬线中心 y
W = 26                        # 线宽
SPAN = (40, 216)              # 线的起止
R_TILE = 56


def draw_mark(px: int) -> Image.Image:
    """渲染 logo 图形（透明底含圆角瓷砖底）。"""
    s = px / 256

    def v(n: float) -> float:
        return n * s

    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 底：圆角瓷砖
    d.rounded_rectangle(
        [v(2), v(2), v(254), v(254)],
        radius=v(R_TILE), fill=BG, outline=BORDER, width=max(1, round(v(3))),
    )

    def capsule_v(x: float):
        d.rounded_rectangle(
            [v(x - W / 2), v(SPAN[0]), v(x + W / 2), v(SPAN[1])],
            radius=v(W / 2), fill=VERT,
        )

    def capsule_h(y: float):
        d.rounded_rectangle(
            [v(SPAN[0]), v(y - W / 2), v(SPAN[1]), v(y + W / 2)],
            radius=v(W / 2), fill=HOR,
        )

    # 1) 先铺纬线（默认在下）
    for y in YS:
        capsule_h(y)
    # 2) 再铺经线（默认在上）
    for x in XS:
        capsule_v(x)
    # 3) over-under 修正：i+j 为奇数的交点纬线在上——局部重绘纬线段
    half = 16
    for i, x in enumerate(XS):
        for j, y in enumerate(YS):
            if (i + j) % 2 == 1:
                d.rounded_rectangle(
                    [v(x - half), v(y - W / 2), v(x + half), v(y + W / 2)],
                    radius=v(7), fill=HOR,
                )
    return img


def main() -> None:
    mark512 = draw_mark(512)

    # 窗口 / 应用图标
    icon256 = mark512.resize((256, 256), Image.LANCZOS)
    icon256.save(OUT / "appicon-256.png")
    icon256.save(
        OUT / "appicon.ico",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # README 横幅：图形 + 字标
    m = draw_mark(256)
    banner = Image.new("RGBA", (900, 300), (0, 0, 0, 0))
    banner.alpha_composite(m, (22, 22))
    text = "CodecLoom"
    for cand in ("segoeuib.ttf", "arialbd.ttf", "msyhbd.ttc"):
        fp = Path("C:/Windows/Fonts") / cand
        if fp.exists():
            font = ImageFont.truetype(str(fp), 88)
            break
    else:
        font = ImageFont.load_default()
    bd = ImageDraw.Draw(banner)
    tw = bd.textlength(text, font=font)
    bd.text((320 + max(0, (560 - tw) // 2), 106), text, font=font, fill=TEXT)
    banner.save(OUT / "logo-banner.png")

    print("OK ->", *(f"{p.name} {p.stat().st_size/1024:.1f} KB" for p in sorted(OUT.iterdir())), sep="\n")


if __name__ == "__main__":
    main()
