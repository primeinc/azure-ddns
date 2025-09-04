#!/bin/bash

# Helper script for PR review comment management
REPO="primeinc/azure-ddns"
PR_NUMBER="1"

case "$1" in
  list)
    echo "Fetching review threads..."
    gh api graphql -f query='
    query($owner: String!, $repo: String!, $pr: Int!) {
      repository(owner: $owner, name: $repo) {
        pullRequest(number: $pr) {
          reviewThreads(first: 100) {
            nodes {
              id
              isResolved
              comments(first: 1) {
                nodes {
                  body
                  path
                  line
                }
              }
            }
          }
        }
      }
    }' -f owner="primeinc" -f repo="azure-ddns" -F pr=1 --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved == false) | {id: .id, path: .comments.nodes[0].path, line: .comments.nodes[0].line, body: (.comments.nodes[0].body | split("\n")[0] | .[0:80])}'
    ;;
    
  resolve)
    THREAD_ID="$2"
    if [ -z "$THREAD_ID" ]; then
      echo "Usage: $0 resolve <thread-id>"
      exit 1
    fi
    
    echo "Resolving thread: $THREAD_ID"
    gh api graphql -f query='
    mutation ResolveReviewThread($threadId: ID!) {
      resolveReviewThread(input: {threadId: $threadId}) {
        thread {
          id
          isResolved
        }
      }
    }' -f threadId="$THREAD_ID"
    ;;
    
  *)
    echo "Usage: $0 {list|resolve <thread-id>}"
    exit 1
    ;;
esac
