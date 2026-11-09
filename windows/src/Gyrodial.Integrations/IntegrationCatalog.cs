using Gyrodial.Core;
using Gyrodial.Integrations.Claude;
using Gyrodial.Integrations.ClaudeCode;
using Gyrodial.Integrations.DialKit;
using Gyrodial.Integrations.Teams;

namespace Gyrodial.Integrations;

/// <summary>Every integration, in the order Settings lists them. The app starts each one.</summary>
public static class IntegrationCatalog
{
    public static IReadOnlyList<IDialIntegration> CreateAll() =>
    [
        new ClaudeIntegration(),
        new ClaudeCodeIntegration(),
        new DialKitIntegration(),
        new TeamsIntegration(),
    ];
}
