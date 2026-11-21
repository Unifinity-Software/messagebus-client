// A messagebus.ai runner in Azure: the runner image (docker/runner) as an Azure Container App with
// no inbound address, its secrets read from an existing Key Vault. The
// runner connects out to the server, picks up tasks and runs a coding agent for each. Deploy it in
// the same resource group as the vault. See docker/runner/README.md for ACA setup and restart behavior.
// Check out mbus-v<version>, build from docker/runner/Dockerfile, and push the resulting
// <registry>.azurecr.io/messagebus-runner:<version> image to your registry. For a development
// build, use your development checkout and an explicit development image tag.

@description('Azure region for the runner.')
param location string = resourceGroup().location

@description('Name of the container app, also its machine name in the dashboard.')
@maxLength(32)
param runnerName string = 'runner-1'

@description('Runner image built from source, e.g. <registry>.azurecr.io/messagebus-runner:<version>.')
param runnerImage string

@description('Azure Container Registry in this resource group the image comes from. Empty: a public image.')
param containerRegistryName string = ''

@description('The address of the messagebus.ai server, such as https://app.messagebus.ai.')
param serverUrl string

@description('The machine\'s Location label in the dashboard.')
param machineLocation string = 'Azure'

@description('Existing Key Vault in this resource group that holds the runner\'s secrets.')
param keyVaultName string

@description('''Secrets the runner gets as environment variables: each item is { env, secret }, the variable name and
the Key Vault secret it comes from. Every listed secret must exist in the vault. mbus-api-key (an API key that signs in runners,
created on the dashboard's Security page) is required; add the keys for the agents this runner uses, for example
{ env: 'ANTHROPIC_API_KEY', secret: 'anthropic-api-key' } for Claude Code, OPENAI_API_KEY for Codex,
COPILOT_GITHUB_TOKEN for Copilot. docker/runner/README.md lists them.''')
param secrets array = [
  {
    env: 'MBUS_API_KEY'
    secret: 'mbus-api-key'
  }
]

@description('Existing subnet (delegated to Microsoft.App/environments) to put the runner on, for example to reach your own private network. Empty: no network.')
param runnerSubnetId string = ''

@description('''Container Apps environment the runners share (created by the first deployment, reused by the next). A
subnet holds only one environment, so every runner on the same subnet must use the same one.''')
param runnerEnvironmentName string = 'mbus-runners'

@description('CPU cores for the runner. Coding agents build and test code, so give them room.')
param cpu string = '2'

@description('Memory for the runner, e.g. 4Gi (Container Apps needs 2Gi per core).')
param memory string = '4Gi'

var tags = {
  project: 'messagebus'
  component: 'runner'
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (!empty(containerRegistryName)) {
  name: containerRegistryName
}

// One identity for reading the vault and pulling the image; it exists (and holds its roles) before
// the container app's first revision starts.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${runnerName}'
  location: location
  tags: tags
}

// Key Vault Secrets User — read secret values, scoped to the vault.
resource secretsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    principalId: identity.properties.principalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalType: 'ServicePrincipal'
  }
}

// AcrPull on the registry, when the image is private.
resource pullAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(containerRegistryName)) {
  name: guid(registry.id, identity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7f951dda-4ed3-4680-a7ca-43fe172d538d'
    )
    principalType: 'ServicePrincipal'
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${runnerEnvironmentName}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: runnerEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    vnetConfiguration: empty(runnerSubnetId)
      ? null
      : {
          infrastructureSubnetId: runnerSubnetId
          internal: true // the runner has no inbound address anyway
        }
  }
}

var secretRefs = [
  for item in secrets: {
    name: toLower(replace(item.env, '_', '-'))
    keyVaultUrl: 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets/${item.secret}'
    identity: identity.id
  }
]

var secretEnv = [
  for item in secrets: {
    name: item.env
    secretRef: toLower(replace(item.env, '_', '-'))
  }
]

resource runner 'Microsoft.App/containerApps@2024-03-01' = {
  name: runnerName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress: the runner only connects out to the server.
      secrets: secretRefs
      registries: empty(containerRegistryName)
        ? []
        : [
            {
              server: registry!.properties.loginServer
              identity: identity.id
            }
          ]
    }
    template: {
      // The container home is ephemeral. A replacement replica enrolls with a new machine key.
      containers: [
        {
          name: 'runner'
          image: runnerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(
            [
              {
                name: 'MBUS_SERVER'
                value: serverUrl
              }
              {
                name: 'MBUS_MACHINE_NAME'
                value: runnerName
              }
              {
                name: 'MBUS_LOCATION'
                value: machineLocation
              }
            ],
            secretEnv
          )
        }
      ]
      scale: {
        // One runner per container app: it holds one connection to the server.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    secretsAccess
    pullAccess
  ]
}

output runnerName string = runner.name
output identityPrincipalId string = identity.properties.principalId
