# MIDI Forwarder Test Lab adapter

Build on a development PC with .NET 10 and Python:

```
python packaging/build_test_lab.py --rid win-x64 --output artifacts/test-lab
python packaging/build_test_lab.py --rid osx-arm64 --output artifacts/test-lab
```

The build uses the pinned agent in `dependencies/test-lab`, and produces a portable ZIP, SHA-256, immutable file manifest and source identity. Each extracted bundle is independent of this checkout. Test machines need no SDK, separately installed runtime, Git or Python. The optional `--connection` argument includes only the public dashboard connection JSON. Use a fresh output folder for each build.

On the development Mac, extract locally and run `bash Prepare-on-Mac-DEV.command` to sign, reseal and check the bundle before distributing it. This is ad hoc signing; Developer ID/notarization remains separate. Native execution still needs validation on each target OS.

Copy the matching bundle to each machine, copy `dashboard-connection.json` into its root, open Pair-with-Dashboard, enter a dashboard code, then Start-Agent. Pairing is per application and OS user; upgrading a bundle retains it. The application ID is `midi-forwarder`. The runner accepts `run --root ROOT --suite Functional --result-directory ROOT/results/RUN --no-open` and the standard All, Native and Desktop suites.

Functional wraps the existing offline .NET tests and retains case counts and logs. Hardware-category tests are excluded even if hardware opt-in environment variables are present. Skipped or empty coverage cannot pass. Native reports incomplete: Two-machine client/server routing and native MIDI loopback need a dedicated coordinated adapter; offline transport tests do not qualify them. Desktop reports incomplete when explicitly requested; All also includes that missing desktop coverage.

macOS currently runs CoreMidi tests; Windows additionally runs Core, Client and Server. An All run cannot pass while required native/desktop coverage is missing. Synthetic tests do not establish physical device qualification.

All runners, including direct local runs, hold a machine-wide resource lease throughout test execution. Other applications wait and their agents continue heartbeats. Upgrade Preset Sender and the dashboard to agent 0.2.0 before mixing applications. Permission errors accessing the global lease stop the run; they never bypass it.

On macOS, Native also runs the three existing CoreMIDI virtual-port round trips. These are separate from Functional. The remaining native/two-machine gaps still make All incomplete.
