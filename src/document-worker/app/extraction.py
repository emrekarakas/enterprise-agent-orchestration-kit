from io import BytesIO

from docx import Document
from openpyxl import load_workbook
from pptx import Presentation
from pypdf import PdfReader

from app.models import ExtractedSection


def extract(content: bytes, extension: str) -> list[ExtractedSection]:
    extractors = {
        ".txt": _extract_text,
        ".md": _extract_markdown,
        ".pdf": _extract_pdf,
        ".docx": _extract_docx,
        ".pptx": _extract_pptx,
        ".xlsx": _extract_xlsx,
    }
    sections = [section for section in extractors[extension](content) if section.text.strip()]
    if not sections:
        raise ValueError("No extractable text was found in the document.")
    return sections


def _decode(content: bytes) -> str:
    try:
        return content.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise ValueError("Text documents must be UTF-8 encoded.") from error


def _extract_text(content: bytes) -> list[ExtractedSection]:
    return [ExtractedSection(_decode(content), "document")]


def _extract_markdown(content: bytes) -> list[ExtractedSection]:
    text = _decode(content)
    sections: list[ExtractedSection] = []
    heading = "document"
    buffer: list[str] = []
    for line in text.splitlines():
        if line.lstrip().startswith("#"):
            if buffer:
                sections.append(ExtractedSection("\n".join(buffer), heading))
                buffer = []
            heading = line.lstrip("# ").strip() or heading
        else:
            buffer.append(line)
    if buffer:
        sections.append(ExtractedSection("\n".join(buffer), heading))
    return sections


def _extract_pdf(content: bytes) -> list[ExtractedSection]:
    reader = PdfReader(BytesIO(content))
    return [
        ExtractedSection(page.extract_text() or "", f"page {number}")
        for number, page in enumerate(reader.pages, 1)
    ]

def _extract_docx(content: bytes) -> list[ExtractedSection]:
    document = Document(BytesIO(content))
    return [ExtractedSection("\n".join(paragraph.text for paragraph in document.paragraphs), "document")]


def _extract_pptx(content: bytes) -> list[ExtractedSection]:
    presentation = Presentation(BytesIO(content))
    return [
        ExtractedSection(
            "\n".join(shape.text for shape in slide.shapes if hasattr(shape, "text")),
            f"slide {number}",
        )
        for number, slide in enumerate(presentation.slides, 1)
    ]


def _extract_xlsx(content: bytes) -> list[ExtractedSection]:
    workbook = load_workbook(BytesIO(content), read_only=True, data_only=True)
    return [
        ExtractedSection(
            "\n".join(
                " | ".join(str(cell) for cell in row if cell is not None)
                for row in worksheet.iter_rows(values_only=True)
                if any(cell is not None for cell in row)
            ),
            f"sheet {worksheet.title}",
        )
        for worksheet in workbook.worksheets
    ]
