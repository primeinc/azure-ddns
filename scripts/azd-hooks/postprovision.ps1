#!/usr/bin/env pwsh
<#
.SYNOPSIS
Post-provision hook for azd to update Azure AD redirect URIs when custom domain is configured

.DESCRIPTION
This hook runs after infrastructure provisioning to update the Azure AD app registration
with custom domain redirect URIs if a custom domain has been configured.
#>

Write-Host "Running post-provision hook..." -ForegroundColor Cyan

# Check if custom domain is configured
$envValues = azd env get-values | ConvertFrom-StringData
$customDomain = $envValues.customDomainName -replace '"', ''

if ([string]::IsNullOrEmpty($customDomain)) {
    Write-Host "No custom domain configured. Skipping Azure AD updates." -ForegroundColor Yellow
    exit 0
}

Write-Host "Custom domain detected: $customDomain" -ForegroundColor Green
Write-Host "Updating Azure AD app registration..." -ForegroundColor Cyan

# Call the update script
$scriptPath = Join-Path $PSScriptRoot "..\update-entra-redirects.ps1"
if (Test-Path $scriptPath) {
    & $scriptPath -CustomDomain $customDomain
} else {
    Write-Warning "Update script not found at: $scriptPath"
    exit 1
}

Write-Host "Post-provision hook completed." -ForegroundColor Green