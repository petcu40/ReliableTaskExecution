<#
.SYNOPSIS
    Creates Azure resources for the Distributed Task Execution System.

.DESCRIPTION
    This script creates the following Azure resources:
    - Resource Group: cristp-reltaskexec
    - SQL Server with Entra ID (Azure AD) only authentication
    - SQL Database: TaskExecutionDb (Basic tier)
    - Firewall rules for local development

.PARAMETER SubscriptionId
    The Azure subscription ID where resources will be created. This is a mandatory parameter.

.EXAMPLE
    .\Create-AzureResources.ps1 -SubscriptionId "your-subscription-id-here"

.NOTES
    Requires Az PowerShell module installed and authenticated.
    Run 'Connect-AzAccount' before executing this script.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = "Azure subscription ID")]
    [ValidateNotNullOrEmpty()]
    [string]$SubscriptionId
)

# Configuration
$ResourceGroupName = "cristp-reltaskexec"
$Location = "westus2"
$DatabaseName = "TaskExecutionDb"

# Generate unique SQL Server name using a short unique identifier
$UniqueId = [System.Guid]::NewGuid().ToString("N").Substring(0, 8)
$SqlServerName = "reltaskexec-$UniqueId"

# Error action preference
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[SUCCESS] $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "[INFO] $Message" -ForegroundColor Yellow
}

try {
    # Step 1: Verify Az module is installed
    Write-Step "Step 1: Verifying Az PowerShell module"

    if (-not (Get-Module -ListAvailable -Name Az.Accounts)) {
        throw "Az PowerShell module is not installed. Please install it using: Install-Module -Name Az -AllowClobber -Scope CurrentUser"
    }
    Write-Success "Az PowerShell module is available"

    # Step 2: Set the subscription context
    Write-Step "Step 2: Setting subscription context"

    Set-AzContext -SubscriptionId $SubscriptionId | Out-Null
    $context = Get-AzContext
    Write-Success "Subscription set to: $($context.Subscription.Name) ($($context.Subscription.Id))"

    # Get current user information for SQL Server admin
    $currentUser = Get-AzADUser -SignedIn
    if (-not $currentUser) {
        throw "Unable to get current signed-in user. Please ensure you are authenticated with 'Connect-AzAccount'."
    }
    Write-Info "Current user: $($currentUser.DisplayName) ($($currentUser.UserPrincipalName))"

    # Step 3: Create or verify Resource Group
    Write-Step "Step 3: Creating Resource Group"

    $existingRg = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue
    if ($existingRg) {
        Write-Info "Resource group '$ResourceGroupName' already exists in '$($existingRg.Location)'"
    }
    else {
        New-AzResourceGroup -Name $ResourceGroupName -Location $Location | Out-Null
        Write-Success "Resource group '$ResourceGroupName' created in '$Location'"
    }

    # Step 4: Create SQL Server with Entra ID only authentication
    Write-Step "Step 4: Creating SQL Server with Entra ID authentication"

    # Check if a SQL Server already exists in the resource group
    $existingServers = Get-AzSqlServer -ResourceGroupName $ResourceGroupName -ErrorAction SilentlyContinue
    if ($existingServers -and $existingServers.Count -gt 0) {
        $SqlServerName = $existingServers[0].ServerName
        Write-Info "SQL Server '$SqlServerName' already exists, skipping creation"
    }
    else {
        Write-Info "Creating SQL Server: $SqlServerName"
        Write-Info "This may take a few minutes..."

        # Create SQL Server with Azure AD-only authentication
        # Note: We use -EnableActiveDirectoryOnlyAuthentication to enforce Entra ID only
        New-AzSqlServer `
            -ResourceGroupName $ResourceGroupName `
            -ServerName $SqlServerName `
            -Location $Location `
            -ExternalAdminName $currentUser.DisplayName `
            -EnableActiveDirectoryOnlyAuthentication | Out-Null

        Write-Success "SQL Server '$SqlServerName' created with Entra ID-only authentication"
        Write-Success "SQL Server admin set to: $($currentUser.DisplayName)"
    }

    # Get the full SQL Server FQDN
    $sqlServer = Get-AzSqlServer -ResourceGroupName $ResourceGroupName -ServerName $SqlServerName
    $SqlServerFqdn = $sqlServer.FullyQualifiedDomainName
    Write-Info "SQL Server FQDN: $SqlServerFqdn"

    # Step 5: Configure Firewall Rules
    Write-Step "Step 5: Configuring Firewall Rules"

    # Allow Azure services
    $azureServicesRule = Get-AzSqlServerFirewallRule -ResourceGroupName $ResourceGroupName -ServerName $SqlServerName -FirewallRuleName "AllowAzureServices" -ErrorAction SilentlyContinue
    if (-not $azureServicesRule) {
        New-AzSqlServerFirewallRule `
            -ResourceGroupName $ResourceGroupName `
            -ServerName $SqlServerName `
            -FirewallRuleName "AllowAzureServices" `
            -StartIpAddress "0.0.0.0" `
            -EndIpAddress "0.0.0.0" | Out-Null
        Write-Success "Firewall rule 'AllowAzureServices' created"
    }
    else {
        Write-Info "Firewall rule 'AllowAzureServices' already exists"
    }

    # Get current public IP for local development
    try {
        $publicIp = (Invoke-RestMethod -Uri "https://api.ipify.org" -TimeoutSec 10)
        $localDevRule = Get-AzSqlServerFirewallRule -ResourceGroupName $ResourceGroupName -ServerName $SqlServerName -FirewallRuleName "LocalDevelopment" -ErrorAction SilentlyContinue
        if (-not $localDevRule) {
            New-AzSqlServerFirewallRule `
                -ResourceGroupName $ResourceGroupName `
                -ServerName $SqlServerName `
                -FirewallRuleName "LocalDevelopment" `
                -StartIpAddress $publicIp `
                -EndIpAddress $publicIp | Out-Null
            Write-Success "Firewall rule 'LocalDevelopment' created for IP: $publicIp"
        }
        else {
            # Update existing rule with current IP
            Set-AzSqlServerFirewallRule `
                -ResourceGroupName $ResourceGroupName `
                -ServerName $SqlServerName `
                -FirewallRuleName "LocalDevelopment" `
                -StartIpAddress $publicIp `
                -EndIpAddress $publicIp | Out-Null
            Write-Info "Firewall rule 'LocalDevelopment' updated for IP: $publicIp"
        }
    }
    catch {
        Write-Warning "Could not determine public IP for local development firewall rule. You may need to add it manually."
    }

    # Step 6: Create SQL Database
    Write-Step "Step 6: Creating SQL Database"

    $existingDb = Get-AzSqlDatabase -ResourceGroupName $ResourceGroupName -ServerName $SqlServerName -DatabaseName $DatabaseName -ErrorAction SilentlyContinue
    if ($existingDb -and $existingDb.DatabaseName -eq $DatabaseName) {
        Write-Info "Database '$DatabaseName' already exists"
    }
    else {
        Write-Info "Creating database: $DatabaseName (Basic tier)"
        Write-Info "This may take a few minutes..."

        New-AzSqlDatabase `
            -ResourceGroupName $ResourceGroupName `
            -ServerName $SqlServerName `
            -DatabaseName $DatabaseName `
            -Edition "Basic" `
            -RequestedServiceObjectiveName "Basic" `
            -MaxSizeBytes 2147483648 | Out-Null

        Write-Success "Database '$DatabaseName' created with Basic tier"
    }

    # Step 7: Output Summary
    Write-Step "Resource Creation Complete"

    Write-Host "Summary of created resources:" -ForegroundColor White
    Write-Host "  Resource Group:  $ResourceGroupName" -ForegroundColor White
    Write-Host "  Location:        $Location" -ForegroundColor White
    Write-Host "  SQL Server:      $SqlServerName" -ForegroundColor White
    Write-Host "  SQL Server FQDN: $SqlServerFqdn" -ForegroundColor White
    Write-Host "  Database:        $DatabaseName" -ForegroundColor White
    Write-Host "  SQL Admin:       $($currentUser.DisplayName) (Entra ID)" -ForegroundColor White
    Write-Host ""
    Write-Host "Connection string for appsettings.json:" -ForegroundColor Yellow
    Write-Host "  Server=$SqlServerFqdn;Database=$DatabaseName;Authentication=Active Directory Default;" -ForegroundColor Cyan
    Write-Host ""

    # Output values that can be captured programmatically
    $output = @{
        ResourceGroupName = $ResourceGroupName
        Location = $Location
        SqlServerName = $SqlServerName
        SqlServerFqdn = $SqlServerFqdn
        DatabaseName = $DatabaseName
        SqlAdminName = $currentUser.DisplayName
    }

    return $output
}
catch {
    Write-Host "`n[ERROR] An error occurred during resource creation:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "`nStack trace:" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
