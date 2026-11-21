#!/usr/bin/env bash
set -euo pipefail

sha=$1
passed=$(gh api --method GET "repos/$GITHUB_REPOSITORY/actions/workflows/build.yml/runs" \
  -f branch=main -f event=push -f head_sha="$sha" -F per_page=1 \
  --jq '.workflow_runs[0] | .status == "completed" and .conclusion == "success"')
[[ "$passed" == true ]] || { echo 'The latest post-merge Build is no longer green.' >&2; exit 1; }
disclosure=$(gh api --paginate "repos/$GITHUB_REPOSITORY/commits/$sha/statuses?per_page=100" \
  --jq '.[] | select(.context == "org-strings") | .state')
[[ "${disclosure%%$'\n'*}" == success ]] || { echo 'The org-strings status is no longer green.' >&2; exit 1; }
