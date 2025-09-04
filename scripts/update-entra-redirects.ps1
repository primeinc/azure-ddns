#!/usr/bin/env pwsh
<#
.SYNOPSIS
Updates Azure AD app redirect URIs with custom domain while preserving existing URIs

.DESCRIPTION
This script fetches current redirect URIs from an Azure AD app, adds the new custom domain
redirect URI if not present, and deploys the Entra ID Bicep template to update the app.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$AppId = "3109db3d-f7e7-4d55-ae7d-ac2170d1335c",
    
    [Parameter(Mandatory=$false)]
    [string]$CustomDomain = "",
    
    [Parameter(Mandatory=$false)]
    [string]$Environment = "azure-ddns-dev"
)

# If no custom domain provided, try to get from azd env
if ([string]::IsNullOrEmpty($CustomDomain)) {
    $envValues = azd env get-values --cwd $PSScriptRoot/.. | ConvertFrom-StringData
    $CustomDomain = $envValues.customDomainName
    # Remove any quotes that might be in the environment variable
    $CustomDomain = $CustomDomain -replace '"', ''
}

if ([string]::IsNullOrEmpty($CustomDomain)) {
    Write-Host "No custom domain configured. Skipping Entra ID update." -ForegroundColor Yellow
    exit 0
}

Write-Host "Updating Azure AD app redirect URIs for custom domain: $CustomDomain" -ForegroundColor Green

# Fetch app details including unique name
Write-Host "Fetching existing app details..." -ForegroundColor Cyan
$appDetails = az ad app show --id $AppId -o json | ConvertFrom-Json

if ($null -eq $appDetails) {
    Write-Error "Could not find Azure AD app with ID: $AppId"
    exit 1
}

# Get the unique name (usually same as appId for apps created via portal)
# For Graph API, we need the app's object ID or unique name
$appUniqueName = if ($appDetails.uniqueName) { $appDetails.uniqueName } else { $AppId }

$existingUris = $appDetails.web.redirectUris
if ($null -eq $existingUris) {
    $existingUris = @()
}

# Build new URIs to add
$newUris = @(
    "https://$CustomDomain/.auth/login/aad/callback"
)

# Merge URIs (avoid duplicates)
$allUris = [System.Collections.ArrayList]::new()
foreach ($uri in $existingUris) {
    [void]$allUris.Add($uri)
}

foreach ($uri in $newUris) {
    if ($allUris -notcontains $uri) {
        Write-Host "Adding new redirect URI: $uri" -ForegroundColor Green
        [void]$allUris.Add($uri)
    } else {
        Write-Host "Redirect URI already exists: $uri" -ForegroundColor Yellow
    }
}

# Build homepage and logout URLs
$homepageUrl = "https://$CustomDomain/"
$logoutUrl = "https://$CustomDomain/.auth/logout"

# Convert array to JSON for Bicep parameter (ensure proper array format)
if ($allUris.Count -eq 1) {
    # Single item needs to be wrapped in array syntax
    $redirectUrisJson = "[`"$($allUris[0])`"]"
} else {
    $redirectUrisJson = $allUris | ConvertTo-Json -Compress
}

# Deploy Bicep template to update Entra ID app
Write-Host "Deploying Entra ID app updates via Bicep..." -ForegroundColor Cyan

$deploymentName = "entra-app-$(Get-Date -Format 'yyyyMMddHHmmss')"
$templateFile = Join-Path $PSScriptRoot "..\infra\app\entra-app.bicep"

# Write parameters to a JSON file for cleaner deployment
$paramsFile = Join-Path $env:TEMP "entra-params-$(Get-Date -Format 'yyyyMMddHHmmss').json"
$params = @{
    appUniqueName = @{ value = $appUniqueName }
    redirectUris = @{ value = $allUris }
    homepageUrl = @{ value = $homepageUrl }
    logoutUrl = @{ value = $logoutUrl }
} | ConvertTo-Json -Depth 10

$params | Out-File -FilePath $paramsFile -Encoding UTF8

# Deploy at tenant scope (for Microsoft.Graph resources)
az deployment tenant create `
    --name $deploymentName `
    --location "eastus2" `
    --template-file $templateFile `
    --parameters $paramsFile

# Clean up temp file
Remove-Item $paramsFile -Force -ErrorAction SilentlyContinue

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Successfully updated Azure AD app with custom domain redirect URIs!" -ForegroundColor Green
    
    # Display final redirect URIs
    Write-Host "`nFinal redirect URIs:" -ForegroundColor Cyan
    $allUris | ForEach-Object { Write-Host "  - $_" }
    Write-Host "Homepage URL: $homepageUrl" -ForegroundColor Cyan
    Write-Host "Logout URL: $logoutUrl" -ForegroundColor Cyan
} else {
    Write-Error "Failed to update Azure AD app. Check the error messages above."
    exit 1
}