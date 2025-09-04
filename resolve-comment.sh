#!/bin/bash
# Script to resolve a PR review comment using GitHub GraphQL API

COMMENT_ID=$1

if [ -z "$COMMENT_ID" ]; then
  echo "Usage: $0 <comment-id>"
  exit 1
fi

# GraphQL mutation to resolve a review thread
gh api graphql -f query='
mutation ResolveReviewThread($threadId: ID!) {
  resolveReviewThread(input: {threadId: $threadId}) {
    thread {
      id
      isResolved
    }
  }
}' -f threadId="$COMMENT_ID"
