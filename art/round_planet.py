"""把还原后图标里的星球校正成正圆：模型画的圆常常偏扁、边是一段段直线。
从草图里找出星球（亮色连成的最大一块），量出中心和横纵半径；输出上画一个标准像素圆，圆内每格按比例到模型画的椭圆里取色
（大陆、云、冰盖都是模型的像素，只是等比拉回正圆），最外一圈用模型描边的颜色，圆外照原样。
用法: round_planet.py 草图.png 已还原.png 输出.png 直径"""
import sys
import numpy as np
from PIL import Image

draft_path, tile_path, out, diameter = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
im = np.asarray(Image.open(draft_path).convert("RGB")).astype(float)
H, W = im.shape[:2]
tile_img = Image.open(tile_path).convert("RGBA")
N = tile_img.size[0]

# 草图里方块的范围（和 pixelize_tile 一样按灰底找）
bg = np.median(np.concatenate([im[:8].reshape(-1, 3), im[-8:].reshape(-1, 3)]), axis=0)
mask = np.abs(im - bg).sum(axis=2) > 40
ys, xs = np.nonzero(mask)
x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1

# 星球：草图缩小 8 倍后，绿通道亮的格子（海洋、陆地、云、描边都比太空亮，星云偏紫绿通道暗）连成的最大一块
step = 8
small = im[y0:y1:step, x0:x1:step]
bright = small[..., 1] > 110
label = np.zeros(bright.shape, np.int32)
best, best_pts = 0, None
for sy0, sx0 in zip(*np.nonzero(bright)):
    if label[sy0, sx0]: continue
    label[sy0, sx0] = 1
    stack, pts = [(sy0, sx0)], []
    while stack:
        y, x = stack.pop()
        pts.append((y, x))
        for ny, nx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
            if 0 <= ny < bright.shape[0] and 0 <= nx < bright.shape[1] and bright[ny, nx] and not label[ny, nx]:
                label[ny, nx] = 1
                stack.append((ny, nx))
    if len(pts) > best: best, best_pts = len(pts), pts
yy = np.array([p[0] for p in best_pts]) * step + y0
xx = np.array([p[1] for p in best_pts]) * step + x0
ecx, ecy = (xx.min() + xx.max() + step) / 2, (yy.min() + yy.max() + step) / 2
ax, ay = (xx.max() - xx.min() + step) / 2, (yy.max() - yy.min() + step) / 2
# 描边颜色：外框边上那一圈里最亮的青色
r, g, b = im[..., 0], im[..., 1], im[..., 2]
ring = np.abs(np.hypot((np.arange(W)[None, :] - ecx) / ax, (np.arange(H)[:, None] - ecy) / ay) - 0.98) < 0.02
rimpix = im[ring & (b > 200) & (g > 190)]
rim_color = np.median(rimpix, axis=0)
print(f"椭圆中心 ({ecx:.0f}, {ecy:.0f})，半径 {ax:.0f} x {ay:.0f}", file=sys.stderr)

# 输出：星球圆心放在椭圆中心对应的格子上
px = np.asarray(tile_img).copy()
sx, sy = (x1 - x0) / N, (y1 - y0) / N
ccx, ccy = (ecx - x0) / sx - 0.5, (ecy - y0) / sy - 0.5
R = diameter / 2
inside = np.zeros((N, N), bool)
for j in range(N):
    for i in range(N):
        inside[j, i] = (i - ccx) ** 2 + (j - ccy) ** 2 <= R * R
edge = inside & ~(np.roll(inside, 1, 0) & np.roll(inside, -1, 0) & np.roll(inside, 1, 1) & np.roll(inside, -1, 1))
for j in range(N):
    for i in range(N):
        if not inside[j, i]: continue
        if edge[j, i]:
            px[j, i, :3] = rim_color
            continue
        # 圆内一点按比例对到椭圆里（留出模型描边的那一圈）
        u, v = (i - ccx) / (R - 1), (j - ccy) / (R - 1)
        ix = int(round(ecx + u * (ax - sx * 1.2)))
        iy = int(round(ecy + v * (ay - sy * 1.2)))
        px[j, i, :3] = im[iy, ix]
Image.fromarray(px).save(out)
