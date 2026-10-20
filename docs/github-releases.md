# GitHub application releases

Run these maintainer commands in PowerShell 7 from the selected release checkout.

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
timestamp before finishing publication.
Missing certificates, token/PIN problems, timestamp failures and verification
failures stop the script. Distribute only the EXEs from a successful run.

`-Sign` requires a configured certificate and fails before building if absent.
Supplying a thumbprint (argument or environment) also enables signing. Without
either, the script explicitly reports an unsigned development build. To make an
unsigned development build despite an existing environment setting, pass
`-SigningCertificateThumbprint ''` and omit `-Sign`.

Initial Certum card signing remains pending certificate approval and card
arrival. Once available, publish with `-Sign` and check the published EXEs:
`Get-AuthenticodeSignature` reports `Valid`, the expected signer and a nonempty
`TimeStamperCertificate`.
Then run the required application workflows on the available machines and record
clean Windows 10/11 coverage separately. Simulated signing tests do not qualify
the certificate, token, timestamp service or downloaded application behavior.

References: [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool),
[Certum signing instructions](https://files.certum.eu/documents/manual_en/Signing_with_the_use_of_jarsigner_tool_and_signtool.pdf).
