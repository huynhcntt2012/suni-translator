from PyInstaller.utils.hooks import collect_all
datas, binaries, hiddenimports = [], [], []
datas.append(('../native/windows_audio/SuniProcessLoopback.exe', 'native/windows_audio'))
for name in ('faster_whisper', 'piper', 'pyaudiowpatch', 'ctranslate2', 'transformers', 'sentencepiece'):
    d, b, h = collect_all(name)
    datas += d
    binaries += b
    hiddenimports += h
a = Analysis(['../run.py'], pathex=['.'], binaries=binaries, datas=datas, hiddenimports=hiddenimports, excludes=['PySide6.QtWebEngineCore','PySide6.QtWebEngineWidgets','PySide6.QtPdf','matplotlib','IPython','pytest'])
# Qt 6.12 uses the ICU ABI supplied by Windows 11. A development PATH can
# contain Poppler's ICU with version-suffixed exports under the same DLL name.
# Shipping that DLL breaks QtCore before the application can start.
a.binaries = [entry for entry in a.binaries if not (entry[0].lower() == 'icuuc.dll' and 'poppler' in entry[1].lower())]
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='Suni', console=False)
coll = COLLECT(exe, a.binaries, a.datas, name='Suni')
