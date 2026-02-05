# tests/test_vad_base.py
import pytest
from client.vad.base import VADBackend


def test_vad_backend_is_abstract():
    with pytest.raises(TypeError):
        VADBackend()
