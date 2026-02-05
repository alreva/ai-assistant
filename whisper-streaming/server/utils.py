# server/utils.py


def dedup_repeated_phrases(text: str, max_repeats: int = 3) -> str:
    """Remove repeated words/phrases caused by Whisper hallucination.

    Scans text for any phrase (1-3 words) repeated more than max_repeats times
    consecutively, and truncates at the first repetition.
    """
    words = text.split()
    if len(words) <= max_repeats:
        return text

    for start in range(len(words)):
        for phrase_len in range(1, min(4, (len(words) - start) // 2) + 1):
            phrase = words[start:start + phrase_len]
            count = 0
            i = start
            while i + phrase_len <= len(words):
                if words[i:i + phrase_len] == phrase:
                    count += 1
                    i += phrase_len
                else:
                    break
            if count > max_repeats:
                return " ".join(words[:start + phrase_len])
    return text


def samples_to_ms(samples: int, sample_rate: int) -> float:
    """Convert sample count to milliseconds."""
    return (samples / sample_rate) * 1000


def ms_to_samples(ms: float, sample_rate: int) -> int:
    """Convert milliseconds to sample count."""
    return int(sample_rate * ms / 1000)
