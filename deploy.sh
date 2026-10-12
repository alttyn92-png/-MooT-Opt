#!/bin/sh
set -eu
cd "$(dirname "$0")"
if [ ! -f .env ]; then
  cp .env.example .env
  chmod 600 .env
  echo 'Создан .env. Заполните ALEM_API_KEY, GOOGLE_CLIENT_ID и GOOGLE_CLIENT_SECRET; затем повторите запуск.'
  exit 1
fi
chmod 600 .env
for key in ALEM_API_KEY GOOGLE_CLIENT_ID GOOGLE_CLIENT_SECRET; do
  if ! grep -Eq "^${key}=.+" .env; then
    echo "Заполните ${key} в .env"
    exit 1
  fi
done
docker compose config --quiet
docker compose up -d --build --wait
echo 'MooT запущен: https://cultmoot.online'
