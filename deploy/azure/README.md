# Azure deployment foundation

`main.bicep` provisions the managed cloud dependencies used by the adapters:

- user-assigned managed identity;
- private blob container with soft-delete retention;
- Azure AI Search service;
- Azure Service Bus Standard namespace, topic, and subscription;
- optional Azure OpenAI account;
- least-privilege Blob Contributor and Service Bus Sender assignments.

It intentionally does not deploy a paid application runtime or PostgreSQL server automatically. This keeps an accidental portfolio deployment from creating long-running compute charges. The API and worker can run in Container Apps, AKS, App Service, or another approved runtime and use the emitted managed identity values.

Preview before deploying:

```bash
az deployment group what-if \
  --resource-group YOUR_RESOURCE_GROUP \
  --template-file deploy/azure/main.bicep \
  --parameters deploy/azure/main.parameters.example.json
```

Deploy only after reviewing the cost-bearing resources:

```bash
az deployment group create \
  --resource-group YOUR_RESOURCE_GROUP \
  --template-file deploy/azure/main.bicep \
  --parameters deploy/azure/main.parameters.example.json
```

Use the outputs as `AzureBlobStorage`, `AzureAiSearch`, `AzureServiceBus`, and `ManagedIdentityClientId` configuration. Model deployments and Azure AI Search data-plane objects remain explicit operations; their JSON templates are under `search/`.
