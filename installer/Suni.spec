from PyInstaller.utils.hooks import collect_all
datas, binaries, hiddenimports = [], [], []
for name in ('faster_whisper', 'piper', 'pyaudiowpatch', 'ctranslate2', 'transformers', 'sentencepiece'):
    d, b, h = collect_all(name)
    datas += d
    binaries += b
    hiddenimports += h
a = Analysis(['../run.py'], pathex=['.'], binaries=binaries, datas=datas, hiddenimports=hiddenimports, excludes=['PySide6.QtWebEngineCore','PySide6.QtWebEngineWidgets','PySide6.QtPdf','matplotlib','IPython','pytest'])
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='Suni', console=False)
coll = COLLECT(exe, a.binaries, a.datas, name='Suni')
