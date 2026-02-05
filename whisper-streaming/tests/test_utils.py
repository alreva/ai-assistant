# tests/test_utils.py
from server.utils import dedup_repeated_phrases, samples_to_ms, ms_to_samples


def test_dedup_no_repetition():
    assert dedup_repeated_phrases("hello world") == "hello world"


def test_dedup_short_text():
    assert dedup_repeated_phrases("hi hi hi") == "hi hi hi"


def test_dedup_single_word_repetition():
    text = "hello hello hello hello hello"
    result = dedup_repeated_phrases(text, max_repeats=3)
    assert result == "hello"


def test_dedup_phrase_repetition():
    text = "the cat the cat the cat the cat the cat"
    result = dedup_repeated_phrases(text, max_repeats=3)
    assert result == "the cat"


def test_dedup_preserves_non_repeated_prefix():
    text = "I said hello hello hello hello hello"
    result = dedup_repeated_phrases(text, max_repeats=3)
    assert result == "I said hello"


def test_samples_to_ms():
    assert samples_to_ms(16000, 16000) == 1000.0
    assert samples_to_ms(8000, 16000) == 500.0
    assert samples_to_ms(480, 16000) == 30.0


def test_ms_to_samples():
    assert ms_to_samples(1000, 16000) == 16000
    assert ms_to_samples(500, 16000) == 8000
    assert ms_to_samples(30, 16000) == 480
