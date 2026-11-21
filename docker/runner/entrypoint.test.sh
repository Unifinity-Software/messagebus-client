#!/bin/sh
set -eu

entrypoint=$(dirname "$0")/entrypoint.sh
scratch=$(mktemp -d)
trap 'rm -f "$scratch/mbus" "$scratch/codex" "$scratch/opencode" "$scratch/secrets/openai-api-key" "$scratch/secrets/codex-api-key"; rmdir "$scratch/secrets" "$scratch"' EXIT
mkdir "$scratch/secrets"

cat > "$scratch/mbus" <<'EOF'
#!/bin/sh
printf '%s|%s|%s\n' "$*" "${CODEX_API_KEY-unset}" "${OPENAI_API_KEY-unset}"
EOF
chmod +x "$scratch/mbus"
cp "$scratch/mbus" "$scratch/codex"
cp "$scratch/mbus" "$scratch/opencode"
printf '%s\n' fixture-openai > "$scratch/secrets/openai-api-key"
printf '%s\n' fixture-codex > "$scratch/secrets/codex-api-key"
export PATH="$scratch:$PATH" MBUS_SECRETS_DIR="$scratch/secrets"
unset CODEX_API_KEY OPENAI_API_KEY OPENAI_API_KEY_FILE CODEX_API_KEY_FILE

[ "$(sh "$entrypoint")" = 'runner|fixture-codex|fixture-openai' ]
[ "$(CODEX_API_KEY=fixture-explicit sh "$entrypoint" mbus doctor --smoke --agent codex)" = 'doctor --smoke --agent codex|fixture-explicit|fixture-openai' ]
[ "$(OPENAI_API_KEY_FILE="$scratch/secrets/codex-api-key" sh "$entrypoint" runner)" = 'runner|fixture-codex|fixture-codex' ]
[ "$(sh "$entrypoint" codex exec resume --last)" = '-c shell_environment_policy.set.CODEX_API_KEY="" exec resume --last|fixture-codex|unset' ]
[ "$(CODEX_API_KEY=' ' sh "$entrypoint" codex review)" = '-c shell_environment_policy.set.CODEX_API_KEY="" review|fixture-openai|unset' ]
[ "$(sh "$entrypoint" codex --version)" = '--version|unset|unset' ]
[ "$(sh "$entrypoint" opencode run)" = 'run|unset|fixture-openai' ]
CODEX_API_KEY=fixture-explicit OPENAI_API_KEY_FILE="$scratch/secrets/openai-api-key" sh "$entrypoint" sh -c \
  'test "${CODEX_API_KEY-unset}|${OPENAI_API_KEY-unset}|${OPENAI_API_KEY_FILE-unset}" = "unset|unset|unset"'
