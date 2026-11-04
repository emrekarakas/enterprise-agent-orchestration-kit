import hashlib
from pathlib import Path
import shutil
from uuid import NAMESPACE_URL, uuid4, uuid5

from app.chunking import chunk_sections
from app.config import Settings
from app.embedding import embed
from app.extraction import extract
from app.models import IngestionResponse, IngestionState, SearchHit, SearchRequest, VectorPoint
from app.repository import IngestionRepository
from app.vector_store import VectorStore


class IngestionService:
    def __init__(
        self, settings: Settings, repository: IngestionRepository, vector_store: VectorStore,
    ) -> None:
        self._settings = settings
        self._repository = repository
        self._vectors = vector_store

    async def ingest(
        self, tenant_id: str, user_id: str, file_name: str, extension: str,
        content: bytes, access_tags: list[str], source_uri: str,
    ) -> IngestionResponse:
        content_hash = hashlib.sha256(content).hexdigest()
        previous = self._repository.latest_for_source(tenant_id, source_uri)
        document_id = previous["document_id"] if previous else str(uuid4())
        record = self._repository.create(
            tenant_id, source_uri, file_name, content_hash, str(uuid4()), document_id,
        )
        if previous and previous["content_hash"] == content_hash and previous["status"] in {
            IngestionState.COMPLETED, IngestionState.SKIPPED,
        }:
            return self._repository.finish(
                record.ingestion_id, IngestionState.SKIPPED, previous["chunk_count"],
                self._repository.point_ids(previous),
            )

        try:
            title = Path(file_name).stem
            chunks = chunk_sections(
                extract(content, extension), self._settings.chunk_words,
                self._settings.chunk_overlap_words,
            )
            if not chunks:
                raise ValueError("No indexable chunks were produced.")
            points = [
                VectorPoint(
                    str(uuid5(NAMESPACE_URL, f"{document_id}:{chunk.chunk_id}")),
                    embed(
                        f"{title} {chunk.locator} {chunk.content}",
                        self._settings.embedding_dimensions,
                    ),
                    {
                        "document_id": document_id, "chunk_id": chunk.chunk_id,
                        "tenant_id": tenant_id, "access_tags": access_tags,
                        "content": chunk.content, "title": title,
                        "source_uri": source_uri, "locator": chunk.locator,
                        "content_hash": content_hash, "ingestion_version": "1",
                        "uploaded_by": user_id,
                    },
                )
                for chunk in chunks
            ]
            if previous:
                await self._vectors.delete(self._repository.point_ids(previous))
            await self._vectors.upsert(points)
            if self._settings.store_originals:
                target = self._settings.data_dir / "originals" / tenant_id / document_id
                target.mkdir(parents=True, exist_ok=True)
                (target / file_name).write_bytes(content)
            return self._repository.finish(
                record.ingestion_id, IngestionState.COMPLETED, len(points),
                [point.point_id for point in points],
            )
        except Exception as error:
            self._repository.finish(
                record.ingestion_id, IngestionState.FAILED, 0, [],
                "ingestion.failed", str(error)[:500],
            )
            raise

    async def delete(self, tenant_id: str, document_id: str) -> bool:
        latest = self._repository.latest_for_document(tenant_id, document_id)
        if latest is None:
            return False
        await self._vectors.delete(self._repository.point_ids(latest))
        self._repository.finish(latest["ingestion_id"], IngestionState.DELETED, 0, [])
        originals = self._settings.data_dir / "originals" / tenant_id / document_id
        if originals.exists():
            shutil.rmtree(originals)
        return True

    async def search(self, request: SearchRequest) -> list[SearchHit]:
        matches = await self._vectors.search(
            embed(request.query, self._settings.embedding_dimensions),
            request.tenant_id, request.access_tags, request.top, request.minimum_score,
        )
        unique: dict[tuple[object, object, object], SearchHit] = {}
        for match in matches:
            key = (
                match.payload.get("content_hash"),
                match.payload.get("locator"),
                match.payload.get("content"),
            )
            unique.setdefault(key, SearchHit(score=match.score, **match.payload))
        return list(unique.values())[: request.top]
