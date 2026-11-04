from dataclasses import dataclass
from datetime import datetime
from enum import StrEnum

from pydantic import BaseModel, Field


class IngestionState(StrEnum):
    PROCESSING = "Processing"
    COMPLETED = "Completed"
    SKIPPED = "Skipped"
    FAILED = "Failed"
    DELETED = "Deleted"


class IngestionResponse(BaseModel):
    ingestion_id: str
    document_id: str
    status: IngestionState
    file_name: str
    content_hash: str
    chunk_count: int = 0
    error_code: str | None = None
    error_message: str | None = None
    created_at: datetime
    completed_at: datetime | None = None


class SearchRequest(BaseModel):
    tenant_id: str = Field(min_length=1, max_length=100)
    query: str = Field(min_length=1, max_length=4000)
    access_tags: list[str] = Field(min_length=1, max_length=50)
    top: int = Field(default=5, ge=1, le=20)
    minimum_score: float = Field(default=0.1, ge=0, le=1)


class SearchHit(BaseModel):
    document_id: str
    chunk_id: str
    title: str
    source_uri: str
    locator: str | None
    content: str
    score: float
    tenant_id: str
    access_tags: list[str]


@dataclass(frozen=True)
class ExtractedSection:
    text: str
    locator: str | None


@dataclass(frozen=True)
class Chunk:
    chunk_id: str
    content: str
    locator: str | None


@dataclass(frozen=True)
class VectorPoint:
    point_id: str
    vector: list[float]
    payload: dict[str, object]


@dataclass(frozen=True)
class VectorMatch:
    score: float
    payload: dict[str, object]
