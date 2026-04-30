#!/bin/bash
set -e

if [[ ! -d ./dovecot-config ]] || [[ ! -f ./dovecot-config/dovecot.conf ]]; then
  echo "ERROR: ./dovecot-config/ is missing. Copy it from the repo:"
  echo "  rsync -a <repo>/dovecot-config/ ./dovecot-config/"
  exit 1
fi

echo "Pulling latest image..."
docker compose pull

echo "Stopping containers..."
docker compose down

# echo "Removing database..."
# rm -rf ./postgres-data

echo "Starting containers..."
docker compose up -d

echo "Done. Run 'docker compose logs -f mailarchive-app dovecot' to watch startup."