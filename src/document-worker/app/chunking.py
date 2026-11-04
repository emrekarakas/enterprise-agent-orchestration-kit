import hashlib

from app.models import Chunk, ExtractedSection


def chunk_sections(sections: list[ExtractedSection], max_words: int, overlap_words: int) -> list[Chunk]:
    if max_words <= 0 or overlap_words < 0 or overlap_words >= max_words:
        raise ValueError("Invalid chunking configuration.")
    chunks: list[Chunk] = []
    step = max_words - overlap_words
    for section_index, section in enumerate(sections):
        words = section.text.split()
        for offset in range(0, len(words), step):
            content = " ".join(words[offset : offset + max_words]).strip()
            if not content:
                continue
            digest = hashlib.sha256(f"{section_index}:{offset}:{content}".encode()).hexdigest()[:24]
            chunks.append(Chunk(digest, content, section.locator))
            if offset + max_words >= len(words):
                break
    return chunks
