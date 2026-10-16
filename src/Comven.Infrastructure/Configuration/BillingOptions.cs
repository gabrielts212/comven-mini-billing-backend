namespace Comven.Infrastructure.Configuration;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public bool UseSeedData { get; set; } = true;

    public int ProviderTimeoutSeconds { get; set; } = 3;
}