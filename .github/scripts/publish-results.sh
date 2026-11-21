#!/usr/bin/env bash
# Force-pushes the contents of ./ci (logs, probe output, test results) to an
# orphan branch so they can be inspected without the GitHub API: "ci-results"
# for main, "ci-results-<branch>" for any other branch (work/x → ci-results-work-x).
set -uo pipefail
mkdir -p ci
{
  echo "commit: ${GITHUB_SHA:-unknown}"
  echo "run: ${GITHUB_RUN_NUMBER:-?} (${GITHUB_SERVER_URL:-}/${GITHUB_REPOSITORY:-}/actions/runs/${GITHUB_RUN_ID:-})"
  echo "build: ${1:-unknown}"
  echo "smoke: ${2:-unknown}"
  echo "with Vintage Doctor: ${3:-unknown}"
  echo "branch: ${GITHUB_REF_NAME:-unknown}"
  date -u
} > ci/STATUS.txt
cp -r out ci/out 2>/dev/null || true

tmp=$(mktemp -d)
cp -r ci "$tmp/"
cd "$tmp"
git init -q
branch=ci-results
ref=${GITHUB_REF_NAME:-main}
[ "$ref" != main ] && branch="ci-results-$(echo "$ref" | tr '/' '-')"
git checkout -q -b "$branch"
git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git add -A
git commit -qm "CI results for ${GITHUB_SHA:-unknown} (run ${GITHUB_RUN_NUMBER:-?})"
git push -qf "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git" "$branch"
echo "Published results to $branch"
