using Azure.Core;
using Azure.Identity;

namespace LoanUnderwriting.Infrastructure;

/// <summary>
/// Picks the Azure credential without the slow DefaultAzureCredential fallthrough: managed identity when the host
/// provides one (App Service, Container Apps, Functions), otherwise the Azure CLI login (<c>az login</c>).
/// </summary>
internal static class AzureCredentials
{
    public static TokenCredential Create()
    {
        var hosted = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
                     || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
        return hosted ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned) : new AzureCliCredential();
    }
}
