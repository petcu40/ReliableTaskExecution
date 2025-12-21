<#
.SYNOPSIS
    Removes Azure resources for the Distributed Task Execution System.

.DESCRIPTION
    This script removes the entire resource group 'cristp-reltaskexec' and all resources within it.
    This includes the SQL Server, SQL Database, and any other resources in the group.

.PARAMETER SubscriptionId
    The Azure subscription ID where resources will be removed. This is a mandatory parameter.

.PARAMETER Force
    Skip the confirmation prompt and proceed with deletion immediately.

.EXAMPLE
    .\Remove-AzureResources.ps1 -SubscriptionId "your-subscription-id-here"

.EXAMPLE
    .\Remove-AzureResources.ps1 -SubscriptionId "your-subscription-id-here" -Force

.NOTES
    Requires Az PowerShell module installed and authenticated.
    Run 'Connect-AzAccount' before executing this script.
    WARNING: This action is irreversible and will delete all resources in the resource group.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true, HelpMessage = "Azure subscription ID")]
    [ValidateNotNullOrEmpty()]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $false, HelpMessage = "Skip confirmation prompt")]
    [switch]$Force
)

# Configuration
$ResourceGroupName = "cristp-reltaskexec"

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

function Write-WarningMessage {
    param([string]$Message)
    Write-Host "[WARNING] $Message" -ForegroundColor Magenta
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

    # Step 3: Check if resource group exists
    Write-Step "Step 3: Checking resource group existence"

    $existingRg = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue
    if (-not $existingRg) {
        Write-Info "Resource group '$ResourceGroupName' does not exist. Nothing to remove."
        Write-Success "Cleanup complete (no resources found)"
        exit 0
    }

    Write-Info "Resource group '$ResourceGroupName' found in '$($existingRg.Location)'"

    # Step 4: List resources that will be deleted
    Write-Step "Step 4: Resources to be deleted"

    $resources = Get-AzResource -ResourceGroupName $ResourceGroupName
    if ($resources -and $resources.Count -gt 0) {
        Write-Host "The following resources will be deleted:" -ForegroundColor Yellow
        Write-Host ""
        foreach ($resource in $resources) {
            Write-Host "  - $($resource.ResourceType): $($resource.Name)" -ForegroundColor White
        }
        Write-Host ""
        Write-Host "Total resources: $($resources.Count)" -ForegroundColor Yellow
    }
    else {
        Write-Info "No resources found in resource group (empty group)"
    }

    # Step 5: Confirmation prompt
    Write-Step "Step 5: Confirmation"

    if (-not $Force) {
        Write-Host "WARNING: This action will permanently delete the resource group '$ResourceGroupName'" -ForegroundColor Red
        Write-Host "         and ALL resources within it. This action cannot be undone." -ForegroundColor Red
        Write-Host ""

        $confirmation = Read-Host "Type 'DELETE' to confirm deletion, or any other key to cancel"

        if ($confirmation -ne "DELETE") {
            Write-Info "Deletion cancelled by user"
            exit 0
        }
    }
    else {
        Write-WarningMessage "Force flag specified - skipping confirmation prompt"
    }

    # Step 6: Delete the resource group
    Write-Step "Step 6: Deleting resource group"

    Write-Info "Deleting resource group '$ResourceGroupName'..."
    Write-Info "This may take several minutes..."

    Remove-AzResourceGroup -Name $ResourceGroupName -Force | Out-Null

    Write-Success "Resource group '$ResourceGroupName' has been deleted"

    # Step 7: Verify deletion
    Write-Step "Step 7: Verifying deletion"

    $verifyRg = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue
    if ($verifyRg) {
        Write-WarningMessage "Resource group may still be in the process of deletion"
        Write-Info "Azure resource group deletion can take a few minutes to complete"
    }
    else {
        Write-Success "Verified: Resource group '$ResourceGroupName' no longer exists"
    }

    # Step 8: Output Summary
    Write-Step "Cleanup Complete"

    Write-Host "Summary:" -ForegroundColor White
    Write-Host "  Resource Group Deleted: $ResourceGroupName" -ForegroundColor White
    Write-Host "  Resources Removed:      $($resources.Count)" -ForegroundColor White
    Write-Host ""
    Write-Success "All resources have been successfully removed"

    return @{
        ResourceGroupName = $ResourceGroupName
        ResourcesRemoved = $resources.Count
        Success = $true
    }
}
catch {
    Write-Host "`n[ERROR] An error occurred during resource cleanup:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "`nStack trace:" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
