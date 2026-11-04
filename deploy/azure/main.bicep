targetScope = 'resourceGroup'

@description('Short lowercase prefix used for globally unique resource names.')
@minLength(3)
@maxLength(12)
param prefix string = 'eaokit'

@allowed([
  'dev'
  'test'
  'prod'
])
param environmentName string = 'dev'

param location string = resourceGroup().location

@allowed([
  'free'
  'basic'
  'standard'
])
param searchSku string = 'basic'

@description('Set to false when an existing Azure OpenAI resource will be used.')
param deployAzureOpenAI bool = true

param tags object = {
  workload: 'enterprise-agent-orchestration-kit'
  environment: environmentName
  managedBy: 'bicep'
}

var suffix = uniqueString(subscription().subscriptionId, resourceGroup().id, prefix, environmentName)
var compactPrefix = toLower(replace(prefix, '-', ''))
var storageName = take('${compactPrefix}${environmentName}${suffix}', 24)
var searchName = take('${prefix}-${environmentName}-${suffix}', 60)
var serviceBusName = take('${prefix}-${environmentName}-${suffix}', 50)
var openAiName = take('${prefix}-aoai-${environmentName}-${suffix}', 64)
var identityName = '${prefix}-identity-${environmentName}'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'eaokit-documents-${environmentName}'
  properties: {
    publicAccess: 'None'
  }
}

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: searchName
  location: location
  tags: tags
  sku: {
    name: searchSku
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
    disableLocalAuth: environmentName == 'prod'
    hostingMode: 'default'
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    replicaCount: 1
    semanticSearch: searchSku == 'free' ? 'free' : 'standard'
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: environmentName == 'prod'
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource eventTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: 'agent-events'
  properties: {
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    requiresDuplicateDetection: true
    supportOrdering: true
  }
}

resource orchestratorSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: eventTopic
  name: 'orchestrator'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployAzureOpenAI) {
  name: openAiName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: openAiName
    disableLocalAuth: environmentName == 'prod'
    publicNetworkAccess: 'Enabled'
  }
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, 'storage-blob-data-contributor')
  scope: storage
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
  }
}

resource serviceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, identity.id, 'service-bus-data-sender')
  scope: serviceBus
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
    )
  }
}

output managedIdentityClientId string = identity.properties.clientId
output storageServiceUri string = storage.properties.primaryEndpoints.blob
output storageContainerName string = documentsContainer.name
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output serviceBusFullyQualifiedNamespace string = '${serviceBus.name}.servicebus.windows.net'
output serviceBusTopicName string = eventTopic.name
output azureOpenAIEndpoint string = deployAzureOpenAI ? 'https://${openAi.name}.openai.azure.com/' : ''
