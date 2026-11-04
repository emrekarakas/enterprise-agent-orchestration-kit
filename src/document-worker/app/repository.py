import json
import sqlite3
import threading
from datetime import UTC, datetime
from pathlib import Path

from app.models import IngestionResponse, IngestionState


class IngestionRepository:
    def __init__(self, database_path: Path):
        database_path.parent.mkdir(parents=True, exist_ok=True)
        self._path = database_path
        self._lock = threading.Lock()
        self._initialize()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self._path)
        connection.row_factory = sqlite3.Row
        return connection

    def _initialize(self) -> None:
        with self._connect() as connection:
            connection.execute("""
                CREATE TABLE IF NOT EXISTS ingestions (
                    ingestion_id TEXT PRIMARY KEY, document_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
                    source_uri TEXT NOT NULL, file_name TEXT NOT NULL, content_hash TEXT NOT NULL,
                    status TEXT NOT NULL, chunk_count INTEGER NOT NULL DEFAULT 0,
                    point_ids TEXT NOT NULL DEFAULT '[]', error_code TEXT, error_message TEXT,
                    created_at TEXT NOT NULL, completed_at TEXT
                )
            """)
            connection.execute(
                "CREATE INDEX IF NOT EXISTS ix_ingestions_tenant_source "
                "ON ingestions(tenant_id, source_uri, created_at)"
            )

    def latest_for_source(self, tenant_id: str, source_uri: str) -> sqlite3.Row | None:
        with self._connect() as connection:
            return connection.execute(
                "SELECT * FROM ingestions WHERE tenant_id=? AND source_uri=? "
                "ORDER BY created_at DESC LIMIT 1",
                (tenant_id, source_uri),
            ).fetchone()

    def latest_for_document(self, tenant_id: str, document_id: str) -> sqlite3.Row | None:
        with self._connect() as connection:
            return connection.execute(
                "SELECT * FROM ingestions WHERE tenant_id=? AND document_id=? "
                "ORDER BY created_at DESC LIMIT 1",
                (tenant_id, document_id),
            ).fetchone()

    def create(
        self, tenant_id: str, source_uri: str, file_name: str, content_hash: str,
        ingestion_id: str, document_id: str,
    ) -> IngestionResponse:
        created_at = datetime.now(UTC)
        with self._lock, self._connect() as connection:
            connection.execute(
                "INSERT INTO ingestions "
                "(ingestion_id,document_id,tenant_id,source_uri,file_name,content_hash,status,created_at) "
                "VALUES(?,?,?,?,?,?,?,?)",
                (ingestion_id, document_id, tenant_id, source_uri, file_name, content_hash,
                 IngestionState.PROCESSING, created_at.isoformat()),
            )
        return IngestionResponse(
            ingestion_id=ingestion_id, document_id=document_id, status=IngestionState.PROCESSING,
            file_name=file_name, content_hash=content_hash, created_at=created_at,
        )

    def finish(
        self, ingestion_id: str, state: IngestionState, chunk_count: int,
        point_ids: list[str], error_code: str | None = None, error_message: str | None = None,
    ) -> IngestionResponse:
        completed_at = datetime.now(UTC)
        with self._lock, self._connect() as connection:
            connection.execute(
                "UPDATE ingestions SET status=?,chunk_count=?,point_ids=?,error_code=?,"
                "error_message=?,completed_at=? WHERE ingestion_id=?",
                (state, chunk_count, json.dumps(point_ids), error_code, error_message,
                 completed_at.isoformat(), ingestion_id),
            )
        result = self.get(ingestion_id)
        if result is None:
            raise RuntimeError("Ingestion disappeared while it was being updated.")
        return result

    def get(self, ingestion_id: str, tenant_id: str | None = None) -> IngestionResponse | None:
        query = "SELECT * FROM ingestions WHERE ingestion_id=?"
        parameters: tuple[str, ...] = (ingestion_id,)
        if tenant_id is not None:
            query += " AND tenant_id=?"
            parameters += (tenant_id,)
        with self._connect() as connection:
            row = connection.execute(query, parameters).fetchone()
        return self._to_response(row) if row else None

    @staticmethod
    def point_ids(row: sqlite3.Row | None) -> list[str]:
        return json.loads(row["point_ids"]) if row else []

    @staticmethod
    def _to_response(row: sqlite3.Row) -> IngestionResponse:
        return IngestionResponse(
            ingestion_id=row["ingestion_id"], document_id=row["document_id"],
            status=IngestionState(row["status"]), file_name=row["file_name"],
            content_hash=row["content_hash"], chunk_count=row["chunk_count"],
            error_code=row["error_code"], error_message=row["error_message"],
            created_at=datetime.fromisoformat(row["created_at"]),
            completed_at=datetime.fromisoformat(row["completed_at"]) if row["completed_at"] else None,
        )
