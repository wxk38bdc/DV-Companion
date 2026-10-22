"""Original vector artwork and matching multi-resolution Windows icon."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import math

root = Path(__file__).resolve().parent
assets = root / 'app' / 'Assets'
assets.mkdir(exist_ok=True)
svg = '''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>DV Companion — event lens</title>
  <desc>An open cyan optical ring with a bright focal point and three event pixels on a midnight blue rounded tile.</desc>
  <defs><linearGradient id="tile" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#193957"/><stop offset="1" stop-color="#0A172C"/></linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="54" fill="url(#tile)"/>
  <rect x="8.75" y="8.75" width="238.5" height="238.5" rx="53.25" fill="none" stroke="#FFFFFF" stroke-opacity="0.12" stroke-width="1.5"/>
  <path d="M 168,140 A 56,56 0 1 1 112,84" fill="none" stroke="#4DE1D3" stroke-width="21" stroke-linecap="round"/>
  <circle cx="112" cy="140" r="18" fill="#F3FCFF"/>
  <rect x="150" y="88" width="24" height="24" rx="6" fill="#4DE1D3"/>
  <rect x="183" y="52" width="25" height="25" rx="6" fill="#FFAD66"/>
  <rect x="199" y="102" width="15" height="15" rx="4" fill="#4DE1D3" opacity="0.65"/>
</svg>
'''
(assets / 'dv-companion.svg').write_text(svg, encoding='utf-8')

scale = 4
side = 256 * scale
im = Image.new('RGBA', (side, side))
mask = Image.new('L', im.size)
md = ImageDraw.Draw(mask)
box = lambda xy: tuple(round(v * scale) for v in xy)
md.rounded_rectangle(box((8, 8, 248, 248)), radius=54*scale, fill=255)
grad = Image.new('RGBA', im.size)
gd = ImageDraw.Draw(grad)
top, bottom = (25,57,87), (10,23,44)
for y in range(side):
    for x in range(side):
        t = min(1, max(0, (x + y - 16*scale) / (480*scale)))
        grad.putpixel((x,y), tuple(round(a+(b-a)*t) for a,b in zip(top,bottom))+(255,))
im.paste(grad, (0,0), mask)
d = ImageDraw.Draw(im)
d.rounded_rectangle(box((8.75,8.75,247.25,247.25)), radius=53.25*scale, outline=(68,87,108,255), width=round(1.5*scale))
cyan = '#4DE1D3'
# Pillow draws arcs inside their bounds; align the centerline with the SVG.
d = ImageDraw.Draw(im)
d.arc(box((45.5,73.5,178.5,206.5)), start=0, end=270, fill=cyan, width=21*scale)
for x,y in [(168,140),(112,84)]:
    d.ellipse(box((x-10.5,y-10.5,x+10.5,y+10.5)), fill=cyan)
d.ellipse(box((94,122,130,158)), fill='#F3FCFF')
for rect,r,color in [((150,88,174,112),6,cyan),((183,52,208,77),6,'#FFAD66'),((199,102,214,117),4,'#369E9E')]:
    d.rounded_rectangle(box(rect), radius=r*scale, fill=color)

im.save(assets/'dv-companion-master.png')
im.resize((256,256),Image.Resampling.LANCZOS).save(assets/'dv-companion.png')
im.save(assets/'dv-companion.ico', sizes=[(s,s) for s in [16,20,24,32,40,48,64,128,256]], bitmap_format='png')

# Inspect the same mark at Windows' common display sizes, on light/dark backgrounds.
sheet=Image.new('RGB',(940,400),'#F0F4F8')
sd=ImageDraw.Draw(sheet)
sd.rectangle((470,0,940,400),fill='#111A28')
font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',20)
small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',15)
for offset,fg in [(0,'#193049'),(470,'#E5F0FB')]:
    sd.text((offset+24,18),'DV Companion',font=font,fill=fg)
    main=im.resize((224,224),Image.Resampling.LANCZOS)
    sheet.paste(main,(offset+24,68),main)
    for s,y in [(64,70),(48,155),(32,223),(24,275),(16,318)]:
        thumb=im.resize((s,s),Image.Resampling.LANCZOS)
        sheet.paste(thumb,(offset+290,y),thumb)
        sd.text((offset+370,y+(s-20)//2),f'{s}px',font=small,fill=fg)
    sd.text((offset+24,334),'Event lens / 2026',font=small,fill=fg)
sheet.save(root/'icon-preview.png')
print('Created SVG, PNG and 9-resolution ICO:', assets)
