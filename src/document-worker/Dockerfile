FROM python:3.13-slim AS runtime

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

WORKDIR /app
RUN groupadd --system --gid 10001 worker && useradd --system --uid 10001 --gid worker worker
COPY pyproject.toml ./
COPY app ./app
RUN pip install --no-cache-dir .
RUN mkdir -p /data && chown -R worker:worker /app /data
USER worker
EXPOSE 8090
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8090/health/ready')"]
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8090"]

FROM runtime AS test
USER root
COPY tests ./tests
RUN pip install --no-cache-dir ".[test]"
CMD ["pytest", "-q"]

FROM runtime AS final
