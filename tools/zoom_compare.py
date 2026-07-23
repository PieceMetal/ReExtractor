from PIL import Image
import os

# side-by-side native-res comparison: Noesis ground truth vs our BCnEncoder decode
gt = Image.open(r"D:\texdump\groundtruth_cloth_top.png").convert("RGB")
mine = Image.open(r"D:\texdump\probe2\tex6_ch001_00_00_Cloth_Top_Mat_ALBD_tex.png").convert("RGB")
print("gt", gt.size, "mine", mine.size)

box = (96, 96, 160, 160)
scale = 6
gt_z = gt.crop(box).resize(((box[2]-box[0])*scale, (box[3]-box[1])*scale), Image.NEAREST)
mine_z = mine.crop(box).resize(((box[2]-box[0])*scale, (box[3]-box[1])*scale), Image.NEAREST)

combo = Image.new("RGB", (gt_z.width + mine_z.width + 8, max(gt_z.height, mine_z.height)), (40, 40, 40))
combo.paste(gt_z, (0, 0))
combo.paste(mine_z, (gt_z.width + 8, 0))
combo.save(r"D:\texdump\compare_zoom.png")

# full-size diff stats
import math
if gt.size == mine.size:
    px_gt = gt.load(); px_m = mine.load()
    total = 0; diff_count = 0; sum_abs = 0
    for y in range(gt.height):
        for x in range(gt.width):
            a = px_gt[x, y]; b = px_m[x, y]
            d = abs(a[0]-b[0]) + abs(a[1]-b[1]) + abs(a[2]-b[2])
            sum_abs += d
            total += 1
            if d > 30: diff_count += 1
    print(f"pixels differing >30/765: {diff_count}/{total} ({100.0*diff_count/total:.1f}%), mean abs diff {sum_abs/total:.1f}")
print("saved compare_zoom.png")
