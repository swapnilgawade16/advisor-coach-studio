# Contributing

Thanks for your interest in improving Advisor Coach Studio!

## Ground rules

- Read [advisor_coach_studio_design.md](advisor_coach_studio_design.md) first. Changes that alter the transport
  (WebSockets only, no WebRTC), add persistent storage, expose credentials to the browser, or add excluded features
  (RAG, CRM integrations, financial recommendations, audio recording) need a design discussion in an issue first.
- Never commit secrets, real client data, or real conversations. Test fixtures must be synthetic.
- Never log transcript text, prompts, audio, keys, or tokens.
- Keep the UI accessible (WCAG 2.2 AA): keyboard support, visible focus, labels, and restrained live regions.

## Development setup

```bash
dotnet --version            # 10.0.403 or a later 10.0.4xx patch (see global.json)
dotnet build
dotnet run --project src/AdvisorCoachStudio.Web --launch-profile simulation   # no Azure needed
```

## Tests

```bash
dotnet test tests/AdvisorCoachStudio.UnitTests
dotnet test tests/AdvisorCoachStudio.IntegrationTests
dotnet test tests/browser          # requires a Chromium-based browser; see tests/browser/README.md
```

Build warnings are treated as errors. Add or update tests with every behavioural change; protocol changes need fixtures
and integration coverage against the fake provider.

## Pull requests

1. Fork and create a topic branch.
2. Keep changes focused and update documentation (`README.md`, `docs/`) when behaviour or configuration changes.
3. Ensure `dotnet build` and the unit/integration tests pass.
4. Describe what you changed, how you tested it, and whether it was verified against live Azure or only the fake provider.

## Adding a scenario

Scenarios are versioned JSON files in `src/AdvisorCoachStudio.Web/Scenarios`. Copy an existing file, give it a new
lowercase slug `id` and `version`, and fill every field. The catalogue is validated at startup (fictional facts only:
no emails, URLs, account-like numbers, amounts, or percentages; every hidden fact needs a disclosure condition; all
three difficulty levels are required). Add the new id to `ScenarioCatalog.OrderKey` if you want a specific landing-page
position.
