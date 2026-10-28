#!/usr/bin/env node
// Windows pack release command. Keys are external to source and release artifacts.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash, createPublicKey, verify, randomUUID } from 'node:crypto';
import { run, publicBytes, repository, tag, channel } from './release-io.mjs';
export { run, publicBytes, repository, tag, channel } from './release-io.mjs';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

function compareVersions(left, right) {
  const a = left.split('.').map(Number), b = right.split('.').map(Number);
  for (let i = 0; i < 4; i++) { const d = (a[i] ?? 0) - (b[i] ?? 0); if (d) return d; } return 0;
}
const bundleName = /^[a-z0-9]+(?:[.-][a-z0-9]+)*-[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\.cbpack$/;
const sha = data => createHash('sha256').update(data).digest('hex');
const hashFile = async file => {
  const hash = createHash('sha256'); for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
};
function safeAncestors(value) {
  for (let current = path.resolve(value);;) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Linked release paths are not supported.');
    const parent = path.dirname(current); if (parent === current) break; current = parent;
  }
}
function outside(root, target) {
  const relative = path.relative(root, target);
  if (!relative || (!relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative)))
    throw new Error('Private keys and release output must be outside the source repository.');
}
async function remote(command) {
  const releases = JSON.parse(await command('gh', ['api', `repos/${repository}/releases?per_page=100`, '--paginate', '--slurp'])).flat();
  const release = releases.find(r => r.tag_name === tag);
  if (!release) return { release: null, assets: [] };
  if (!Number.isSafeInteger(release.id) || release.immutable || !release.prerelease)
    throw new Error('The pack channel must be an editable prerelease.');
  const assets = JSON.parse(await command('gh', ['api', `repos/${repository}/releases/${release.id}/assets?per_page=100`, '--paginate', '--slurp'])).flat();
  const names = new Set(); let total = 0;
  for (const asset of assets) {
    if (!Number.isSafeInteger(asset.id) || !Number.isSafeInteger(asset.size) || asset.size <= 0 || asset.size > 1024 ** 3 + 1024 ** 2 ||
        !['catalog.json', 'catalog.json.sig'].includes(asset.name) && !bundleName.test(asset.name) || names.has(asset.name))
      throw new Error('Unexpected pack release asset inventory.');
    names.add(asset.name); total += asset.size;
  }
  if (assets.length > 1000 || total > 8 * 1024 ** 3) throw new Error('Pack channel retention limits exceeded.');
  return { release, assets };
}
async function download(command, directory, patterns = []) {
  fs.mkdirSync(directory);
  await command('gh', ['release', 'download', tag, '--repo', repository, '--dir', directory,
    ...patterns.flatMap(pattern => ['--pattern', pattern])]);
}
function catalog(directory, publicKey) {
  for (const [name, maximum] of [['catalog.json', 4 * 1024 ** 2], ['catalog.json.sig', 128]])
    if (fs.statSync(path.join(directory, name)).size > maximum) throw new Error('Catalog metadata exceeds its limit.');
  const bytes = fs.readFileSync(path.join(directory, 'catalog.json'));
  if (!verify('sha256', bytes, publicKey, fs.readFileSync(path.join(directory, 'catalog.json.sig'))))
    throw new Error('The pack catalog signature is invalid.');
  const parsed = JSON.parse(bytes);
  if (!['1', '2'].includes(parsed.schemaVersion) || !Array.isArray(parsed.packs) || parsed.packs.length > 998)
    throw new Error('Invalid pack catalog.');
  const expected = new Set(['catalog.json', 'catalog.json.sig']);
  for (const entry of parsed.packs) {
    const name = `${entry.manifest.id}-${entry.manifest.version}.cbpack`;
    if (!bundleName.test(name) || expected.has(name) || !Number.isSafeInteger(entry.downloadBytes) || entry.downloadBytes <= 0)
      throw new Error('Unsafe or duplicate pack catalog entry.');
    expected.add(name);
  }
  return { bytes, parsed, expected };
}
export async function releasePacks(options, dependencies = {}) {
  const root = path.resolve(options.repoRoot);
  if (options.desktopVersion && !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(options.desktopVersion))
    throw new Error('Invalid desktop version.');
  const output = path.resolve(options.outputDirectory);
  const keyFile = path.resolve(options.keyFile);
  if ((dependencies.platform ?? process.platform) !== 'win32') throw new Error('Pack publication requires Windows.');
  outside(root, output); outside(root, keyFile); safeAncestors(output); safeAncestors(keyFile);
  const signal = options.signal;
  const command = (file, args, commandOptions = {}) => (dependencies.run ?? run)(file, args, { ...commandOptions, cwd: root, signal });
  const progress = dependencies.progress ?? (message => console.log(message));
  const revision = (await command('git', ['rev-parse', 'HEAD'])).trim();
  if (!/^[a-f0-9]{40}$/.test(revision) || (await command('git', ['status', '--porcelain'])).trim())
    throw new Error('Commit the reviewed source on the current branch before releasing packs.');
  if (!fs.existsSync(keyFile)) throw new Error('The local pack signing key is missing. Run pack-keygen once outside all repositories.');
  const lockPath = keyFile + '.release.lock';
  const lock = fs.openSync(lockPath, 'wx', 0o600);
  try {
    const tools = ['run', '--project', path.join(root, 'src/PowerTrove.ReleaseTools'), '-c', 'Release', '--'];
    const tool = args => command('dotnet', [...tools, ...args]);
    const embedded = JSON.parse(fs.readFileSync(path.join(root, 'src/PowerTrove.Application/Resources/PackDistribution.json'), 'utf8'));
    if (embedded.Channel !== channel || !Array.isArray(embedded.TrustedKeys) || !embedded.TrustedKeys.length)
      throw new Error('Embed the owner public pack key and official channel before preparing a release.');
    const publicKey = createPublicKey({ key: Buffer.from(embedded.TrustedKeys[0], 'base64'), format: 'der', type: 'spki' });
    let plan;
    if (options.publishOnly) {
      plan = JSON.parse(fs.readFileSync(path.join(output, 'pack-release.json'), 'utf8'));
      if (plan.sourceRevision !== revision || plan.repository !== repository || plan.tag !== tag)
        throw new Error('Prepared pack release does not match this source revision and channel.');
    } else {
      if (!/^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$/.test(options.minimumHostVersion ?? '0.1.0')) throw new Error('Invalid minimum host version.');
      if (fs.existsSync(output)) throw new Error('Use a new release output directory.');
      fs.mkdirSync(output);
      const state = await remote(command);
      let previous, baselineCatalogHash = null;
      const pair = state.assets.filter(a => ['catalog.json', 'catalog.json.sig'].includes(a.name));
      if (pair.length !== 0 && pair.length !== 2 || state.assets.length > 0 && pair.length !== 2)
        throw new Error('The remote pack catalog is incomplete. Resume the original prepared release with --publish-only.');
      if (pair.length) {
        progress('Downloading retained signed pack releases.');
        previous = path.join(output, 'previous'); await download(command, previous);
        baselineCatalogHash = sha(catalog(previous, publicKey).bytes);
      }
      const configuration = path.join(output, 'pack-distribution-public.json');
      const derivedPublic = path.join(output, 'signing-public.pem');
      // Validate custody through a public-key export; never copy or print the private PEM.
      await tool(['pack-export-public', '--key-file', keyFile, '--public-key-file', derivedPublic]);
      await tool(['pack-configuration', '--public-key-file', derivedPublic, '--channel', channel, '--config-file', configuration]);
      const derived = JSON.parse(fs.readFileSync(configuration, 'utf8'));
      if (!embedded.TrustedKeys.includes(derived.TrustedKeys[0])) throw new Error('Signing key does not match the public trust embedded in the app.');
      const { preparePackPlan } = await import('./plan-packs.mjs');
      const planDirectory = path.join(output, 'plan');
      const versionPlan = await (dependencies.preparePlan ?? preparePackPlan)({ repoRoot: root, outputDirectory: planDirectory,
        previousCatalogFile: previous ? path.join(previous, 'catalog.json') : undefined, signal }, dependencies);
      if (options.planDirectory) {
        const frozen = JSON.parse(fs.readFileSync(path.join(options.planDirectory, 'pack-plan.json'), 'utf8'));
        const identity = plan => JSON.stringify(plan.packs.map(p => [p.id, p.version, p.fingerprint, p.minimumHostVersion]).sort());
        if (frozen.sourceRevision !== revision || identity(frozen) !== identity(versionPlan))
          throw new Error('Desktop pack plan is stale or differs from current sources/dependencies. Rebuild the desktop candidate.');
      }
      const newerHost = p => options.desktopVersion && compareVersions(p.minimumHostVersion, options.desktopVersion) > 0;
      if (!options.deferNewerHost && versionPlan.packs.some(newerHost))
        throw new Error('A selected pack requires a newer host than this desktop release.');
      // A pack-only release cannot ship a pack that the published host cannot run; the next desktop release publishes it.
      for (const deferred of versionPlan.packs.filter(newerHost))
        progress(`Deferring ${deferred.id} until a desktop release provides host ${deferred.minimumHostVersion}.`);
      const eligible = versionPlan.packs.filter(p => !newerHost(p));
      const oldEntries = previous ? catalog(previous, publicKey).parsed.packs : [];
      const toBuild = eligible.filter(p => !oldEntries.some(e => e.manifest.id === p.id && e.manifest.version === p.version && e.manifest.releaseFingerprint === p.fingerprint));
      const published = path.join(output, 'published'); fs.mkdirSync(published);
      for (const selected of toBuild) {
        const pack = selected.assemblyName.slice('PowerTrove.Pack.'.length);
        signal?.throwIfAborted(); progress(`Publishing ${pack} pack binaries and collecting supplier notices.`);
        const project = path.join(root, `packs/PowerTrove.Pack.${pack}/composition/PowerTrove.Pack.${pack}.Composition/PowerTrove.Pack.${pack}.Composition.csproj`);
        const destination = path.join(published, `PowerTrove.Pack.${pack}`);
        await command('dotnet', ['publish', project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
          '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:EnableSourceLink=false', '-p:PowerTrovePackVersionsFile=' + path.join(planDirectory, 'pack-versions.props'), '-o', destination]);
        await tool(['collect-pack-notices', '--repo-root', root, '--published-root', destination, '--composition-project', project]);
        const inventory = JSON.parse(fs.readFileSync(path.join(destination, 'licenses/dependency-inventory.json'), 'utf8'));
        if (!Array.isArray(inventory.Packages) || inventory.Packages.some(p => !Array.isArray(p.Files) || p.Files.length === 0))
          throw new Error('Supplier legal text is missing. Resolve the exact pack inventory before signing.');
        for (const name of ['LICENSE', 'NOTICE']) fs.copyFileSync(path.join(root, name), path.join(destination, name), fs.constants.COPYFILE_EXCL);
      }
      progress('Signing bundles and retaining compatible historical versions.');
      const bundles = path.join(output, 'bundles');
      if (toBuild.length) await tool(['pack-bundles', '--published-root', published, '--output-dir', bundles, '--key-file', keyFile,
        '--min-host-version', options.minimumHostVersion ?? '0.1.0', '--pack-plan-file', path.join(planDirectory, 'pack-plan.json')]);
      await tool(['pack-github-assets', '--pack-root', toBuild.length ? path.join(bundles, 'packs') : previous, '--output-dir', path.join(output, 'github-assets'),
        '--key-file', keyFile, ...(previous && toBuild.length ? ['--previous-pack-root', previous] : [])]);
      const checked = catalog(path.join(output, 'github-assets'), publicKey);
      for (const required of eligible)
        if (!checked.parsed.packs.some(p => p.manifest.id === required.id && p.manifest.version === required.version && p.manifest.releaseFingerprint === required.fingerprint))
          throw new Error('A required desktop pack is missing from the signed candidate.');
      progress('Checking the real Windows worker handshake for each signed pack.');
      let worker = options.workerExecutable ? path.resolve(options.workerExecutable) : undefined;
      if (worker) {
        safeAncestors(worker);
        if (!fs.statSync(worker).isFile()) throw new Error('The packaged worker executable is missing.');
      } else {
        const workerProject = path.join(root, 'src/PowerTrove.PackHost/PowerTrove.PackHost.csproj');
        await command('dotnet', ['build', workerProject, '-c', 'Release', '--nologo',
          ...(options.desktopVersion ? ['-p:Version=' + options.desktopVersion] : [])]);
        worker = path.join(root, 'src/PowerTrove.PackHost/bin/Release/net10.0-windows10.0.19041.0/PowerTrove.PackHost.exe');
      }
      await command('pwsh', ['-NoProfile', '-File', path.join(root, 'scripts/smoke-packs.ps1'),
        '-AssetDirectory', path.join(output, 'github-assets'), '-WorkerExecutable', worker, '-CatalogSha256', sha(checked.bytes)],
      { logOutput: true });
      const assets = [];
      for (const name of checked.expected) assets.push({ name, sha256: await hashFile(path.join(output, 'github-assets', name)) });
      const target = (await command('gh', ['api', `repos/${repository}/commits/main`, '--jq', '.sha'])).trim();
      if (!/^[a-f0-9]{40}$/.test(target)) throw new Error('Cannot identify the public release target.');
      plan = { repository, tag, sourceRevision: revision, target, baselineCatalogHash, assets, packs: versionPlan.packs, changedPackIds: toBuild.map(p => p.id) };
      fs.writeFileSync(path.join(output, 'pack-release.json'), JSON.stringify(plan, null, 2) + '\n', { flag: 'wx' });
      // These are owned intermediate directories; retain only the reviewable signed assets/report.
      for (const directory of [published, bundles, previous].filter(Boolean)) if (fs.existsSync(directory)) fs.rmSync(directory, { recursive: true });
      fs.unlinkSync(derivedPublic); fs.unlinkSync(configuration);
    }
    const assetRoot = path.join(output, 'github-assets');
    const checked = catalog(assetRoot, publicKey);
    if (plan.assets.length !== checked.expected.size || fs.readdirSync(assetRoot).length !== checked.expected.size)
      throw new Error('Prepared asset inventory differs from the signed catalog.');
    for (const asset of plan.assets)
      if (!checked.expected.has(asset.name) || await hashFile(path.join(assetRoot, asset.name)) !== asset.sha256)
        throw new Error('Prepared pack release bytes have changed.');
    if (!options.publish) { progress('Signed pack release prepared. Publication requires --publish or --publish-only.'); return { output, published: false }; }
    if ((await command('git', ['rev-parse', 'HEAD'])).trim() !== revision || (await command('git', ['status', '--porcelain'])).trim())
      throw new Error('Source changed during release preparation. Nothing was published.');
    const current = await remote(command);
    let remoteCatalogHash = null;
    if (current.assets.some(a => a.name === 'catalog.json')) {
      const directory = path.join(output, 'publication-check-' + randomUUID());
      try {
        await download(command, directory, ['catalog.json', 'catalog.json.sig']);
        try { remoteCatalogHash = sha(catalog(directory, publicKey).bytes); }
        catch (error) {
          // Resume an interrupted upload only when the remote JSON is exactly
          // this prepared catalog; an unrelated invalid index is never accepted.
          const rawHash = sha(fs.readFileSync(path.join(directory, 'catalog.json')));
          if (rawHash !== sha(checked.bytes)) throw error;
          remoteCatalogHash = rawHash;
        }
      }
      finally { if (fs.existsSync(directory)) fs.rmSync(directory, { recursive: true }); }
    }
    if (remoteCatalogHash !== plan.baselineCatalogHash && remoteCatalogHash !== sha(checked.bytes))
      throw new Error('Another pack catalog was published. Prepare a new release retaining those versions.');
    for (const asset of current.assets.filter(a => bundleName.test(a.name))) {
      const local = plan.assets.find(a => a.name === asset.name);
      if (!local) throw new Error('Prepared release would drop a retained remote bundle.');
      if (asset.digest === `sha256:${local.sha256}`) continue;
      const directory = path.join(output, 'asset-check-' + randomUUID());
      try {
        await download(command, directory, [asset.name]);
        if (await hashFile(path.join(directory, asset.name)) !== local.sha256) throw new Error('Published pack bytes cannot be replaced.');
      } finally { if (fs.existsSync(directory)) fs.rmSync(directory, { recursive: true }); }
    }
    const unchanged = plan.changedPackIds?.length === 0 && remoteCatalogHash === sha(checked.bytes);
    const notes = path.join(output, 'pack-release-notes.md');
    fs.writeFileSync(notes, 'Experimental official PowerTrove file packs for Windows x64.\n\nOptional signed pack downloads; this release is not the desktop installer. Requires a pack-enabled PowerTrove application.\n\nIndividual downloads include supplier notices. Clean-machine and interactive application acceptance remain separate from automated build and signature verification.\n' +
      (options.releaseNotes ? '\n' + options.releaseNotes.trim() + '\n' : ''));
    if (!unchanged && !current.release) {
      await command('gh', ['release', 'create', tag, '--repo', repository, '--target', plan.target,
        '--title', 'Official PowerTrove packs', '--notes-file', notes, '--prerelease', '--draft', '--latest=false']);
    }
    if (!unchanged) {
      progress('Uploading new immutable bundles before the signed catalog.');
      for (const asset of plan.assets.filter(a => bundleName.test(a.name)))
        if (!current.assets.some(a => a.name === asset.name)) await command('gh', ['release', 'upload', tag, path.join(assetRoot, asset.name), '--repo', repository]);
      await command('gh', ['release', 'upload', tag, path.join(assetRoot, 'catalog.json'), path.join(assetRoot, 'catalog.json.sig'), '--repo', repository, '--clobber']);
      await command('gh', ['release', 'edit', tag, '--repo', repository, '--draft=false', '--prerelease', '--latest=false',
        ...(options.releaseNotes ? ['--notes-file', notes] : [])]);
    } else progress('Required pack versions already exist; verifying their public downloads.');
    const publicCatalog = await publicBytes('catalog.json', 4 * 1024 ** 2, dependencies.fetch ?? fetch, signal);
    const publicSignature = await publicBytes('catalog.json.sig', 128, dependencies.fetch ?? fetch, signal);
    if (sha(publicCatalog) !== sha(checked.bytes) || !verify('sha256', publicCatalog, publicKey, publicSignature))
      throw new Error('Public channel verification failed. Retain this prepared release for retry.');
    for (const asset of plan.assets.filter(a => bundleName.test(a.name))) {
      const digest = await publicBytes(asset.name, fs.statSync(path.join(assetRoot, asset.name)).size, dependencies.fetch ?? fetch, signal, true);
      if (digest !== asset.sha256) throw new Error('Public pack download differs from the verified signed bundle.');
    }
    fs.writeFileSync(path.join(output, 'published.json'), JSON.stringify({ repository, tag, catalogSha256: sha(publicCatalog), verifiedBundles: checked.expected.size - 2, verifiedUtc: new Date().toISOString() }) + '\n');
    progress('Pack release published. Existing bundle assets were preserved.');
    return { output, published: true, url: `https://github.com/${repository}/releases/tag/${tag}` };
  } finally { fs.closeSync(lock); fs.unlinkSync(lockPath); }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const parsed = parseArgs({ options: { 'output-dir': { type: 'string' }, 'key-file': { type: 'string' },
    'minimum-host-version': { type: 'string', default: '0.1.0' }, 'plan-dir': { type: 'string' }, 'desktop-version': { type: 'string' }, publish: { type: 'boolean' }, 'publish-only': { type: 'boolean' } } });
  const controller = new AbortController(); process.once('SIGINT', () => controller.abort());
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  try {
    if (parsed.values['publish-only'] && !parsed.values['output-dir']) throw new Error('--publish-only requires the original --output-dir.');
    const result = await releasePacks({ repoRoot: root, outputDirectory: parsed.values['output-dir'] ?? path.join(os.tmpdir(), 'PowerTrove-PackRelease-' + randomUUID()),
      keyFile: parsed.values['key-file'] ?? path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'PowerTrove-ReleaseKeys', 'packs-private.pem'),
      minimumHostVersion: parsed.values['minimum-host-version'], publish: parsed.values.publish || parsed.values['publish-only'],
      publishOnly: parsed.values['publish-only'], planDirectory: parsed.values['plan-dir'], desktopVersion: parsed.values['desktop-version'], signal: controller.signal });
    console.log(JSON.stringify(result));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
