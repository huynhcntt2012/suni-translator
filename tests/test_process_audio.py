import io
import os
import psutil
import pytest
from engine.process_audio import read_exact, validate_process

def test_protocol_reader_rejects_truncated_packet():
    assert read_exact(io.BytesIO(b'abcd'),4) == b'abcd'
    with pytest.raises(EOFError):
        read_exact(io.BytesIO(b'ab'),4)

def test_recycled_pid_is_rejected():
    with pytest.raises(RuntimeError,match='PID'):
        validate_process(os.getpid(),psutil.Process().create_time()-100)

def test_own_audio_tree_is_rejected():
    with pytest.raises(RuntimeError,match='Suni'):
        validate_process(os.getpid(),psutil.Process().create_time())
