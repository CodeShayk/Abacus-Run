# MyLibrary

Example .NET library template with:
- GitVersion-based semantic versioning
- Alpha prereleases on feature/fix branches (published to GitHub Packages)
- Stable releases on main (published to GitHub + NuGet)
- Automated tagging

## Branch Strategy

| Branch Pattern | Version Tag | Publishes To                  |
| -------------- | ----------- | ----------------------------- |
| feature/*      | -alpha.x    | GitHub Packages only          |
| fix/*          | -alpha.x    | GitHub Packages only          |
| chore/*        | -alpha.x    | GitHub Packages only          |
| main (merge)   | stable      | GitHub + NuGet (tag created)  |
| hotfix/*       | patch stable after merge | GitHub + NuGet |

## Local Dev

```bash
dotnet build
dotnet test
```

## Releasing

Merge your PR into main. The `release.yml` workflow will:
1. Compute version (e.g. 1.2.0)
2. Tag `v1.2.0`
3. Publish package to GitHub + NuGet

## Script
```
#!/usr/bin/env bash
# Local bootstrap script: create a new repo from this template.
# Requires: gh CLI (authenticated), git, dotnet.

set -euo pipefail

if [ $# -lt 2 ]; then
  echo "Usage: ./script/init-local.sh <org> <NewRepoName>"
  exit 1
fi

ORG="$1"
REPO="$2"

echo "Creating repo $ORG/$REPO..."
gh repo create "$ORG/$REPO" --public --description "New .NET library $REPO" --disable-wiki --confirm

echo "Cloning..."
git clone "https://github.com/$ORG/$REPO.git"
cd "$REPO"

echo "Copying template contents (assuming this script is run from template root)..."
# Adjust the path to template root if needed
TEMPLATE_ROOT="$(cd .. && pwd)"

rsync -av --exclude ".git" "$TEMPLATE_ROOT/" .

git add .
git commit -m "chore: initial template import"
git push origin main

echo "Done. Set secrets NUGET_API_KEY in GitHub -> Settings -> Secrets."
```

Pre-releases for feature branches appear as `1.3.0-alpha.5`.
