"""Versioned development-only helper. Test machines use self-contained apphosts."""
import argparse
import hashlib
import json
import plistlib
from pathlib import Path
import shutil
import subprocess
import uuid
import zipfile


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def mac_application(directory, spec):
    """Bind the native .NET apphost to a sealed, private managed runtime."""
    executable = spec['executable']
    host = directory / executable
    data = host.read_bytes()
    old = (executable + '.dll\0').encode()
    binding = spec.get('runtimeFromExecutable', '../Resources/Runtime')
    new = (binding + '/' + executable + '.dll\0').encode()
    offset = data.index(old)
    if data[offset:offset + len(new)] != old + b'\0' * (len(new) - len(old)):
        raise ValueError('Unexpected .NET apphost binding layout')
    contents = directory.parent
    runtime = (directory / binding).resolve()
    if not runtime.is_relative_to(directory.parents[3]):
        raise ValueError('Mac runtime must remain inside the portable bundle')
    runtime.mkdir(parents=True)
    for path in directory.iterdir():
        if path != host:
            shutil.move(str(path), runtime / path.name)
    host.write_bytes(data[:offset] + new + data[offset + len(new):])
    (contents / 'Info.plist').write_bytes(plistlib.dumps(dict(
        CFBundleIdentifier=spec['identifier'], CFBundleName=spec['name'],
        CFBundleDisplayName=spec['name'], CFBundleExecutable=executable,
        CFBundlePackageType='APPL', CFBundleVersion='1', CFBundleShortVersionString='1.0',
        LSUIElement=spec.get('backgroundAgent', True), NSHighResolutionCapable=True)))
    # Signing fills this resource seal; include it in the portable manifest up front.
    (contents / '_CodeSignature').mkdir()
    (contents / '_CodeSignature/CodeResources').write_bytes(b'')


def build(root):
    parser = argparse.ArgumentParser()
    parser.add_argument('--rid', choices=['win-x64', 'osx-arm64', 'osx-x64'], required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--connection', type=Path)
    parser.add_argument('--agent-project', type=Path, help='Development-only source override')
    parser.add_argument('--config', type=Path, default=Path('packaging/test-lab.json'), help='Repository-owned build profile')
    parser.add_argument('--preserve-mac-bundle', type=Path, help='Upgrade using the exact approved Mac driver/application bytes; never publish or sign retained components')
    args = parser.parse_args()
    config = json.loads((root / args.config).read_text(encoding='utf-8'))
    platform = config['platforms'][args.rid.split('-')[0]]
    offline = config.get('testProfile') == 'offline-windows-no-midi'
    read_only = config.get('testProfile') == 'physical-windows-fm9-read-only'
    if (offline or read_only) and args.rid != 'win-x64':
        raise ValueError('The audited offline Windows profile requires win-x64')
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    bundle_id = config['applicationId'] + '-' + uuid.uuid4().hex[:16]
    staging = output / bundle_id
    staging.mkdir()
    retained = []
    if args.preserve_mac_bundle:
        if not args.rid.startswith('osx-'):
            raise ValueError('Mac component preservation requires a Mac target')
        previous = args.preserve_mac_bundle.resolve()
        old = json.loads((previous / 'bundle-manifest.json').read_text(encoding='utf-8'))
        if old.get('applicationId') != config['applicationId'] or old['runtimeId'] != args.rid:
            raise ValueError('Retained components must belong to the same application and runtime')
        retained = config.get('macUpgradeRetainedPaths', [])
        if not retained:
            raise ValueError('The profile must explicitly list its retained Mac components')
        for relative in retained:
            source = previous / relative
            if not source.resolve().is_relative_to(previous) or not source.is_dir():
                raise ValueError('Invalid retained Mac component: ' + relative)
            entries = {e['path']: e['sha256'] for e in old['files'] if e['path'].startswith(relative + '/')}
            actual = {p.relative_to(previous).as_posix(): digest(p) for p in source.rglob('*') if p.is_file()}
            if not entries or actual != entries:
                raise ValueError('Retained Mac component differs from its approved manifest: ' + relative)
            shutil.copytree(source, staging / relative)
    # A bundle never consumes another build's intermediate or compiled outputs.
    build_directory = output / (bundle_id + '-build')
    if build_directory.exists():
        raise ValueError('Clean build directory already exists')
    properties = list(platform.get('properties', []))
    if config.get('appVersionFile'):
        # Portable test bundles must not increment a product's release version.
        version_source = root / config['appVersionFile']
        version = version_source.read_text(encoding='utf-8-sig').strip()
        version_copy = output / (bundle_id + '-app-version.txt')
        version_copy.write_text(version + '\n', encoding='utf-8')
        properties += ['-p:AppVersionFile=' + str(version_copy), '-p:PublishVersion=' + version]
    if args.agent_project:
        properties += ['-p:TestLabAgentProject=' + str(args.agent_project.resolve())]
    for item in [config['runner'], *platform['tests']]:
        if any(item['destination'] == p or item['destination'].startswith(p + '/') for p in retained):
            continue  # Do not rebuild the pilot apphost, its runtime, or the retained application.
        command = ['dotnet', 'publish', item['project'], '-c', 'Release', '-r', args.rid,
                   '--self-contained', 'true', '-m:1', '-p:UseArtifactsOutput=true', '-p:PublishSingleFile=false', '-p:EnableMacOSCodeSign=false',
                   '--artifacts-path', str(build_directory),
                   '-o', str(staging / item['destination']), *properties]
        result = subprocess.run(command, cwd=root, capture_output=True, text=True)
        (output / (bundle_id + '-' + item['destination'].replace('/', '-') + '.log')).write_text(result.stdout + result.stderr, encoding='utf-8')
        if result.returncode:
            raise RuntimeError(result.stdout[-10000:] + result.stderr[-2000:])
        if item.get('macApplication'):
            mac_application(staging / item['destination'], item['macApplication'])
    revision = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=root, capture_output=True, text=True)
    status = subprocess.run(['git', 'status', '--porcelain'], cwd=root, capture_output=True, text=True, check=True).stdout
    # Uncommitted repositories still get exact build-input hashes, not an invented revision.
    paths = subprocess.run(['git', 'ls-files', '-co', '--exclude-standard', '-z'], cwd=root, capture_output=True, check=True).stdout.decode().split('\0')
    inputs = []
    for relative in sorted(set(paths)):
        path = root / relative
        if relative and path.is_file() and not any(p in {'.build', 'bin', 'obj', 'artifacts', 'TestResults', 'build_test'} for p in Path(relative).parts):
            inputs.append(dict(path=relative, sha256=digest(path)))
    identity = dict(applicationId=config['applicationId'], revision=revision.stdout.strip() if revision.returncode == 0 else None,
                    dirty=bool(status), inputs=inputs, agentVersion=config.get('agentVersion', '0.2.0'), developmentAgentOverride=bool(args.agent_project))
    identity['testProfile'] = config.get('testProfile', 'development')
    identity['cleanBuild'] = True
    identity['buildOutputs'] = 'Fresh per-bundle artifacts directory; no previous bin/obj outputs reused.'
    if retained:
        identity['retainedMacComponents'] = dict(bundleId=old['bundleId'], paths=retained,
            files=[dict(path=p.relative_to(staging).as_posix(), sha256=digest(p)) for p in sorted(staging.rglob('*'))
                   if p.is_file() and any(p.relative_to(staging).as_posix().startswith(r + '/') for r in retained)])
        identity['buildOutputs'] += ' Approved Mac components listed separately were copied byte-for-byte, without rebuilding or signing.'
    (staging / 'source-identity.json').write_text(json.dumps(identity, indent=2), encoding='utf-8')
    if offline or read_only:
        policy = config['offlinePolicy']
        if not policy['includedTestSources'] or policy.get('midiAccess') != 'none':
            raise ValueError('An offline profile must list its audited test sources and prohibit MIDI access')
        policy = {**policy, 'testProfile': config['testProfile'],
                  'auditedSources': [dict(path=p, sha256=digest(root / p)) for p in policy['includedTestSources']]}
        (staging / 'offline-policy.json').write_text(json.dumps(policy, indent=2), encoding='utf-8')
    if read_only:
        policy = config['hardwarePolicy']
        if policy['midiAccess'] != 'four-exact-fm9-read-queries' or not policy['includedSources']:
            raise ValueError('Read-only FM9 profiles require an explicit source audit')
        (staging / 'hardware-policy.json').write_text(json.dumps({**policy,
            'auditedSources': [dict(path=p, sha256=digest(root / p)) for p in policy['includedSources']]}, indent=2), encoding='utf-8')
    for source, destination in config.get('staticFiles', {}).items():
        target = staging / destination
        if not target.resolve().is_relative_to(staging) or target.exists():
            raise ValueError('Invalid static bundle file')
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / source, target)
    runner = config['runner']['executable']
    if args.rid.startswith('win-'):
        for name, mode in [('Pair-with-Dashboard-Windows.cmd', 'pair'), ('Start-Agent-Windows.cmd', 'agent'), ('Run-Windows.cmd', 'run --suite All')]:
            text = '@echo off\r\n"%~dp0' + runner.replace('/', '\\') + '.exe" ' + mode + ' --root "%~dp0." %*\r\npause\r\n'
            (staging / name).write_bytes(text.encode('utf-8'))
    else:
        for name, mode in [('Pair-with-Dashboard-Mac.command', 'pair'), ('Start-Agent-Mac.command', 'agent'), ('Run-Mac.command', 'run --suite All')]:
            text = '#!/bin/bash\nset -euo pipefail\ncd "$(dirname "$0")"\n"./' + runner + '" ' + mode + ' --root "$PWD" "$@"\n'
            (staging / name).write_text(text, encoding='utf-8', newline='\n')
        (staging / 'dotnet-jit.entitlements.plist').write_text('<?xml version="1.0"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>', encoding='utf-8')
        skip_retained = '\n'.join('    "' + p + '"|"' + p + '"/*) continue ;;' for p in retained)
        prepare = '''#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
while IFS= read -r -d '' file; do
  case "$file" in
SKIP_RETAINED
  esac
  if /usr/bin/file -b "$file" | /usr/bin/grep -q 'Mach-O'; then
    /bin/chmod u+x "$file"
    /usr/bin/codesign --force --sign - --entitlements dotnet-jit.entitlements.plist "$file"
    /usr/bin/codesign --verify --strict "$file"
  fi
done < <(/usr/bin/find bin -type f -print0)
while IFS= read -r -d '' app; do
  case "$app" in
SKIP_RETAINED
  esac
  /usr/bin/codesign --force --sign - --entitlements dotnet-jit.entitlements.plist "$app"
  /usr/bin/codesign --verify --deep --strict "$app"
done < <(/usr/bin/find bin -type d -name '*.app' -print0)
/bin/chmod u+x ./*.command
"./RUNNER" seal --root "$PWD"
"./RUNNER" check --root "$PWD"
'''.replace('RUNNER', runner).replace('SKIP_RETAINED', skip_retained or '    "") continue ;;')
        (staging / 'Prepare-on-Mac-DEV.command').write_text(prepare, encoding='utf-8', newline='\n')
    (staging / 'START-HERE.txt').write_text(config['applicationName'] + '\n\nCopy to a writable local folder. Do not run on the share.\n'
        + 'Mac: run bash Prepare-on-Mac-DEV.command on the development Mac before redistribution. This is ad hoc signing, not notarization.\n'
        + 'Copy dashboard-connection.json here, pair with a dashboard code, then start the agent.\n'
        + 'No .NET SDK/runtime or scripting installation is needed on test machines.\n'
        + ('All runs only the audited offline Functional stages. Native/Desktop requests report unavailable; neither hardware nor routing is qualified. No MIDI connections are opened or created. See offline-policy.json.\n'
           if offline else 'All includes required missing coverage as incomplete. Functional runs the existing offline checks.\n'
           + ('Physical profile: Native uses only the explicitly selected existing real FM9 connection; see report limits.\n'
              if config.get('testProfile', '').startswith('physical-') else 'See report limits. Hardware tests are never started by these bundles.\n')), encoding='utf-8')
    if args.connection:
        connection = json.loads(args.connection.read_text(encoding='utf-8-sig'))
        if set(connection) != {'server', 'certificateSha256'}:
            raise ValueError('Only a public dashboard connection file may be bundled.')
        (staging / 'dashboard-connection.json').write_text(json.dumps(connection, indent=2), encoding='utf-8')
    manifest = dict(schemaVersion=1, applicationId=config['applicationId'], bundleId=bundle_id, runtimeId=args.rid,
                    testProfile=config.get('testProfile', 'development'),
                    files=[dict(path=p.relative_to(staging).as_posix(), sha256=digest(p)) for p in sorted(staging.rglob('*'))
                           if p.is_file() and p.name != 'dashboard-connection.json'])
    (staging / 'bundle-manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    archive = output / (bundle_id + '-' + args.rid + '.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as package:
        for path in sorted(staging.rglob('*')):
            if path.is_file():
                info = zipfile.ZipInfo(path.relative_to(staging.parent).as_posix())
                executable = path.suffix == '.command' or path.suffix == '' and 'bin' in path.parts
                info.create_system = 3
                info.external_attr = (0o100755 if executable else 0o100644) << 16
                package.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
    archive.with_suffix('.zip.sha256').write_text(digest(archive) + '  ' + archive.name + '\n', encoding='ascii')
    print(json.dumps(dict(bundle=str(staging), archive=str(archive), bundleId=bundle_id)))
