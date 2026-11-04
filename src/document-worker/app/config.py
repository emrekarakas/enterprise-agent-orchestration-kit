from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="DOCUMENT_WORKER_", case_sensitive=False)

    vector_store: str = "memory"
    qdrant_url: str = "http://localhost:6333"
    qdrant_collection: str = "enterprise_documents"
    data_dir: Path = Path("data/document-worker")
    store_originals: bool = False
    max_upload_bytes: int = 25 * 1024 * 1024
    embedding_dimensions: int = 256
    chunk_words: int = 180
    chunk_overlap_words: int = 30
