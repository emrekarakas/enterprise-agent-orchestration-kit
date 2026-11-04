from pathlib import Path

import pytest
from fastapi import HTTPException, UploadFile
from starlette.datastructures import Headers

from app.chunking import chunk_sections
from app.config import Settings
from app.models import ExtractedSection, IngestionState, SearchRequest
from app.repository import IngestionRepository
from app.security import parse_tags, validate_context_identifier, validate_upload
from app.service import IngestionService
from app.vector_store import InMemoryVectorStore


def test_rejects_path_traversal_filename() -> None:
    file = UploadFile(
        filename="../policy.txt", file=None,
        headers=Headers({"content-type": "text/plain"}),
    )
    with pytest.raises(HTTPException) as error:
        validate_upload(file, b"safe content", 1000)
    assert error.value.status_code == 400


def test_chunking_is_deterministic_and_overlaps() -> None:
    sections = [ExtractedSection("one two three four five six", "page 1")]
    first = chunk_sections(sections, 4, 1)
    second = chunk_sections(sections, 4, 1)
    assert first == second
    assert [chunk.content for chunk in first] == ["one two three four", "four five six"]


@pytest.mark.asyncio
async def test_ingestion_is_incremental_and_search_is_tenant_isolated(
    tmp_path: Path,
) -> None:
    settings = Settings(data_dir=tmp_path, chunk_words=20, chunk_overlap_words=2)
    repository = IngestionRepository(tmp_path / "worker.db")
    vectors = InMemoryVectorStore()
    service = IngestionService(settings, repository, vectors)

    first = await service.ingest(
        "tenant-a", "user-a", "policy.txt", ".txt",
        b"International travel daily limit is 85 units.", ["employee"],
        "upload://policy.txt",
    )
    duplicate = await service.ingest(
        "tenant-a", "user-a", "policy.txt", ".txt",
        b"International travel daily limit is 85 units.", ["employee"],
        "upload://policy.txt",
    )

    assert first.status == IngestionState.COMPLETED
    assert duplicate.status == IngestionState.SKIPPED
    allowed = await service.search(SearchRequest(
        tenant_id="tenant-a", query="policy",
        access_tags=["employee"], minimum_score=0.1,
    ))
    denied = await service.search(SearchRequest(
        tenant_id="tenant-b", query="travel daily limit",
        access_tags=["employee"], minimum_score=0,
    ))
    assert len(allowed) == 1
    assert allowed[0].title == "policy"
    assert denied == []


def test_access_tags_are_normalized() -> None:
    assert parse_tags(" Employee,finance,employee ") == ["employee", "finance"]


def test_context_identifier_rejects_path_characters() -> None:
    with pytest.raises(HTTPException):
        validate_context_identifier("../other-tenant", "tenant")
