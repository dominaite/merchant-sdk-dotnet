# Contributing

Thanks for helping. This file lists the rules a pull request must follow.

## Run the tests

```sh
dotnet test tests/Dominaite.MerchantSdk.Tests -c Release
```

This is the command CI runs. It builds the library and the test project in Release and runs
every test. Install the .NET 8 and .NET 10 SDKs, like CI does: the library targets `net8.0` and
the test project targets `net10.0`.

## Runtime floor

The library targets `net8.0`. Do not use an API that .NET 8 does not have in `src/`. The library
has no package dependencies: `System.Text.Json` and `HMACSHA256` are in the box on .NET 8. Keep
it that way.

## Test fixtures

- `tests/Dominaite.MerchantSdk.Tests/merchant-api-contract.json` is the shared response
  contract. Every Dominaite SDK carries the same bytes. Do not edit it in a pull request here.
  It changes only after the API changes.
- `tests/Dominaite.MerchantSdk.Tests/merchant-api-wire-contract.json` is generated from the
  live production contract by `.github/workflows/contract-drift.yml`. Do not edit it by hand.
  When the drift check fails, the API moved: update the SDK first, then regenerate the fixture
  with the jq projection in that workflow.
- When a fixture test fails, fix the SDK, not the fixture.

## Tests with every change

Every change needs a test. Test what a caller gets back, not how the code works inside. A bug
fix needs a test that fails on the old code.

## Versions

The SDK is 0.x on purpose. A minor release (0.6 to 0.7) can contain a breaking change. A
breaking change goes in `CHANGELOG.md` under the next unreleased minor version. Do not change
`<Version>` in `src/Dominaite.MerchantSdk/Dominaite.MerchantSdk.csproj` in a pull request.

## Releases

The maintainer tags releases. A pull request never creates a tag, a GitHub release or a NuGet
package.

## Security

Do not report a security problem in a public issue or pull request. See
[SECURITY.md](SECURITY.md).
