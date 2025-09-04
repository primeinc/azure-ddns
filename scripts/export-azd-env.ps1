# Export azd environment variables to current shell
# Usage: . .\scripts\export-azd-env.ps1

Write-Host "Exporting azd environment variables..." -ForegroundColor Cyan

# Get all azd environment values
$envValues = azd env get-values

# Parse and set each value as environment variable
foreach ($line in $envValues -split "`n") {
    if ($line -match '^([A-Z_]+)=(.*)$') {
        $name = $Matches[1]
        $value = $Matches[2].Trim('"')
        
        # Set environment variable
        Set-Item -Path "Env:$name" -Value $value
        Write-Host "  Set $name" -ForegroundColor Gray
    }
}

Write-Host "Environment variables exported successfully!" -ForegroundColor Green