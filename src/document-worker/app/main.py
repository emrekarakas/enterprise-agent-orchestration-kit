from contextlib import asynccontextmanager

from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile

from app.config import Settings
from app.models import IngestionResponse, SearchHit, SearchRequest
from app.repository import IngestionRepository
from app.security import parse_tags, validate_context_identifier, validate_upload
from app.service import IngestionService
from app.vector_store import InMemoryVectorStore, QdrantVectorStore


settings = Settings()
settings.data_dir.mkdir(parents=True, exist_ok=True)
repository = IngestionRepository(settings.data_dir / "worker.db")
vector_store = (
    QdrantVectorStore(settings.qdrant_url, settings.qdrant_collection, settings.embedding_dimensions)
    if settings.vector_store.lower() == "qdrant"
    else InMemoryVectorStore()
)
service = IngestionService(settings, repository, vector_store)


@asynccontextmanager
async def lifespan(_: FastAPI):
    await vector_store.initialize()
    yield


app = FastAPI(
    title="Enterprise Agent Document Worker", version="0.1.0",
    description="Extracts, chunks and indexes tenant-scoped documents for internal API use.",
    lifespan=lifespan,
)


@app.get("/health/live", include_in_schema=False)
async def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready", include_in_schema=False)
async def ready() -> dict[str, str]:
    return {"status": "ready"}


@app.post("/v1/ingestions", response_model=IngestionResponse, status_code=201)
async def ingest(
    file: UploadFile = File(...), access_tags: str = Form(...),
    source_uri: str | None = Form(None),
    tenant_id: str = Header(..., alias="X-Tenant-Id"),
    user_id: str = Header(..., alias="X-User-Id"),
) -> IngestionResponse:
    content = await file.read(settings.max_upload_bytes + 1)
    tenant_id = validate_context_identifier(tenant_id, "tenant")
    user_id = validate_context_identifier(user_id, "user")
    file_name, extension = validate_upload(file, content, settings.max_upload_bytes)
    resolved_source = source_uri.strip() if source_uri and source_uri.strip() else f"upload://{file_name}"
    if len(resolved_source) > 2000:
        raise HTTPException(status_code=400, detail="The source URI is too long.")
    try:
        return await service.ingest(
            tenant_id, user_id, file_name, extension, content,
            parse_tags(access_tags), resolved_source,
        )
    except ValueError as error:
        raise HTTPException(status_code=422, detail=str(error)) from error


@app.get("/v1/ingestions/{ingestion_id}", response_model=IngestionResponse)
async def status(
    ingestion_id: str, tenant_id: str = Header(..., alias="X-Tenant-Id"),
) -> IngestionResponse:
    tenant_id = validate_context_identifier(tenant_id, "tenant")
    result = repository.get(ingestion_id, tenant_id)
    if result is None:
        raise HTTPException(status_code=404, detail="Ingestion was not found.")
    return result


@app.delete("/v1/documents/{document_id}", status_code=204)
async def delete_document(
    document_id: str, tenant_id: str = Header(..., alias="X-Tenant-Id"),
) -> None:
    tenant_id = validate_context_identifier(tenant_id, "tenant")
    if not await service.delete(tenant_id, document_id):
        raise HTTPException(status_code=404, detail="Document was not found.")


@app.post("/v1/search", response_model=list[SearchHit])
async def search(request: SearchRequest) -> list[SearchHit]:
    return await service.search(request)
