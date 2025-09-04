#!/usr/bin/env bash

# Updates Azure AD app redirect URIs with custom domain while preserving existing URIs
#
# This script fetches current redirect URIs from an Azure AD app, adds the new custom domain
# redirect URI if not present, and deploys the Entra ID Bicep template to update the app.

set -euo pipefail

# Default values
APP_ID="3109db3d-f7e7-4d55-ae7d-ac2170d1335c"
CUSTOM_DOMAIN=""
ENVIRONMENT="azure-ddns-dev"

# Parse command-line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --app-id)
            APP_ID="$2"
            shift 2
            ;;
        --custom-domain)
            CUSTOM_DOMAIN="$2"
            shift 2
            ;;
        --environment)
            ENVIRONMENT="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1" >&2
            echo "Usage: $0 [--app-id <app-id>] [--custom-domain <domain>] [--environment <env>]" >&2
            exit 1
            ;;
    esac
done

# Get script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# If no custom domain provided, try to get from azd env
if [[ -z "$CUSTOM_DOMAIN" ]]; then
    if command -v azd &> /dev/null; then
        # Parse azd env get-values output
        while IFS='=' read -r key value; do
            if [[ "$key" == "customDomainName" ]]; then
                # Remove quotes if present
                CUSTOM_DOMAIN="${value//\"/}"
                break
            fi
        done < <(cd "$SCRIPT_DIR/.." && azd env get-values 2>/dev/null || true)
    fi
fi

if [[ -z "$CUSTOM_DOMAIN" ]]; then
    echo -e "\033[0;33mNo custom domain configured. Skipping Entra ID update.\033[0m"
    exit 0
fi

echo -e "\033[0;32mUpdating Azure AD app redirect URIs for custom domain: $CUSTOM_DOMAIN\033[0m"

# Fetch app details including unique name
echo -e "\033[0;36mFetching existing app details...\033[0m"
APP_DETAILS=$(az ad app show --id "$APP_ID" -o json 2>/dev/null || echo "")

if [[ -z "$APP_DETAILS" ]] || [[ "$APP_DETAILS" == "{}" ]]; then
    echo -e "\033[0;31mCould not find Azure AD app with ID: $APP_ID\033[0m" >&2
    exit 1
fi

# Get the unique name (usually same as appId for apps created via portal)
APP_UNIQUE_NAME=$(echo "$APP_DETAILS" | jq -r '.uniqueName // .appId // empty')
if [[ -z "$APP_UNIQUE_NAME" ]]; then
    APP_UNIQUE_NAME="$APP_ID"
fi

# Get existing redirect URIs as JSON array
EXISTING_URIS=$(echo "$APP_DETAILS" | jq -r '.web.redirectUris // []')

# Build new URI to add
NEW_URI="https://$CUSTOM_DOMAIN/.auth/login/aad/callback"

# Check if the new URI already exists
URI_ALREADY_EXISTS=false
if echo "$EXISTING_URIS" | jq -e --arg uri "$NEW_URI" 'contains([$uri])' > /dev/null 2>&1; then
    echo -e "\033[0;33mRedirect URI already exists: $NEW_URI\033[0m"
    URI_ALREADY_EXISTS=true
    # Get all URIs as-is
    ALL_URIS="$EXISTING_URIS"
else
    echo -e "\033[0;32mAdding new redirect URI: $NEW_URI\033[0m"
    # Add new URI to the array
    ALL_URIS=$(echo "$EXISTING_URIS" | jq --arg uri "$NEW_URI" '. + [$uri]')
fi

# Build homepage and logout URLs
HOMEPAGE_URL="https://$CUSTOM_DOMAIN/"
LOGOUT_URL="https://$CUSTOM_DOMAIN/.auth/logout"

# Check if homepage and logout URLs also match current config
CURRENT_HOMEPAGE=$(echo "$APP_DETAILS" | jq -r '.web.homePageUrl // ""')
CURRENT_LOGOUT=$(echo "$APP_DETAILS" | jq -r '.web.logoutUrl // ""')

if [[ "$URI_ALREADY_EXISTS" == "true" ]] && \
   [[ "$CURRENT_HOMEPAGE" == "$HOMEPAGE_URL" ]] && \
   [[ "$CURRENT_LOGOUT" == "$LOGOUT_URL" ]]; then
    echo -e "\033[0;32m✅ Azure AD app already configured correctly for $CUSTOM_DOMAIN\033[0m"
    echo -e "\033[0;36mNo updates needed.\033[0m"
    exit 0
fi

# Create parameters JSON file for Bicep deployment
PARAMS_FILE=$(mktemp /tmp/entra-params-XXXXXX.json)
trap "rm -f $PARAMS_FILE" EXIT

# Create the parameters JSON
cat > "$PARAMS_FILE" <<EOF
{
  "appUniqueName": {
    "value": "$APP_UNIQUE_NAME"
  },
  "redirectUris": {
    "value": $ALL_URIS
  },
  "homepageUrl": {
    "value": "$HOMEPAGE_URL"
  },
  "logoutUrl": {
    "value": "$LOGOUT_URL"
  }
}
EOF

# Deploy Bicep template to update Entra ID app
echo -e "\033[0;36mDeploying Entra ID app updates via Bicep...\033[0m"

DEPLOYMENT_NAME="entra-app-$(date +%Y%m%d%H%M%S)"
TEMPLATE_FILE="$SCRIPT_DIR/../infra/app/entra-app.bicep"

# Check if template file exists
if [[ ! -f "$TEMPLATE_FILE" ]]; then
    echo -e "\033[0;31mBicep template not found at: $TEMPLATE_FILE\033[0m" >&2
    exit 1
fi

# Deploy at tenant scope (for Microsoft.Graph resources)
if az deployment tenant create \
    --name "$DEPLOYMENT_NAME" \
    --location "eastus2" \
    --template-file "$TEMPLATE_FILE" \
    --parameters "$PARAMS_FILE" 2>&1; then
    
    echo -e "\033[0;32m✅ Successfully updated Azure AD app with custom domain redirect URIs!\033[0m"
    
    # Display final redirect URIs
    echo -e "\n\033[0;36mFinal redirect URIs:\033[0m"
    echo "$ALL_URIS" | jq -r '.[] | "  - \(.)"'
    echo -e "\033[0;36mHomepage URL: $HOMEPAGE_URL\033[0m"
    echo -e "\033[0;36mLogout URL: $LOGOUT_URL\033[0m"
else
    echo -e "\033[0;31mFailed to update Azure AD app. Check the error messages above.\033[0m" >&2
    exit 1
fi