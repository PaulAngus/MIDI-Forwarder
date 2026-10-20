# GitHub application releases

Build the selected release commit from a clean checkout using PowerShell 7.
Use a short checkout path on Windows: the existing Windows entry point still
places restore/build files beneath the checkout and can fail in deeply nested
directories. The shared Mac cross-publish helper's temporary-path fix does not
change the Windows entry point.
On Windows, run:

```powershell
./tools/Publish-Windows.ps1
```

The normal script creates separate standalone client and server EXEs and wraps
each with dependency notices:

- `publish/client/MIDI-Forwarder-Client-<version>-win-x64.zip`
- `publish/server/MIDI-Forwarder-Server-<version>-win-x64.zip`

Each ZIP has a `.zip.sha256` companion. Users extract the ZIP and run the EXE.

On the signing Mac:

```powershell
./tools/Publish-Mac.ps1 -SigningIdentity 'Developer ID Application: YOUR NAME (TEAMID)' -NotaryProfile 'YOUR-KEYCHAIN-PROFILE'
```

This prints the exact paths to `MIDI-Forwarder-Client-<version>-osx-arm64.zip`
and `MIDI-Forwarder-Server-<version>-osx-arm64.zip` under `publish/macos-arm64/`,
with `.zip.sha256` companions. The current Forwarder Mac publish target is
Apple Silicon; this procedure adds no Intel qualification.
The ZIPs preserve complete `.app` bundles, symbolic links and permissions.
Users extract and copy the `.app` to Applications. ZIP is the chosen format;
a DMG is not required. Windows cross-built Mac payloads are signing inputs,
not public release assets.

## Windows signing

On the Windows build PC, install the Windows SDK signing tools and Certum card
software, activate/register the approved certificate in `Cert:\CurrentUser\My`,
and connect the reader/card. These tools are not application-user dependencies.
List available certificates without exporting the private key:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey
$env:MBU_WINDOWS_SIGNING_THUMBPRINT = 'YOUR-EXACT-40-CHARACTER-THUMBPRINT'
./tools/Publish-Windows.ps1 -Sign
```

The card software may display a PIN prompt. Enter the PIN there only; never put
it or a private key in scripts, environment variables, Git or chat. SignTool is
discovered on PATH or in the installed Windows SDK. Override its location with
`-SignToolPath 'C:/full/path/to/signtool.exe'` or `MBU_SIGNTOOL_PATH` if necessary.

The publisher signs the final EXE with SHA-256 and an RFC 3161 SHA-256 timestamp
(default `http://time.certum.pl`, configurable with `-TimestampServer`). It checks
SignTool verification, Windows trust, the exact signer and the presence of a
timestamp before ZIP/checksum creation.
Missing certificates, token/PIN problems, timestamp failures and verification
failures stop the script. Upload only the ZIPs/checksums from a successful run.

`-Sign` requires a configured certificate and fails before building if absent.
Supplying a thumbprint (argument or environment) also enables signing. Without
either, the script explicitly reports an unsigned development build. To make an
unsigned development build despite an existing environment setting, pass
`-SigningCertificateThumbprint ''` and omit `-Sign`.

Initial Certum card signing remains pending certificate approval and card
arrival. Once available, publish with `-Sign`, extract each new ZIP and check
`Get-AuthenticodeSignature` reports `Valid`, the expected signer and a nonempty
`TimeStamperCertificate`; verify the ZIP SHA-256 sidecar against `Get-FileHash`.
Then run the required application workflows on the available machines and record
clean Windows 10/11 coverage separately. Simulated signing tests do not qualify
the certificate, token, timestamp service or downloaded application behavior.

References: [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool),
[Certum signing instructions](https://files.certum.eu/documents/manual_en/Signing_with_the_use_of_jarsigner_tool_and_signtool.pdf).

## Upload

Create a draft release for an existing tag pointing to the tested commit.
Set `$tag` to that tag and `$archive` to one exact ZIP path, then:

```powershell
gh release create $tag --repo PaulAngus/MIDI-Forwarder --verify-tag --draft --title $tag --notes-file release-notes.txt
gh release upload $tag $archive "$archive.sha256" --repo PaulAngus/MIDI-Forwarder
```

Create the draft once and repeat the upload command for the other three ZIPs.
Use exact paths, not wildcards that include older builds. Do not use `--clobber`
to silently replace released files. Download the assets, check their hashes,
extract them, and run the required first-launch, settings, discovery, virtual
interface and two-way communication tests before publishing the draft. Record
missing clean-machine coverage; VM packaging checks do not qualify real devices.
Keep Windows 10's lack of native virtual MIDI presentation explicit.
GitHub's automatic source ZIPs are not application downloads.

## Credentials

Creating the ZIPs requires no developer key. Public Mac releases need a Developer
ID Application certificate/private key and notarization credentials installed
locally in the signing Mac's keychain. Pass identity/profile names only; never
send or commit private keys. Default ad hoc builds are for lab testing.
Windows signing requires its own certificate, not an Apple developer key.
ZIP compression does not bypass browser or OS security checks.
Uploading needs GitHub authentication on the maintainer machine; the publish
scripts do not upload automatically.

References: [GitHub release creation](https://cli.github.com/manual/gh_release_create),
[asset upload](https://cli.github.com/manual/gh_release_upload),
[Apple distribution](https://developer.apple.com/developer-id/).
