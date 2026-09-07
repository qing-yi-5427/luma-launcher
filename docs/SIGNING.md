# Windows code signing

Luma is **not** Authenticode-signed by default. SmartScreen may warn on first run of a downloaded EXE even when the hash matches the release notes.

## Recommended path: SignPath (free for open source)

1. Create an account at [signpath.io](https://signpath.io) and add this GitHub organization/user.
2. Create a project bound to `qing-yi-5427/luma-launcher`.
3. Create a **Release Signing** policy (OV or free OSS certificate).
4. Add repository secrets:
   - `SIGNPATH_ORG_ID`
   - `SIGNPATH_PROJECT` (project slug)
5. Uncomment the SignPath step in `.github/workflows/release.yml` and fill the organization/project/policy GUIDs.

Until then, always publish the SHA-256 next to `Luma.exe` and document the verification command:

```powershell
Get-FileHash .\Luma.exe -Algorithm SHA256
```

## What Luma will never do automatically

- Silently replace a running executable
- Elevate without an explicit user action
- Download and execute a payload without hash verification (when a `.sha256` companion asset exists)
