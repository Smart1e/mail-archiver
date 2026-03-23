#!/bin/bash
set -e

echo "Pulling latest image..."
docker compose pull

echo "Stopping containers..."
docker compose down

echo "Removing database..."
rm -rf ./postgres-data

echo "Starting containers..."
docker compose up -d

echo "Done. Run 'docker compose logs -f mailarchive-app' to watch startup."
