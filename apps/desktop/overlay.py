from PySide6.QtCore import Qt, QTimer
from PySide6.QtWidgets import QWidget, QVBoxLayout, QLabel

class Overlay(QWidget):
    def __init__(self, settings):
        super().__init__()
        self.settings = settings
        self.setWindowFlags(Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool | Qt.WindowType.WindowTransparentForInput)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.resize(900, 180)
        layout = QVBoxLayout(self)
        self.original = QLabel()
        self.translation = QLabel()
        for label in (self.original, self.translation):
            label.setWordWrap(True)
            label.setAlignment(Qt.AlignmentFlag.AlignCenter)
            label.setTextFormat(Qt.TextFormat.PlainText)
            layout.addWidget(label)
        self.timer = QTimer(self)
        self.timer.setSingleShot(True)
        self.timer.timeout.connect(self.clear)
        self.drag = None
        self.configure(settings)

    def configure(self, settings):
        self.settings = settings
        self.translation.setStyleSheet(f"background:rgba(10,16,28,225);color:#ffffff;border-radius:14px;padding:14px;font-size:{settings.font_size}px;font-weight:600;")
        self.original.setStyleSheet(f"background:rgba(10,16,28,210);color:#b6c9e8;border-radius:10px;padding:7px;font-size:{max(14,settings.font_size-8)}px;")
        self.original.setVisible(settings.bilingual)
        if settings.overlay_x >= 0:
            self.move(settings.overlay_x, settings.overlay_y)
        else:
            screen = self.screen().availableGeometry()
            self.move(screen.x()+(screen.width()-self.width())//2, screen.bottom()-240)

    def display(self, segment):
        self.original.setText(segment.original)
        self.translation.setText(segment.translated)
        self.show()
        self.timer.start(self.settings.subtitle_seconds * 1000)

    def clear(self):
        self.hide()

    def edit_position(self, enabled):
        self.setWindowFlag(Qt.WindowType.WindowTransparentForInput, not enabled)
        if enabled:
            self.timer.stop()
            self.original.setText("Kéo để đổi vị trí • tắt chỉnh vị trí để khóa")
            self.translation.setText("Phụ đề dịch sẽ xuất hiện ở đây")
            self.show()
        else:
            self.settings.overlay_x, self.settings.overlay_y = self.x(), self.y()
            self.hide()

    def mousePressEvent(self, event):
        self.drag = event.globalPosition().toPoint() - self.pos()

    def mouseMoveEvent(self, event):
        if self.drag is not None:
            self.move(event.globalPosition().toPoint() - self.drag)

    def mouseReleaseEvent(self, event):
        self.drag = None
