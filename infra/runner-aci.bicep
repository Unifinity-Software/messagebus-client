// One customer-owned ACI runner. Enroll its identity once with `mbus login`, then
// supply config.json and machine-key.pem through secure deployment parameters.
// ACI restores those files from a secret volume after every stop/start.
@description('Azure region for the runner.')
param location string = resourceGroup().location

@description('Container group name. Use a distinct enrolled identity for each group.')
param runnerName string = 'mbus-runner'

@description('Linux amd64 runner image built from a release, pinned by tag or digest.')
param runnerImage string

@description('Existing registry in this resource group. Empty for a public image.')
param containerRegistryName string = ''

@description('config.json from this runner\'s dedicated, enrolled mbus settings directory.')
@secure()
param machineConfig string

@description('machine-key.pem belonging to machineConfig. Keep the original in a secret store.')
@secure()
param machinePrivateKey string

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

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (!empty(containerRegistryName)) {
  name: 'id-${runnerName}'
  location: location
  tags: tags
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

// The image's runner UID owns local POSIX storage. Secret volumes survive ACI
// stop/start and never require the Azure Files root mount or its synthetic modes.
var bootstrap = '''
set -eu
[ "$(id -u)" = 10001 ] || { echo 'The runner image must use UID 10001.' >&2; exit 1; }
umask 077
mkdir -p "$MBUS_HOME"
cat /run/mbus-identity/config.json > "$MBUS_HOME/config.json"
cat /run/mbus-identity/machine-key.pem > "$MBUS_HOME/machine-key.pem"
chmod 700 "$MBUS_HOME"
chmod 600 "$MBUS_HOME/config.json" "$MBUS_HOME/machine-key.pem"
exec /usr/local/bin/mbus-entrypoint runner
'''

resource runner 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: runnerName
  location: location
  tags: tags
  identity: empty(containerRegistryName) ? null : {
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
        command: ['/usr/bin/tini', '-s', '--', '/bin/sh', '-c', bootstrap]
        // ACI supports only privileged here. The image's USER runner sets UID 10001.
        securityContext: { privileged: false }
        resources: { requests: { cpu: cpu, memoryInGB: memoryInGB } }
        environmentVariables: [
          { name: 'MBUS_HOME', value: '/home/runner/.mbus' }
          { name: 'MBUS_LOCATION', value: 'Azure Container Instances' }
          { name: 'MBUS_KEY_STORE', value: 'file' }
          { name: 'ANTHROPIC_API_KEY', secureValue: anthropicApiKey }
          { name: 'OPENAI_API_KEY', secureValue: openaiApiKey }
        ]
        volumeMounts: [{ name: 'identity', mountPath: '/run/mbus-identity', readOnly: true }]
      }
    }]
    volumes: [{
      name: 'identity'
      secret: {
        'config.json': base64(machineConfig)
        'machine-key.pem': base64(machinePrivateKey)
      }
    }]
  }
  dependsOn: [pullAccess]
}

@description('Resource to bind to the enrolled machine in mbus after deployment.')
output containerGroupResourceId string = runner.id
