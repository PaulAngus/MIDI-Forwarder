# MIDI Forwarder project guidance

## Local builds, signing and tests

- Run builds, tests, signing and release packaging locally on the appropriate development machine.
- GitHub Actions is disabled by the maintainer's choice. Do not add, enable, run or suggest GitHub Actions workflows.
- Keep the existing local build, publish and test entry points. Use the README and release documentation for commands; GitHub is used for source control and release downloads.
- Preserve test-host permissions and audited offline profiles. Native MIDI and hardware acceptance remain separate from offline tests and packaging checks.
