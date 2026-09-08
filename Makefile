# Convenience wrapper around local dev infrastructure. No backend/frontend projects exist
# yet (see CLAUDE.md's phased rollout) — build/test targets are added here as each stack
# actually lands, rather than stubbed out speculatively.

.PHONY: up down restart logs ps

## Start local infra (SQL Server, Elasticsearch, Redis, RabbitMQ)
up:
	docker compose up -d

## Stop local infra
down:
	docker compose down

## Restart local infra
restart: down up

## Tail logs from all local infra containers
logs:
	docker compose logs -f

## Show local infra container status
ps:
	docker compose ps
