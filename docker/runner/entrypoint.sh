#!/bin/sh
# Entry point of the messagebus.ai runner image. Turns secrets from the customer's secret store into
# environment variables, then starts mbus (default command: mbus runner).
#
# Secrets arrive in one of three ways; a variable that is already set always wins:
#   1. Plain environment variables (Azure Container Apps Key Vault references, docker run -e).
#   2. NAME_FILE=/path: the file's contents become NAME (the Docker / Compose convention).
#   3. A file per secret in $MBUS_SECRETS_DIR (default /run/secrets): Docker and Podman secrets,
#      Kubernetes secret volumes, the Key Vault CSI driver. The file name is the variable name;
#      lower case and dashes are accepted (anthropic-api-key -> ANTHROPIC_API_KEY).
set -eu

# Keep model credentials out of shell helpers. mbus checks the configuration and sets the key for
# managed Codex launches. Direct non-interactive agent commands can also use their provider key.
codex_key=${CODEX_API_KEY-}
openai_key=${OPENAI_API_KEY-}
unset CODEX_API_KEY OPENAI_API_KEY
[ $# -gt 0 ] || set -- runner
case "$1" in
  sh | bash | node | npm | git | claude | codex | opencode | copilot) mbus_command=false ;;
  *) mbus_command=true ;;
esac
codex_runtime=false
opencode_runtime=false
case "$1 ${2-}" in
  'codex exec' | 'codex e' | 'codex review') codex_runtime=true ;;
  'opencode run') opencode_runtime=true ;;
esac

is_name() {
  case "$1" in
    "" | [0-9]* | *[!A-Za-z0-9_]*) return 1 ;;
    *) return 0 ;;
  esac
}

is_set() {
  case "$1" in
    CODEX_API_KEY) [ -n "$codex_key" ] ;;
    OPENAI_API_KEY) [ -n "$openai_key" ] ;;
    *) eval "[ -n \"\${$1:-}\" ]" ;;
  esac
}

load_secret() {
  case "$1" in
    CODEX_API_KEY) if $mbus_command || $codex_runtime; then codex_key=$(cat "$2"); fi ;;
    OPENAI_API_KEY) if $mbus_command || $codex_runtime || $opencode_runtime; then openai_key=$(cat "$2"); fi ;;
    *) value=$(cat "$2"); export "$1=$value" ;;
  esac
}

# 2. NAME_FILE variables. Names are checked before eval, so nothing else is ever evaluated.
for var in $(env | sed -n 's/^\([A-Za-z_][A-Za-z0-9_]*\)_FILE=.*/\1/p'); do
  is_name "$var" || continue
  eval "file=\${${var}_FILE}"
  if ! is_set "$var" && [ -f "$file" ]; then
    load_secret "$var" "$file"
  fi
  unset "${var}_FILE"
done

# 3. One file per secret.
secrets_dir=${MBUS_SECRETS_DIR:-/run/secrets}
if [ -d "$secrets_dir" ]; then
  for file in "$secrets_dir"/*; do
    [ -f "$file" ] || continue
    var=$(basename "$file" | tr 'a-z-' 'A-Z_')
    is_name "$var" || continue
    if ! is_set "$var"; then
      load_secret "$var" "$file"
    fi
  done
fi

# The runner's own settings, for mbus: MBUS_SERVER (server URL), MBUS_API_KEY (a key that signs in
# runners: on first start mbus makes this machine's key and enrolls it as a shared runner),
# MBUS_MACHINE_NAME and MBUS_LOCATION (how the dashboard shows this machine). mbus reads these from
# the environment, so a container needs no interactive `mbus login`.

# A shell or an agent CLI runs as given (handy for checking the image); anything else is an mbus
# command, so `docker run messagebus-runner runner` and `... --help` both work.
if $codex_runtime; then
  case "$codex_key" in *[![:space:]]*) ;; *) codex_key=$openai_key ;; esac
  case "$codex_key" in
    *[![:space:]]*)
      export CODEX_API_KEY="$codex_key"
      shift
      exec codex -c 'shell_environment_policy.set.CODEX_API_KEY=""' "$@"
      ;;
  esac
fi
if $mbus_command; then
  [ -z "$codex_key" ] || export CODEX_API_KEY="$codex_key"
fi
if $mbus_command || $opencode_runtime; then
  [ -z "$openai_key" ] || export OPENAI_API_KEY="$openai_key"
fi
case "$1" in
  mbus | sh | bash | node | npm | git | claude | codex | opencode | copilot) exec "$@" ;;
  *) exec mbus "$@" ;;
esac
