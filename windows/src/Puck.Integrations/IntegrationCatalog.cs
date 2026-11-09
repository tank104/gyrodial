using Puck.Core;
using Puck.Integrations.Claude;
using Puck.Integrations.ClaudeCode;
using Puck.Integrations.DialKit;
using Puck.Integrations.Teams;

namespace Puck.Integrations;

/// <summary>Every integration, in the order Settings lists them. The app starts each one.</summary>
public static class IntegrationCatalog
{
    public static IReadOnlyList<IPuckIntegration> CreateAll() =>
    [
        new ClaudeIntegration(),
        new ClaudeCodeIntegration(),
        new DialKitIntegration(),
        new TeamsIntegration(),
    ];
}
