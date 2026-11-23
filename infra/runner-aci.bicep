// One customer-owned ACI runner. Enrollment runs in Azure on first start.
// A private blob preserves the enrolled identity across stop/start.
@description('Azure region for the runner.')
param location string = resourceGroup().location

@description('Container group name. Use a distinct enrolled identity for each group.')
param runnerName string = 'mbus-runner'

@description('Linux amd64 runner image pinned by tag or digest. Defaults to the official 1.5.2 image.')
@minLength(1)
param runnerImage string = 'ghcr.io/unifinity-software/messagebus-runner:1.5.2'

@description('Existing registry in this resource group. Empty for a public image.')
param containerRegistryName string = ''

@description('mbus server URL, reachable from Azure.')
param serverUrl string = 'https://app.messagebus.ai'

@description('Sign in runners key. Used only for first enrollment; can be empty after the identity is persisted.')
@secure()
param runnerApiKey string = ''

@description('Display name of the shared runner in the workspace.')
param machineName string = runnerName

@description('Claude Code API key, supplied from the customer\'s secret store.')
@secure()
param anthropicApiKey string

@description('Codex API key, supplied from the customer\'s secret store.')
@secure()
param openaiApiKey string

@description('CPU cores. Set Tasks at once to one in the dashboard for the initial deployment.')
@minValue(1)
param cpu int = 2

@description('Memory in GiB.')
@minValue(1)
param memoryInGB int = 4

var tags = { project: 'messagebus', component: 'runner' }

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (!empty(containerRegistryName)) {
  name: containerRegistryName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${runnerName}'
  location: location
  tags: tags
}

resource identityStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'mbusid${uniqueString(resourceGroup().id, runnerName)}'
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: identityStorage
  name: 'default'
}

resource identityContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'runner-identity'
  properties: { publicAccess: 'None' }
}

resource identityAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(identityContainer.id, identity.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: identityContainer
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource pullAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(containerRegistryName)) {
  name: guid(registry.id, identity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: registry
  properties: {
    principalId: identity!.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource runner 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: runnerName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    osType: 'Linux'
    restartPolicy: 'Always'
    imageRegistryCredentials: empty(containerRegistryName) ? [] : [{
      server: registry!.properties.loginServer
      identity: identity.id
    }]
    containers: [{
      name: 'runner'
      properties: {
        image: runnerImage
        command: ['/usr/bin/tini', '-s', '--', 'node', '/usr/local/lib/mbus/aci-bootstrap.mjs']
        // ACI supports only privileged here. The image's USER runner sets UID 10001.
        securityContext: { privileged: false }
        resources: { requests: { cpu: cpu, memoryInGB: memoryInGB } }
        environmentVariables: [
          { name: 'MBUS_HOME', value: '/home/runner/.mbus' }
          { name: 'MBUS_SERVER', value: serverUrl }
          { name: 'MBUS_MACHINE_NAME', value: machineName }
          { name: 'MBUS_API_KEY', secureValue: runnerApiKey }
          { name: 'MBUS_AZURE_CLIENT_ID', value: identity.properties.clientId }
          { name: 'MBUS_AZURE_IDENTITY_BLOB_URL', value: '${identityStorage.properties.primaryEndpoints.blob}${identityContainer.name}/identity.json' }
          { name: 'MBUS_LOCATION', value: 'Azure Container Instances' }
          { name: 'MBUS_DEPLOYMENT_TARGET', value: 'aci' }
          { name: 'MBUS_AZURE_RESOURCE_ID', value: resourceId('Microsoft.ContainerInstance/containerGroups', runnerName) }
          { name: 'MBUS_KEY_STORE', value: 'file' }
          { name: 'ANTHROPIC_API_KEY', secureValue: anthropicApiKey }
          { name: 'OPENAI_API_KEY', secureValue: openaiApiKey }
        ]
      }
    }]
  }
  dependsOn: [pullAccess, identityAccess]
}

@description('Resource to bind to the enrolled machine in mbus after deployment.')
output containerGroupResourceId string = runner.id

@description('Private identity storage. Preserve this when stopping or replacing the container group.')
output identityStorageAccountName string = identityStorage.name
