from PIL import Image
import os

jobs = [
    (r"D:\texdump\probe2\tex6_ch001_00_00_Cloth_Top_Mat_ALBD_tex.png", (96, 96, 160, 160)),
    (r"D:\texdump\probe7\pl_common_body_mat_albd_tex_251111100_bcn.png", (96, 96, 160, 160)),
    (r"D:\texdump\probe2\tex0_ch001_00_00_Cloth_Bottom_Mat_ALBD_tex.png", (96, 96, 160, 160)),
]
for name, box in jobs:
    img = Image.open(name)
    print(os.path.basename(name), img.size)
    z = img.crop(box)
    z = z.resize((z.width * 6, z.height * 6), Image.NEAREST)
    out = name.replace(".png", "_zoom.png")
    z.save(out)
    print("  ->", out)
