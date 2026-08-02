ROOT := $(CURDIR)
SERVER_DIR := $(ROOT)/GameMCPServer
UNITY_DIR := $(ROOT)/unity-NPC-agent-client
export GOCACHE := $(ROOT)/.cache/go-build

.PHONY: help env-check server test unity-info

help:
	@echo "Available commands:"
	@echo "  make server      Start the Go Agent Service"
	@echo "  make test        Run Go server tests"
	@echo "  make env-check   Check root env files"
	@echo "  make unity-info  Print Unity project information"

env-check:
	@if [ ! -f "$(ROOT)/.env.local" ] && [ ! -f "$(ROOT)/.env" ]; then \
		echo "Missing local configuration. Copy .env.example to .env.local and fill local values."; \
		exit 1; \
	fi
	@echo "Local environment configuration exists"

server:
	cd "$(SERVER_DIR)" && go run ./cmd/server

test:
	cd "$(SERVER_DIR)" && go test ./...

unity-info:
	@echo "Unity project: $(UNITY_DIR)"
	@echo "Unity version:"
	@cat "$(UNITY_DIR)/ProjectSettings/ProjectVersion.txt"
