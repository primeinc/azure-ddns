#!/usr/bin/env bash

# Post-provision hook for azd to update Azure AD redirect URIs when custom domain is configured
#
# This hook runs after infrastructure provisioning to update the Azure AD app registration
# with custom domain redirect URIs if a custom domain has been configured.

set -euo pipefail

echo -e "\033[0;36mRunning post-provision hook...\033[0m"

# Check if custom domain is configured
# Parse azd env get-values output to extract customDomainName
custom_domain=""
if command -v azd &> /dev/null; then
    # Get environment values and parse for customDomainName
    # The output format is KEY="VALUE" or KEY=VALUE
    while IFS='=' read -r key value; do
        if [[ "$key" == "customDomainName" ]]; then
            # Remove quotes if present
            custom_domain="${value//\"/}"
            break
        fi
    done < <(azd env get-values 2>/dev/null || true)
fi

if [[ -z "$custom_domain" ]]; then
    echo -e "\033[0;33mNo custom domain configured. Skipping Azure AD updates.\033[0m"
    exit 0
fi

echo -e "\033[0;32mCustom domain detected: $custom_domain\033[0m"
echo -e "\033[0;36mUpdating Azure AD app registration...\033[0m"

# Call the update script
# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UPDATE_SCRIPT="${SCRIPT_DIR}/../update-entra-redirects.sh"

if [[ -f "$UPDATE_SCRIPT" ]]; then
    # Execute the update script with custom domain parameter
    bash "$UPDATE_SCRIPT" --custom-domain "$custom_domain"
else
    echo -e "\033[0;31mUpdate script not found at: $UPDATE_SCRIPT\033[0m" >&2
    exit 1
fi

echo -e "\033[0;32mPost-provision hook completed.\033[0m"