import os
os.environ["QT_QPA_PLATFORM"] = "offscreen"
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from PySide6.QtWidgets import QApplication
from apps.desktop.main import Window
from storage.database import Database
from engine.pipeline import Segment

folder = Path("tmp/verification")
folder.mkdir(parents=True, exist_ok=True)
app = QApplication([])
from PySide6.QtGui import QFontDatabase
QFontDatabase.addApplicationFont('C:/Windows/Fonts/segoeui.ttf')
QFontDatabase.addApplicationFont('C:/Windows/Fonts/segoeuib.ttf')
db = Database(folder / "ui.sqlite3")
window = Window(db)
window.show()
app.processEvents()
window.grab().save(str(folder / "dashboard.png"))
for index, name in [(1,"settings"),(2,"models"),(3,"history")]:
    window.tabs.setCurrentIndex(index)
    app.processEvents()
    window.grab().save(str(folder / f"{name}.png"))
window.overlay.display(Segment(0, 2, "en", "Welcome to Suni Live Translator.", "Chào mừng bạn đến với Suni Live Translator.", 1))
app.processEvents()
window.overlay.grab().save(str(folder / "overlay.png"))
window.close()
print("UI construction, tab rendering, overlay and graceful close OK")
