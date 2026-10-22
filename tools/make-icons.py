# Thunderstore icons (256x256) from docs/brand/streplay-logo.png: a square crop centred on the title, plus a labelled
# version for the ffmpeg add-on. Run: python tools/make-icons.py
from PIL import Image, ImageDraw, ImageFont
import os
root = os.path.join(os.path.dirname(__file__), '..')
logo = Image.open(os.path.join(root, 'docs', 'brand', 'streplay-logo.png')).convert('RGB')
w, h = logo.size
side = min(w, h)
cx = int(w * 0.53)  # the title sits a little right of centre
left = max(0, min(w - side, cx - side // 2))
icon = logo.crop((left, 0, left + side, side)).resize((256, 256), Image.LANCZOS)
icon.save(os.path.join(root, 'thunderstore', 'icon.png'))

# the add-on: same picture, a dark slanted strip with its name, in the mod's lime
ff = icon.copy()
d = ImageDraw.Draw(ff, 'RGBA')
d.polygon([(0, 196), (256, 184), (256, 236), (0, 248)], fill=(9, 14, 11, 225))
d.polygon([(0, 196), (256, 184), (256, 188), (0, 200)], fill=(233, 255, 145, 255))
font = ImageFont.truetype('C:/Windows/Fonts/segoeuiz.ttf', 25)
text = 'FFMPEG ADD-ON'
tw = d.textlength(text, font=font)
d.text(((256 - tw) / 2, 199), text, font=font, fill=(233, 255, 145, 255))
ff.save(os.path.join(root, 'ffmpeg-package', 'icon.png'))
print('icons written')
