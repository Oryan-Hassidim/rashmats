# Run Schemathesis contract & property-based tests against the live Rashmatz OpenAPI spec.
# Prerequisites:
#   1. Start the server:  dotnet run --project src/Rashmatz.Server.fsproj
#   2. Install schemathesis (if needed):  uv tool install schemathesis   (or: pip install schemathesis)

param(
    [string]$BaseUrl = "http://localhost:5000",
    [string]$AdminEmail = "admin@rashmatz.org"
)

$OpenApiUrl = "$BaseUrl/openapi/v1.json"
Write-Host "Running Schemathesis against $OpenApiUrl ..." -ForegroundColor Cyan

schemathesis run $OpenApiUrl `
    --url $BaseUrl `
    -H "X-Dev-User-Email: $AdminEmail" `
    --checks all `
    --max-examples 20
