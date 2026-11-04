.PHONY: restore build test test-dotnet test-python test-unit test-integration test-containers test-e2e evaluate run up down logs format-check clean

restore:
	dotnet restore EnterpriseAgentOrchestrationKit.slnx

build:
	dotnet build EnterpriseAgentOrchestrationKit.slnx --no-restore

test: test-dotnet test-python

test-dotnet:
	dotnet test EnterpriseAgentOrchestrationKit.slnx --no-build

test-python:
	docker build --target test -t enterprise-agent-kit-document-worker-tests src/document-worker
	docker run --rm enterprise-agent-kit-document-worker-tests

test-unit:
	dotnet test tests/Orchestrator.UnitTests/Orchestrator.UnitTests.csproj

test-integration:
	dotnet test tests/Orchestrator.IntegrationTests/Orchestrator.IntegrationTests.csproj

test-containers:
	TESTCONTAINERS_ENABLED=true dotnet test tests/Orchestrator.IntegrationTests/Orchestrator.IntegrationTests.csproj

test-e2e:
	bash tests/e2e/full-stack.sh

evaluate:
	python3 evaluations/run.py

run:
	dotnet run --project src/Orchestrator.Api/Orchestrator.Api.csproj

up:
	docker compose up --build --wait

down:
	docker compose down

logs:
	docker compose logs --follow api

format-check:
	dotnet format EnterpriseAgentOrchestrationKit.slnx --verify-no-changes --no-restore

clean:
	dotnet clean EnterpriseAgentOrchestrationKit.slnx
