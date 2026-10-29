FROM docker:27.5.1-cli AS docker-cli
FROM python:3.13.2-alpine3.21
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
WORKDIR /app
COPY collector.py /app/collector.py
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
ENTRYPOINT ["python", "/app/collector.py"]
