"""把草图里一个整块铺满的方形图标还原成 N×N 像素：先找出方块的边界，量出 AI 画的像素块边长和起点
（找颜色突变最集中的等距位置），在每块中间取色，尽量原样保留 AI 的像素；块数和 N 不一样时再按最近邻缩放到 N。
原版的模板图标四角是圆的，还原后照样挖掉角上的像素。
用法: pixelize_tile.py 草图.png 行 列 第几格 输出.png [N=50]（格子从 0 开始，按从左到右、从上到下）"""
import sys
import numpy as np
from PIL import Image

src, rows, cols, index, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
N = int(sys.argv[6]) if len(sys.argv) > 6 else 50
im = np.asarray(Image.open(src).convert("RGB")).astype(float)
H, W = im.shape[:2]
r, c = divmod(index, cols)
cell = im[r * H // rows:(r + 1) * H // rows, c * W // cols:(c + 1) * W // cols]

# 方块边界：和灰底差得多的像素
bg = np.median(np.concatenate([cell[:8].reshape(-1, 3), cell[-8:].reshape(-1, 3)]), axis=0)
mask = np.abs(cell - bg).sum(axis=2) > 40
ys, xs = np.nonzero(mask)
tile = cell[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def phase_score(g, p):
    best = (0, 0)
    for ph in np.arange(0, p, 0.2):
        idx = np.floor(np.arange(ph, len(g) - 1, p)).astype(int)
        s = g[idx].mean() / g.mean()
        if s > best[0]: best = (s, ph)
    return best


def grid(gx, gy, lo, hi):
    best = (0, 0, 0, 0)
    for p in np.arange(lo, hi, 0.1):
        sx, phx = phase_score(gx, p)
        sy, phy = phase_score(gy, p)
        if sx + sy > best[0]: best = (sx + sy, p, phx, phy)
    return best[1:]


gx = np.abs(np.diff(tile, axis=1)).sum(axis=(0, 2))
gy = np.abs(np.diff(tile, axis=0)).sum(axis=(1, 2))
guess = tile.shape[1] / N
p, phx, phy = grid(gx, gy, guess * 0.97, guess * 1.03)
centers_x = np.arange(phx + p / 2, tile.shape[1], p).astype(int)
centers_y = np.arange(phy + p / 2, tile.shape[0], p).astype(int)
px = tile[np.ix_(centers_y, centers_x)].astype(np.uint8)
print(f"方块 {tile.shape[1]}x{tile.shape[0]}，块边长 {p:.1f}，{len(centers_x)}x{len(centers_y)} 块", file=sys.stderr)
img = Image.fromarray(px)
if img.size != (N, N): img = img.resize((N, N), Image.NEAREST)
# 和原版模板图标一样，每个角挖掉三个像素（第一行两个、第二行一个）
img = img.convert("RGBA")
for x, y in ((0, 0), (1, 0), (0, 1)):
    for cx, cy in ((x, y), (N - 1 - x, y), (x, N - 1 - y), (N - 1 - x, N - 1 - y)):
        img.putpixel((cx, cy), (0, 0, 0, 0))
img.save(out)
