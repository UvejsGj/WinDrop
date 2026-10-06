## What and why

<!-- What this changes, and why. If it rests on how Apple's implementation behaves, say how that was observed. -->

## How it was tested

<!-- For example: dotnet test (N passing); checked against opendrop, plistlib or bsdtar; or a device test with the device, OS version and result. -->

## Checklist

- [ ] `dotnet test` passes
- [ ] New behaviour has a test that fails without the change
- [ ] Protocol findings are recorded in `docs/protocol-notes.md`, dated, with their evidence
- [ ] Consent, path checks and resource limits are untouched, or the change is tested against them (see `SECURITY.md`)
- [ ] Logs, captures and notes are redacted: no device names, MAC addresses, UUIDs or IP addresses
