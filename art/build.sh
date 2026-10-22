#!/bin/bash
# 星球模板图标（50x50）。生成经过：
#   prompt.txt → draft.png（八个方案，挑中右图左下那一格 B3）
#   prompt_coarse.txt + B3 → draft_coarse.png（照原样在 50x50 网格上重画成粗像素）
#   prompt_round.txt + draft_coarse.png + ref_circle.png（标准像素圆）→ draft_round.png
# 这里把 draft_round.png 还原成 50x50，再把星球校正成直径 36 格的正圆
set -euo pipefail
cd "$(dirname "$0")"
tmp=$(mktemp -d)
python3 pixelize_tile.py draft_round.png 1 1 0 "$tmp/tile.png" 50
python3 round_planet.py draft_round.png "$tmp/tile.png" ../GameResources/ui/globe_template_planet.png 36
rm -rf "$tmp"
