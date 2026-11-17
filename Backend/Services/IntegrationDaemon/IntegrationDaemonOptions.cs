namespace Backend.Services.IntegrationDaemon;

public sealed class IntegrationDaemonOptions
{
    public const string SectionName = "IntegrationDaemon";

    /// <summary>When false, manual Smappee and Sigenergy refresh requests are rejected.</summary>
    public bool Enabled { get; set; } = true;
}
