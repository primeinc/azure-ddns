#!/bin/bash
# Export azd environment variables to current shell
# Usage: source ./scripts/export-azd-env.sh

echo "Exporting azd environment variables..."

# Export each azd environment value
while IFS='=' read -r name value; do
    # Remove quotes from value
    value="${value%\"}"
    value="${value#\"}"
    
    # Export the variable
    export "$name=$value"
    echo "  Set $name"
done < <(azd env get-values)

echo "Environment variables exported successfully!"