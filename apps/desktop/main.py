import csv
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import sys
import threading
import time
from collections import deque
from PySide6.QtCore import Qt, QObject, Signal, QTimer
from PySide6.QtGui import QColor, QPainter, QPen, QShortcut, QKeySequence, QIcon, QPixmap
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QComboBox, QFormLayout, QTabWidget, QCheckBox, QSpinBox, QDoubleSpinBox, QLineEdit, QFileDialog, QTableWidget, QTableWidgetItem, QHeaderView, QSystemTrayIcon, QMenu, QMessageBox, QGroupBox)
from engine.config import DATA, Settings, LANGUAGES
from engine.audio import devices
from engine.pipeline import Pipeline
from engine.providers import model_path, download_model, model_ready
from engine.export import to_srt
from engine.process_audio import applications, capability_error
from storage.database import Database
from apps.desktop.overlay import Overlay

class Bridge(QObject):
    status = Signal(str)
    meter = Signal(float)
    segment = Signal(object)
    finished = Signal()
    downloaded = Signal()

class Waveform(QWidget):
    def __init__(self):
        super().__init__()
        self.values = deque([0.0]*100, maxlen=100)
        self.setMinimumHeight(95)

    def push(self, value):
        self.values.append(min(1, value*5))
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(QPen(QColor("#53dbc3"), 3))
        width, height = self.width(), self.height()
        for i, value in enumerate(self.values):
            x = int(i*width/100)
            amplitude = max(1, int(value*(height/2-8)))
            painter.drawLine(x, height//2-amplitude, x, height//2+amplitude)

STYLE = """
QMainWindow,QWidget {background:#101722;color:#e6edf7;font-family:'Segoe UI';font-size:13px;}
QLabel#title {font-size:30px;font-weight:700;} QLabel#muted {color:#8fa2be;}
QGroupBox {border:1px solid #2b394d;border-radius:12px;margin-top:18px;padding:18px 12px 12px;}
QGroupBox::title {subcontrol-origin:margin;left:12px;color:#9db4d4;}
QPushButton {background:#26364d;border:0;border-radius:8px;padding:10px 16px;}
QPushButton:hover {background:#344b68;} QPushButton:disabled {color:#64748b;background:#1c2636;}
QPushButton#start {background:#51dcc0;color:#082e29;font-weight:700;font-size:16px;}
QComboBox,QLineEdit,QSpinBox,QDoubleSpinBox {background:#1a2638;border:1px solid #34445d;border-radius:6px;padding:7px;}
QTabWidget::pane {border:0;} QTabBar::tab {padding:12px 22px;background:#101722;color:#98abc6;}
QTabBar::tab:selected {color:#55e0c4;border-bottom:2px solid #55e0c4;}
QTableWidget {background:#131e2d;gridline-color:#29384c;border:0;alternate-background-color:#182437;}
QHeaderView::section {background:#223149;color:#a8bdd9;padding:9px;border:0;}
QCheckBox {spacing:8px;} QToolTip {background:#24364d;color:white;}
"""

def combo(items, value):
    widget = QComboBox()
    for label, data in items:
        widget.addItem(label, data)
    index = widget.findData(value)
    widget.setCurrentIndex(max(0, index))
    return widget

class Window(QMainWindow):
    def __init__(self, db):
        super().__init__()
        self.db, self.settings = db, db.settings()
        self.pipeline = None
        self.downloading = False
        self.closing = False
        self.bridge = Bridge()
        self.bridge.status.connect(self.set_status)
        self.bridge.segment.connect(self.add_segment)
        self.bridge.finished.connect(self.stopped)
        self.bridge.downloaded.connect(self.download_finished)
        self.setWindowTitle("Suni • Live Translator")
        self.resize(1120, 820)
        self.overlay = Overlay(self.settings)
        body = QWidget()
        root = QVBoxLayout(body)
        root.setContentsMargins(28, 22, 28, 20)
        title = QLabel("suni  /  Live Translator")
        title.setObjectName("title")
        root.addWidget(title)
        subtitle = QLabel("Nghe thế giới. Hiểu bằng ngôn ngữ của bạn.     •     OFFLINE FIRST")
        subtitle.setObjectName("muted")
        root.addWidget(subtitle)
        self.tabs = QTabWidget()
        root.addWidget(self.tabs)
        self.build_live()
        self.build_settings()
        self.build_models()
        self.build_history()
        self.status = QLabel("Sẵn sàng • Tải model trước khi bắt đầu")
        self.status.setWordWrap(True)
        root.addWidget(self.status)
        self.setCentralWidget(body)
        self.setStyleSheet(STYLE)
        self.bridge.meter.connect(self.wave.push)
        self.shortcut = QShortcut(QKeySequence("Ctrl+Shift+S"), self)
        self.shortcut.activated.connect(self.toggle)
        self.overlay_shortcut = QShortcut(QKeySequence("Ctrl+Shift+O"), self)
        self.overlay_shortcut.activated.connect(lambda: self.overlay.setVisible(not self.overlay.isVisible()))
        pixmap = QPixmap(32, 32)
        pixmap.fill(QColor("#51dcc0"))
        self.setWindowIcon(QIcon(pixmap))
        self.tray = QSystemTrayIcon(self.windowIcon(), self)
        menu = QMenu()
        menu.addAction("Mở Suni", self.showNormal)
        menu.addAction("Start / Stop", self.toggle)
        menu.addAction("Thoát", self.close)
        self.tray.setContextMenu(menu)
        self.tray.setToolTip("Suni Live Translator")
        self.tray.activated.connect(lambda *_: self.showNormal())
        self.tray.show()
        self.refresh_devices()
        self.load_sessions()

    def build_live(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        self.controls = QGroupBox("01   NGUỒN ÂM THANH & NGÔN NGỮ")
        form = QFormLayout(self.controls)
        self.capture_kind = combo([('Toàn bộ thiết bị • System Audio','system'),('Ứng dụng riêng • gồm tiến trình con','process')],self.settings.capture_kind)
        self.capture_kind.currentIndexChanged.connect(self.source_changed)
        form.addRow('Phạm vi thu',self.capture_kind)
        self.capture = QComboBox()
        self.refresh = QPushButton("Làm mới thiết bị")
        self.refresh.clicked.connect(self.refresh_devices)
        row = QHBoxLayout()
        row.addWidget(self.capture, 1)
        row.addWidget(self.refresh)
        form.addRow("System Audio", row)
        self.processes = QComboBox()
        self.processes.setMinimumContentsLength(30)
        form.addRow('Ứng dụng / PID',self.processes)
        self.source = combo([("Tự nhận diện", "auto")]+[(v,k) for k,v in LANGUAGES.items()], self.settings.source)
        self.target = combo([(v,k) for k,v in LANGUAGES.items()], self.settings.target)
        languages = QHBoxLayout()
        languages.addWidget(self.source)
        languages.addWidget(QLabel("→"))
        languages.addWidget(self.target)
        form.addRow("Ngôn ngữ", languages)
        self.mode = combo([("A • Live Subtitles", "subtitles"), ("B • Voice Overlay — thiết bị phát riêng", "voice"), ("C • Voice Replacement — chưa khả dụng", "replacement")], self.settings.mode)
        self.mode.model().item(2).setEnabled(False)
        if self.settings.mode == "replacement":
            self.mode.setCurrentIndex(0)
        form.addRow("Chế độ", self.mode)
        self.source_note = QLabel()
        self.source_note.setWordWrap(True)
        self.source_note.setObjectName("muted")
        form.addRow(self.source_note)
        layout.addWidget(self.controls)
        self.wave = Waveform()
        layout.addWidget(self.wave)
        self.start_button = QPushButton("Bắt đầu phiên dịch")
        self.start_button.setObjectName("start")
        self.start_button.clicked.connect(self.toggle)
        layout.addWidget(self.start_button)
        self.last_translation = QLabel("Bản dịch sẽ xuất hiện tại đây")
        self.last_translation.setWordWrap(True)
        self.last_translation.setTextFormat(Qt.TextFormat.PlainText)
        self.last_translation.setStyleSheet("font-size:23px;padding:18px;background:#1a283c;border-radius:12px;")
        layout.addWidget(self.last_translation)
        self.metrics = QLabel("Độ trễ: —   |   Ngôn ngữ: —   |   Ctrl+Shift+S: Start/Stop trong cửa sổ")
        self.metrics.setObjectName("muted")
        layout.addWidget(self.metrics)
        layout.addStretch()
        self.tabs.addTab(page, "Phiên dịch")

    def build_settings(self):
        self.settings_page = QWidget()
        form = QFormLayout(self.settings_page)
        self.compute = combo([("CPU • int8", "cpu"), ("NVIDIA CUDA • tự fallback CPU khi nạp lỗi", "cuda")], self.settings.compute)
        self.whisper = combo([(x, x) for x in ("tiny", "base", "small", "medium")], self.settings.whisper)
        self.provider = combo([("NLLB • Offline", "nllb"), ("Ollama • HTTP đã cấu hình", "ollama")], self.settings.translation)
        self.ollama_url = QLineEdit(self.settings.ollama_url)
        self.ollama_model = QLineEdit(self.settings.ollama_model)
        self.output = QComboBox()
        self.voice_path = QLineEdit(self.settings.voice)
        voice_row = QHBoxLayout()
        voice_row.addWidget(self.voice_path)
        browse = QPushButton("Chọn .onnx")
        browse.clicked.connect(self.browse_voice)
        voice_row.addWidget(browse)
        self.duck = QSpinBox()
        self.duck.setRange(0, 100)
        self.duck.setValue(round(self.settings.duck*100))
        self.duck.setSuffix("% âm gốc khi AI đọc")
        self.font = QSpinBox()
        self.font.setRange(16, 60)
        self.font.setValue(self.settings.font_size)
        self.bilingual = QCheckBox("Hiển thị cả câu gốc và bản dịch")
        self.bilingual.setChecked(self.settings.bilingual)
        self.position = QCheckBox("Mở khóa phụ đề để kéo đến vị trí khác")
        self.position.toggled.connect(self.overlay.edit_position)
        self.chunk = QDoubleSpinBox()
        self.chunk.setRange(1, 10)
        self.chunk.setSingleStep(0.5)
        self.chunk.setValue(self.settings.chunk_seconds)
        self.chunk.setSuffix(" giây")
        for label, widget in [("Xử lý STT", self.compute), ("Whisper", self.whisper), ("Bộ dịch", self.provider), ("Ollama URL", self.ollama_url), ("Ollama model", self.ollama_model), ("Thiết bị phát AI", self.output), ("Giọng Piper", voice_row), ("Âm lượng gốc", self.duck), ("Cỡ phụ đề", self.font), ("Phân đoạn audio", self.chunk), ("Song ngữ", self.bilingual), ("Vị trí", self.position)]:
            form.addRow(label, widget)
        save = QPushButton("Lưu cấu hình")
        save.clicked.connect(self.save)
        form.addRow(save)
        note = QLabel("Voice Overlay cần hai thiết bị audio riêng, không nối đầu ra AI trở lại nguồn thu.\nGiọng phải khớp ngôn ngữ đích. NLLB chạy CPU để tiết kiệm VRAM.\nOllama URL là nơi văn bản nhận diện được gửi tới khi chọn provider này.")
        note.setWordWrap(True)
        form.addRow(note)
        self.tabs.addTab(self.settings_page, "Cấu hình")

    def build_models(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        label = QLabel("Tải model một lần khi có mạng; phiên dịch NLLB sau đó chạy cục bộ.\nNLLB 600M cần khoảng vài GB dung lượng. Kiểm tra giấy phép model trước khi phân phối thương mại.")
        label.setWordWrap(True)
        layout.addWidget(label)
        self.model_selector = combo([(x, x) for x in ("tiny", "base", "small", "medium", "nllb")], "small")
        layout.addWidget(self.model_selector)
        self.download_button = QPushButton("Tải model đã chọn")
        self.download_button.clicked.connect(self.download)
        layout.addWidget(self.download_button)
        self.model_info = QLabel()
        self.model_info.setWordWrap(True)
        layout.addWidget(self.model_info)
        self.refresh_models()
        folder = QPushButton("Mở thư mục dữ liệu / model / log")
        folder.clicked.connect(self.open_data)
        layout.addWidget(folder)
        layout.addWidget(QLabel("Giọng Piper: cung cấp .onnx + .onnx.json phù hợp, chọn ở Cấu hình.\nKhông có giọng tiếng Việt được đóng gói hoặc chứng nhận trong bản này."))
        layout.addStretch()
        self.tabs.addTab(page, "Model AI")

    def build_history(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        self.sessions = QComboBox()
        self.sessions.currentIndexChanged.connect(self.load_history)
        layout.addWidget(self.sessions)
        self.table = QTableWidget(0, 5)
        self.table.setHorizontalHeaderLabels(["Thời gian", "Ngôn ngữ", "Câu gốc", "Bản dịch", "Độ trễ"])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table.setAlternatingRowColors(True)
        layout.addWidget(self.table)
        row = QHBoxLayout()
        for name, format in [("Xuất SRT", "srt"), ("Xuất CSV", "csv")]:
            button = QPushButton(name)
            button.clicked.connect(lambda checked=False, f=format: self.export(f))
            row.addWidget(button)
        refresh = QPushButton("Làm mới lịch sử")
        refresh.clicked.connect(self.load_sessions)
        row.addWidget(refresh)
        layout.addLayout(row)
        self.tabs.addTab(page, "Lịch sử")

    def open_data(self):
        import os
        os.startfile(str(DATA))

    def browse_voice(self):
        path, _ = QFileDialog.getOpenFileName(self, "Chọn model Piper", "", "Piper (*.onnx)")
        if path:
            self.voice_path.setText(path)

    def refresh_devices(self):
        try:
            inputs, outputs = devices()
            for widget, found, preferred in [(self.capture, inputs, self.settings.capture_name), (self.output, outputs, self.settings.output_name)]:
                current = widget.currentText() or preferred
                widget.clear()
                for d in found:
                    widget.addItem(d["name"])
                index = widget.findText(current)
                if index >= 0:
                    widget.setCurrentIndex(index)
            if not inputs:
                self.set_status("Không có thiết bị WASAPI loopback. Kết nối loa/tai nghe rồi làm mới.")
        except Exception as exc:
            self.set_status(f"Không đọc được thiết bị audio: {exc}")
        selected = self.processes.currentData() or {'pid':self.settings.process_pid,'create_time':self.settings.process_created}
        self.processes.clear()
        self.processes.addItem('Chọn ứng dụng đang chạy…',None)
        try:
            for process in applications():
                self.processes.addItem(f"{process['name']}   •   PID {process['pid']} (+ tiến trình con)",process)
                if process['pid'] == selected['pid'] and abs(process['create_time']-selected['create_time']) < 0.01:
                    self.processes.setCurrentIndex(self.processes.count()-1)
        except Exception as exc:
            self.set_status(f'Không đọc được tiến trình: {exc}')
        error = capability_error()
        self.capture_kind.model().item(1).setEnabled(not bool(error))
        self.capture_kind.setToolTip(error)
        self.source_changed()

    def source_changed(self):
        if not hasattr(self,'processes'):
            return
        process = self.capture_kind.currentData() == 'process'
        self.processes.setEnabled(process)
        self.capture.setEnabled(not process)
        self.source_note.setText('Chọn tiến trình gốc của Chrome/Zoom/Discord. Thu cả tiến trình con, trên mọi thiết bị phát. Cần làm mới và chọn lại sau khi ứng dụng khởi động lại. Hiện hỗ trợ Live Subtitles.' if process else 'Thu toàn bộ âm thanh của thiết bị được chọn. Chuyển phạm vi thu để chỉ nghe một ứng dụng.')

    def save(self):
        s = self.settings
        s.capture_kind = self.capture_kind.currentData()
        selected = self.processes.currentData()
        s.process_pid = selected['pid'] if selected else 0
        s.process_created = selected['create_time'] if selected else 0
        for name, widget in [("source", self.source), ("target", self.target), ("mode", self.mode), ("compute", self.compute), ("whisper", self.whisper), ("translation", self.provider)]:
            setattr(s, name, widget.currentData())
        s.capture_name, s.output_name = self.capture.currentText(), self.output.currentText()
        s.voice = self.voice_path.text().strip()
        s.ollama_url, s.ollama_model = self.ollama_url.text().strip(), self.ollama_model.text().strip()
        s.font_size, s.bilingual = self.font.value(), self.bilingual.isChecked()
        s.chunk_seconds, s.duck = self.chunk.value(), self.duck.value()/100
        if self.position.isChecked():
            self.position.setChecked(False)
        self.db.save_settings(s)
        self.overlay.configure(s)
        self.set_status("Đã lưu cấu hình")

    def toggle(self):
        if self.pipeline:
            self.pipeline.stop()
            self.start_button.setEnabled(False)
            self.set_status("Đang dừng; chờ tác vụ AI hiện tại kết thúc…")
            return
        self.save()
        if self.settings.capture_kind == 'process' and not self.settings.process_pid:
            self.set_status('Chọn ứng dụng / PID trước khi bắt đầu.')
            return
        if self.settings.capture_kind == 'system' and not self.settings.capture_name:
            self.set_status("Chưa có thiết bị thu âm.")
            return
        self.controls.setEnabled(False)
        self.settings_page.setEnabled(False)
        self.start_button.setText("Dừng phiên dịch")
        self.pipeline = Pipeline(Settings.from_dict(self.settings.dict()), self.db, self.bridge.segment.emit, self.bridge.status.emit, self.bridge.meter.emit, self.bridge.finished.emit)
        self.pipeline.start()

    def stopped(self):
        self.pipeline = None
        self.controls.setEnabled(True)
        self.settings_page.setEnabled(True)
        self.start_button.setEnabled(True)
        self.start_button.setText("Bắt đầu phiên dịch")
        self.overlay.hide()
        if not self.status.text().startswith("Lỗi:"):
            self.set_status("Đã dừng phiên dịch")
        self.load_sessions()
        if self.closing:
            self.close()

    def set_status(self, text):
        self.status.setText(text)
        logging.info(text)

    def add_segment(self, segment):
        self.last_translation.setText(segment.translated)
        self.metrics.setText(f"Độ trễ: {segment.latency:.1f}s   |   Ngôn ngữ: {segment.language}   |   Mốc: {segment.start:.1f}–{segment.end:.1f}s")
        self.overlay.display(segment)

    def refresh_models(self):
        self.model_info.setText("\n".join(f"{'Đã tải' if model_ready(x) else 'Chưa tải'}  {x}   —   {model_path(x)}" for x in ("tiny", "base", "small", "medium", "nllb")))

    def download(self):
        if self.downloading:
            return
        name = self.model_selector.currentData()
        self.downloading = True
        self.download_button.setEnabled(False)
        def worker():
            try:
                download_model(name, self.bridge.status.emit)
            except Exception as exc:
                self.bridge.status.emit(f"Tải model thất bại: {exc}")
            finally:
                self.bridge.downloaded.emit()
        threading.Thread(target=worker, name="suni-download", daemon=True).start()

    def download_finished(self):
        self.downloading = False
        self.download_button.setEnabled(True)
        self.refresh_models()

    def load_sessions(self):
        selected = self.sessions.currentData()
        self.sessions.blockSignals(True)
        self.sessions.clear()
        for id, started, target, mode in self.db.sessions():
            self.sessions.addItem(f"#{id}  •  {started} UTC  •  {target}  •  {mode}", id)
        index = self.sessions.findData(selected)
        if index >= 0:
            self.sessions.setCurrentIndex(index)
        self.sessions.blockSignals(False)
        self.load_history()

    def load_history(self):
        rows = self.db.history(self.sessions.currentData()) if self.sessions.count() else []
        self.table.setRowCount(len(rows))
        for i, (start, end, language, original, translated, latency) in enumerate(rows):
            for j, value in enumerate([f"{start:.1f}–{end:.1f}s", language, original, translated, f"{latency:.1f}s"]):
                self.table.setItem(i, j, QTableWidgetItem(value))
        self.table.resizeRowsToContents()

    def export(self, format):
        if not self.sessions.count():
            self.set_status("Chưa có phiên dịch để xuất.")
            return
        path, _ = QFileDialog.getSaveFileName(self, "Xuất lịch sử", f"suni-session-{self.sessions.currentData()}.{format}", f"{format.upper()} (*.{format})")
        if not path:
            return
        rows = self.db.history(self.sessions.currentData())
        try:
            if format == "srt":
                Path(path).write_text(to_srt(rows, self.settings.bilingual), encoding="utf-8")
            else:
                with open(path, "w", encoding="utf-8-sig", newline="") as output:
                    writer = csv.writer(output)
                    writer.writerow(["start", "end", "language", "original", "translated", "latency"])
                    # Avoid spreadsheet formula evaluation of untrusted speech.
                    writer.writerows([["'"+v if isinstance(v,str) and v.startswith(("=", "+", "-", "@")) else v for v in row] for row in rows])
            self.set_status(f"Đã xuất {path}")
        except OSError as exc:
            self.set_status(f"Không thể xuất file: {exc}")

    def closeEvent(self, event):
        if self.pipeline:
            self.closing = True
            self.pipeline.stop()
            self.set_status("Đang dừng an toàn trước khi thoát…")
            event.ignore()
            return
        self.save()
        self.overlay.close()
        self.tray.hide()
        self.db.close()
        event.accept()

def main():
    DATA.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(level=logging.INFO, handlers=[RotatingFileHandler(DATA / "suni.log", maxBytes=5_000_000, backupCount=3, encoding="utf-8")], format="%(asctime)s %(levelname)s %(name)s %(message)s")
    app = QApplication(sys.argv)
    app.setApplicationName("Suni Live Translator")
    # Only one process may own the volume recovery journal and audio pipeline.
    from PySide6.QtCore import QLockFile
    lock = QLockFile(str(DATA / "app.lock"))
    lock.setStaleLockTime(0)
    if not lock.tryLock(100):
        QMessageBox.information(None, "Suni", "Suni đang chạy. Mở từ system tray.")
        return 0
    db = Database()
    window = Window(db)
    try:
        from engine.ducking import recover
        recover()
    except Exception as exc:
        window.set_status(f"Khôi phục âm lượng: {exc}")
    window.show()
    return app.exec()

if __name__ == "__main__":
    sys.exit(main())
