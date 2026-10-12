FROM python:3.12-slim
WORKDIR /app
COPY requirements.lock.txt .
RUN pip install --no-cache-dir -r requirements.lock.txt && useradd -m -u 10001 moot
COPY config_env.py server.py storage.py google_login.py wsgi.py live.py security.py serve.py backup.py ./
COPY web ./web
RUN mkdir -p /app/data /app/downloads && chown -R moot:moot /app
USER moot
ENV MOOT_STATE_PATH=/app/data/state.sqlite
ENV MOOT_INSTALLER_PATH=/app/downloads/MooT-Setup.exe
EXPOSE 8765
CMD ["python","serve.py"]
