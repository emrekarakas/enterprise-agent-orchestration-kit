import hashlib
import math
import re


TOKEN = re.compile(r"[\w-]+", re.UNICODE)


def embed(text: str, dimensions: int) -> list[float]:
    vector = [0.0] * dimensions
    for token in TOKEN.findall(text.lower()):
        digest = hashlib.sha256(token.encode()).digest()
        index = int.from_bytes(digest[:4], "big") % dimensions
        vector[index] += 1.0 if digest[4] % 2 == 0 else -1.0
    norm = math.sqrt(sum(value * value for value in vector))
    return [value / norm for value in vector] if norm else vector
