# Azure OpenAI adapter

Phase 5 introduces an Azure OpenAI adapter behind the platform's provider-neutral chat, structured-completion, and embedding ports. Azure SDK request and response types remain inside `Orchestrator.Providers`; neither Domain nor Application references Azure packages.

The default profile remains `DeterministicFake`, so local development and CI do not require an Azure subscription or paid model.

## Configuration

The committed `appsettings.json` contains names and empty secret placeholders only:

```json
{
  "AI": {
    "Provider": "AzureOpenAI"
  },
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com",
    "ChatDeployment": "YOUR-CHAT-DEPLOYMENT",
    "StructuredOutputDeployment": "",
    "EmbeddingDeployment": "YOUR-EMBEDDING-DEPLOYMENT",
    "CredentialMode": "DefaultAzureCredential",
    "ManagedIdentityClientId": "",
    "ApiKey": ""
  }
}
```

`StructuredOutputDeployment` is optional and falls back to `ChatDeployment`. The selected chat model must support strict JSON-schema structured output.

Authentication precedence is:

1. `ApiKey` when it is non-empty.
2. User-assigned or system-assigned managed identity when `CredentialMode=ManagedIdentity`.
3. `DefaultAzureCredential` for local Azure CLI/IDE credentials and deployed workload identity.

Managed identity is the recommended deployed mode. Do not put a real API key in a committed settings file. For a local key-based smoke test, use .NET user secrets or untracked environment variables:

```bash
dotnet user-secrets set "AI:Provider" "AzureOpenAI" --project src/Orchestrator.Api
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com" --project src/Orchestrator.Api
dotnet user-secrets set "AzureOpenAI:ChatDeployment" "YOUR-CHAT-DEPLOYMENT" --project src/Orchestrator.Api
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" "YOUR-EMBEDDING-DEPLOYMENT" --project src/Orchestrator.Api
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR-LOCAL-SECRET" --project src/Orchestrator.Api
```

The API project has a `UserSecretsId`, so these values are stored outside the repository. In Azure, prefer configuration references backed by managed identity and Key Vault rather than raw secrets.

## Runtime behavior

- Grounded answer composition uses the chat deployment.
- Intent, routing, and workflow proposals use strict JSON Schema and deserialize into application-owned records.
- Invalid structured JSON returns the request's deterministic typed fallback and marks the model span as a fallback.
- Provider/network failures become safe domain errors; provider response bodies and credentials are not returned to callers.
- Token counts reported by the service are attached to OpenTelemetry spans without recording prompts or document text.
- Reranking, safety evaluation, and local token estimation remain deterministic local capabilities in this phase. They can be replaced independently through their existing ports.

No live Azure result or performance benchmark is claimed until the opt-in cloud integration suite is run against a real test resource.
