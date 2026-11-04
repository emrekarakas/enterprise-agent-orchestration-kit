import re
from pathlib import Path

from fastapi import HTTPException, UploadFile


ALLOWED_CONTENT_TYPES: dict[str, set[str]] = {
    ".pdf": {"application/pdf"},
    ".docx": {"application/vnd.openxmlformats-officedocument.wordprocessingml.document"},
    ".pptx": {"application/vnd.openxmlformats-officedocument.presentationml.presentation"},
    ".xlsx": {"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"},
    ".md": {"text/markdown", "text/plain"},
    ".txt": {"text/plain"},
}
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._ -]{0,199}$")
SAFE_CONTEXT_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")


def validate_upload(file: UploadFile, content: bytes, max_bytes: int) -> tuple[str, str]:
    name = file.filename or ""
    if not name or Path(name).name != name or "/" in name or "\\" in name or not SAFE_NAME.fullmatch(name):
        raise HTTPException(status_code=400, detail="The file name is invalid.")
    if not content:
        raise HTTPException(status_code=400, detail="The uploaded file is empty.")
    if len(content) > max_bytes:
        raise HTTPException(status_code=413, detail="The uploaded file exceeds the configured size limit.")

    extension = Path(name).suffix.lower()
    allowed_types = ALLOWED_CONTENT_TYPES.get(extension)
    if allowed_types is None:
        raise HTTPException(status_code=415, detail="The file type is not supported.")
    content_type = (file.content_type or "application/octet-stream").lower()
    if content_type not in allowed_types and content_type != "application/octet-stream":
        raise HTTPException(status_code=415, detail="The MIME type does not match the file extension.")
    if extension == ".pdf" and not content.startswith(b"%PDF-"):
        raise HTTPException(status_code=415, detail="The PDF signature is invalid.")
    if extension in {".docx", ".pptx", ".xlsx"} and not content.startswith(b"PK"):
        raise HTTPException(status_code=415, detail="The Office document signature is invalid.")
    if extension in {".txt", ".md"} and b"\x00" in content[:4096]:
        raise HTTPException(status_code=415, detail="The text document contains binary data.")
    return name, extension


def parse_tags(value: str) -> list[str]:
    tags = sorted({tag.strip().lower() for tag in value.split(",") if tag.strip()})
    if not tags or len(tags) > 50 or any(len(tag) > 100 for tag in tags):
        raise HTTPException(status_code=400, detail="At least one valid access tag is required.")
    return tags


def validate_context_identifier(value: str, name: str) -> str:
    if not SAFE_CONTEXT_ID.fullmatch(value):
        raise HTTPException(status_code=400, detail=f"The {name} identifier is invalid.")
    return value
