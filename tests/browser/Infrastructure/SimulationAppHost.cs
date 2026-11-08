namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Host flavour (a): Ai:Mode=DevelopmentSimulation. The product's in-memory GA simulator answers with a synthetic
/// client voice and scripted text, detects advisor speech with an energy gate, and produces simulated reports.
/// </summary>
public sealed class SimulationAppHost : AppHost
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
    [
        new("Ai:Mode", "DevelopmentSimulation"),
    ];
}
