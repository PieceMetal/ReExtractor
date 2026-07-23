from PIL import Image
import os

# streaming 2048 cloth_top decoded earlier by probe1 (BCnEncoder, verified correct)
jobs = [
    (r"D:\texdump\probe\ch001_00_00_cloth_top_mat_albd_tex_251111100.png", (900, 900, 1152, 1152), "streaming2048_cloth_top"),
    (r"D:\texdump\probe\ch001_00_00_parts_mat_albd_tex_251111100.png", (900, 900, 1152, 1152), "streaming2048_parts"),
]
for name, box, tag in jobs:
    if not os.path.exists(name):
        print("MISSING", name)
        continue
    img = Image.open(name)
    print(tag, img.size)
    z = img.crop(box).resize(((box[2] - box[0]) * 2, (box[3] - box[1]) * 2), Image.NEAREST)
    out = rf"D:\texdump\zoom_{tag}.png"
    z.save(out)
    print("  ->", out)
