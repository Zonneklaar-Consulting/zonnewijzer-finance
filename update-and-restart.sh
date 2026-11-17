#!/bin/bash
# Pull the latest backend image and restart it. Smappee and Sigenergy polling run in the backend.
# The old integration-daemon container is removed so it cannot keep writing finance.db.

if [ -f IntegrationDaemon/.env ]; then
  cp IntegrationDaemon/.env backend.integration.env
  echo "Copied IntegrationDaemon/.env to backend.integration.env"
fi

echo "🔄 Pulling latest images from Docker Hub..."
docker compose -f docker-compose.production.yml pull

echo "🚀 Restarting services with updated images..."
docker compose -f docker-compose.production.yml up -d --remove-orphans
docker rm -f finance-integration-daemon >/dev/null 2>&1 || true

echo "✅ Update complete! Checking service status..."
docker compose -f docker-compose.production.yml ps
