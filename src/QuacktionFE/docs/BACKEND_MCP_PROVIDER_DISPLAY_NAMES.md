# MCP connection names incorrectly say Google for other providers

## Confirmed cause

In `src/Quacktion.Data/Agents/McpConnectionService.cs`, `CompleteConnectAsync` creates every connection with:

```csharp
DisplayName = "Google connection " + id[..6]
```

The provider is saved separately from `tx.Provider`. `ToInfo` returns the saved display name unchanged. Consequently a GitHub connection can have a correct `provider` field but a generated Google display name.

The frontend previously rendered `displayName` unchanged in the connection list, account selection, and tool management views.

## Frontend correction

The app now corrects the exact generated `Google connection <six lowercase hex characters>` format (and bare `Google connection`) using the actual provider field. Google remains Google; GitHub and Jira use their proper labels. Custom names and account labels remain unchanged.

This is a presentation correction only. IDs, provider values, enabled services, tools, and authorization requests remain unchanged. The app does not infer account identity from a grant ID.

## Proposed backend fix

- Generate the default name from the validated provider, with Google / GitHub / Jira labels and the existing short connection suffix.
- Consider a targeted migration or response normalization for existing records with the generated legacy label. Preserve custom names and verified account identities.
- Do not change credentials, connection IDs, or approvals to fix naming.

## Verification

- Connect Google and GitHub and assert that each response has a provider-appropriate default display name.
- Check existing generated GitHub / Jira names after the chosen legacy-record fix.
- Verify custom display names remain intact and account identity is not invented.

No backend files were edited for this investigation or frontend correction.
