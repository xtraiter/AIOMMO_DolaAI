"""Remove duration words from a prompt before it is typed into Dola's chat.

The Dola30 extension unlocks the 30 s option through the page protocol (see extensions/dola30/README.md): "avoid putting
duration words (e.g. '30s', '30 seconds') directly in the prompt text". When the text itself talks about 30 seconds, Dola's
chat agent compares it with the 4-15 s it natively supports and asks for confirmation instead of generating. The length is
chosen in the duration dropdown, so timestamps such as "(00:00 - 00:03)", "Giây 0 đến 3", "0-3s" or "30 seconds" only do harm.
"""
import re

_SEP = r"[-–—~]"                       # - – — ~
_UNIT = r"(?:s|sec|secs|second|seconds|giây|giay|秒|초)"

_PATTERNS = [
    # (00:00 - 00:03)  /  00:25 - 00:30  /  00:08
    re.compile(r"[\(（\[]?\s*\d{1,2}:\d{2}(?:\s*" + _SEP + r"\s*\d{1,2}:\d{2})?\s*[\)）\]]?"),
    # Giây 0 đến 3  /  Giây 3 - 8
    re.compile(r"\b(?:giây|giay|second|seconds|sec)\s*\d+(?:[.,]\d+)?\s*(?:đến|den|tới|toi|to|" + _SEP + r")\s*\d+(?:[.,]\d+)?",
               re.IGNORECASE),
    # 0-3s  /  3-8 giây  /  18–25 seconds
    re.compile(r"\b\d+(?:[.,]\d+)?\s*" + _SEP + r"\s*\d+(?:[.,]\d+)?\s*" + _UNIT + r"(?![A-Za-z])", re.IGNORECASE),
    # 30s  /  30 seconds  /  15 giây  /  30秒   (a number directly followed by a time unit)
    re.compile(r"(?<![A-Za-z0-9.:])\d+(?:[.,]\d+)?\s*" + _UNIT + r"(?![A-Za-z])", re.IGNORECASE),
]

_EMPTY_BRACKETS = re.compile(r"[\(（\[]\s*[\)）\]]")
_EDGE_SEPARATORS = re.compile(r"^[\s\-–—:|,;]+|[\s\-–—:|,;]+$")


def strip_duration_words(text: str) -> tuple[str, int]:
    """Returns (cleaned text, number of places removed). Only the lines that contained such words are touched;
    all line breaks, bullets and everything else are kept."""
    total = 0
    out = []
    for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        new, n = line, 0
        for pattern in _PATTERNS:
            new, k = pattern.subn("", new)
            n += k
        if n:
            total += n
            new = _EMPTY_BRACKETS.sub("", new)
            new = re.sub(r"[ \t]{2,}", " ", new)
            new = re.sub(r"\s+([:,;.?!])", r"\1", new)            # no dangling space before punctuation
            bullet = re.match(r"^\s*([•*\-]\s+)", line)        # keep a leading "• " / "- " bullet of the original line
            new = _EDGE_SEPARATORS.sub("", new).strip()
            if bullet and new:
                new = bullet.group(1) + new
        out.append(new)
    if not total:
        return text, 0
    cleaned = re.sub(r"\n{3,}", "\n\n", "\n".join(out)).strip()
    return cleaned, total
