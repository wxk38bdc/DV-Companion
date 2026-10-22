from pathlib import Path
import concurrent.futures, hashlib, json, tarfile, urllib.request
import zstandard

ROOT=Path(__file__).resolve().parent
CACHE=ROOT/'packages'; CACHE.mkdir(exist_ok=True)
PREFIX=ROOT/'native-deps'; PREFIX.mkdir(exist_ok=True)
# Fetch pinned source archives and verify before extraction.
for item in json.loads((ROOT/'source-archives.json').read_text(encoding='utf-8')):
    archive=ROOT/item['file']
    if not archive.exists():
        partial=archive.with_suffix(archive.suffix+'.partial')
        try:
            with urllib.request.urlopen(item['url'],timeout=120) as response,partial.open('wb') as output:
                while chunk:=response.read(1024*1024):output.write(chunk)
            partial.replace(archive)
        finally:
            if partial.exists():partial.unlink()
    if hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()!=item['sha256']:
        raise ValueError('upstream source SHA256 mismatch: '+item['file'])

PACKAGES=[
 'mingw-w64-x86_64-boost-1.88.0-2-any.pkg.tar.zst',
 'mingw-w64-x86_64-opencv-4.11.0-5-any.pkg.tar.zst',
 'mingw-w64-x86_64-fmt-11.1.4-1-any.pkg.tar.zst',
 'mingw-w64-x86_64-eigen3-3.4.0-1-any.pkg.tar.zst',
 'mingw-w64-x86_64-flatbuffers-25.2.10-1-any.pkg.tar.zst',
 'mingw-w64-x86_64-libusb-1.0.30-1-any.pkg.tar.zst',
 'mingw-w64-x86_64-lz4-1.10.0-1-any.pkg.tar.zst',
 'mingw-w64-x86_64-zstd-1.5.7-1-any.pkg.tar.zst',
]
manifest_path=ROOT/'dependency-manifest.json'
expected={p['name']:p['sha256'] for p in json.loads(manifest_path.read_text(encoding='utf-8'))} if manifest_path.exists() else {}
def fetch(name):
    target=CACHE/name
    url='https://repo.msys2.org/mingw/mingw64/'+name
    if not target.exists():
        with urllib.request.urlopen(url,timeout=90) as response,target.open('wb') as f:
            while chunk:=response.read(1024*1024):f.write(chunk)
    print(json.dumps({'downloaded':name,'bytes':target.stat().st_size}),flush=True)
    digest=hashlib.file_digest(target.open('rb'),'sha256').hexdigest()
    if name in expected and digest!=expected[name]:raise ValueError('dependency SHA256 mismatch: '+name)
    return {'name':name,'url':url,'sha256':digest}
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    results=list(pool.map(fetch,PACKAGES))
for package in PACKAGES:
    with (CACHE/package).open('rb') as raw,zstandard.ZstdDecompressor().stream_reader(raw) as stream,tarfile.open(fileobj=stream,mode='r|') as tar:
        for member in tar:
            if not member.isfile():continue
            name=member.name.removeprefix('./')
            if not(name.startswith('mingw64/include/') or name.startswith('mingw64/lib/') or name.startswith('mingw64/share/licenses/')):continue
            destination=(PREFIX/name).resolve()
            if not destination.is_relative_to(PREFIX.resolve()):raise ValueError('unsafe archive path')
            destination.parent.mkdir(parents=True,exist_ok=True)
            with tar.extractfile(member) as src,destination.open('wb') as dst:
                while chunk:=src.read(1024*1024):dst.write(chunk)
    print(json.dumps({'extracted':package}),flush=True)
for archive,label in [('runtime-1.7.1.tar.gz','runtime-source'),('processing-2.0.2.tar.gz','processing-source')]:
    target=ROOT/label;target.mkdir(exist_ok=True)
    with tarfile.open(ROOT/archive,'r:gz') as tar:
        for member in tar:
            if not member.isfile():continue
            relative=Path(*Path(member.name).parts[1:]);destination=(target/relative).resolve()
            if not destination.is_relative_to(target.resolve()):raise ValueError('unsafe source archive path')
            destination.parent.mkdir(parents=True,exist_ok=True)
            with tar.extractfile(member) as src,destination.open('wb') as dst:dst.write(src.read())
(ROOT/'dependency-manifest.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print('READY',flush=True)
