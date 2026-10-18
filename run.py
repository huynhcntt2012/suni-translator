from apps.desktop.main import main

if __name__ == "__main__":
    import sys
    if '--self-test' in sys.argv:
        import json
        import traceback
        import numpy as np
        from engine.config import DATA, Settings
        from engine.providers import Speech, Translator
        from engine.audio import devices
        DATA.mkdir(parents=True, exist_ok=True)
        try:
            inputs, outputs = devices()
            speech = Speech(Settings(), lambda _: None)
            speech.transcribe(np.zeros(16000, dtype=np.float32))
            translated = Translator(Settings()).translate('Hello, welcome to our application.', 'en')
            from engine.process_audio import capability_error
            process_error = capability_error()
            if process_error:
                raise RuntimeError(process_error)
            from PySide6.QtWidgets import QApplication
            from storage.database import Database
            from apps.desktop.main import Window
            app = QApplication([])
            window = Window(Database(DATA / 'self-test.sqlite3'))
            window.show()
            app.processEvents()
            window.grab().save(str(DATA / 'self-test.png'))
            window.close()
            report = {'ok': True, 'inputs': len(inputs), 'outputs': len(outputs), 'translation': translated,'process_capture_bridge':True}
        except Exception:
            report = {'ok': False, 'error': traceback.format_exc()}
        (DATA / 'self-test.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        raise SystemExit(0 if report['ok'] else 1)
    raise SystemExit(main())
