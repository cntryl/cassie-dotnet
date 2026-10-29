#!/bin/sh
# Points this clone at the repository's versioned git hooks.
set -e
cd "$(git rev-parse --show-toplevel)"
git config core.hooksPath .githooks
chmod +x .githooks/*
echo "Git hooks enabled from .githooks"
