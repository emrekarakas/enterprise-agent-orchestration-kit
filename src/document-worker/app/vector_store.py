from abc import ABC, abstractmethod
import asyncio

import httpx

from app.models import VectorMatch, VectorPoint


class VectorStore(ABC):
    @abstractmethod
    async def initialize(self) -> None: ...

    @abstractmethod
    async def upsert(self, points: list[VectorPoint]) -> None: ...

    @abstractmethod
    async def delete(self, point_ids: list[str]) -> None: ...

    @abstractmethod
    async def search(
        self, vector: list[float], tenant_id: str, access_tags: list[str],
        top: int, minimum_score: float,
    ) -> list[VectorMatch]: ...


class InMemoryVectorStore(VectorStore):
    def __init__(self) -> None:
        self.points: dict[str, VectorPoint] = {}

    async def initialize(self) -> None:
        return None

    async def upsert(self, points: list[VectorPoint]) -> None:
        self.points.update({point.point_id: point for point in points})

    async def delete(self, point_ids: list[str]) -> None:
        for point_id in point_ids:
            self.points.pop(point_id, None)

    async def search(
        self, vector: list[float], tenant_id: str, access_tags: list[str],
        top: int, minimum_score: float,
    ) -> list[VectorMatch]:
        matches: list[VectorMatch] = []
        allowed = set(access_tags)
        for point in self.points.values():
            payload_tags = set(point.payload.get("access_tags", []))
            if point.payload.get("tenant_id") != tenant_id or not allowed.intersection(payload_tags):
                continue
            score = sum(left * right for left, right in zip(vector, point.vector, strict=True))
            if score >= minimum_score:
                matches.append(VectorMatch(score, point.payload))
        return sorted(matches, key=lambda match: match.score, reverse=True)[:top]


class QdrantVectorStore(VectorStore):
    def __init__(self, base_url: str, collection: str, dimensions: int) -> None:
        self._client = httpx.AsyncClient(base_url=base_url.rstrip("/"), timeout=20.0)
        self._collection = collection
        self._dimensions = dimensions

    async def initialize(self) -> None:
        last_error: Exception | None = None
        for attempt in range(10):
            try:
                response = await self._client.get(f"/collections/{self._collection}")
                if response.status_code == 404:
                    response = await self._client.put(
                        f"/collections/{self._collection}",
                        json={"vectors": {"size": self._dimensions, "distance": "Cosine"}},
                    )
                response.raise_for_status()
                return
            except (httpx.HTTPError, OSError) as error:
                last_error = error
                await asyncio.sleep(min(1 + attempt, 5))
        raise RuntimeError("Qdrant did not become ready in time.") from last_error

    async def upsert(self, points: list[VectorPoint]) -> None:
        response = await self._client.put(
            f"/collections/{self._collection}/points?wait=true",
            json={"points": [
                {"id": point.point_id, "vector": point.vector, "payload": point.payload}
                for point in points
            ]},
        )
        response.raise_for_status()

    async def delete(self, point_ids: list[str]) -> None:
        if not point_ids:
            return
        response = await self._client.post(
            f"/collections/{self._collection}/points/delete?wait=true",
            json={"points": point_ids},
        )
        response.raise_for_status()

    async def search(
        self, vector: list[float], tenant_id: str, access_tags: list[str],
        top: int, minimum_score: float,
    ) -> list[VectorMatch]:
        response = await self._client.post(
            f"/collections/{self._collection}/points/query",
            json={
                "query": vector,
                "filter": {"must": [
                    {"key": "tenant_id", "match": {"value": tenant_id}},
                    {"key": "access_tags", "match": {"any": access_tags}},
                ]},
                "limit": top, "score_threshold": minimum_score, "with_payload": True,
            },
        )
        response.raise_for_status()
        return [
            VectorMatch(point["score"], point["payload"])
            for point in response.json()["result"]["points"]
        ]
