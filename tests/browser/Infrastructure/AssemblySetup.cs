using AdvisorCoachStudio.BrowserTests.Infrastructure;

// One browser process per microphone mode and one application host per flavour are shared by every test.
[assembly: AssemblyFixture(typeof(BrowserFixture))]
[assembly: AssemblyFixture(typeof(SimulationAppHost))]
[assembly: AssemblyFixture(typeof(AzureAppHost))]

// Live audio sessions are timing sensitive (real-time capture and playback); running them one at a time keeps the
// suite deterministic on small CI runners.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
