#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID, createPublicKey, verify } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { run, publicBytes } from './release-io.mjs';

// Candidate planning uses anonymous signed metadata. Private keys are needed only at publication.
export async function preparePackPlan(options, dependencies = {}) {
  const root = path.resolve(options.repoRoot), output = path.resolve(options.outputDirectory);
  const command = (file, args) => (dependencies.run ?? run)(file, args, { cwd: root, signal: options.signal });
  const metadata = output + '.catalog-' + randomUUID();
  let previous = options.previousCatalogFile;
  if (!previous) {
    fs.mkdirSync(metadata);
    try {
      const bytes = await publicBytes('catalog.json', 4 * 1024 ** 2, dependencies.fetch ?? fetch, options.signal);
      const signature = await publicBytes('catalog.json.sig', 128, dependencies.fetch ?? fetch, options.signal);
      const config = JSON.parse(fs.readFileSync(path.join(root, 'src/PowerTrove.Application/Resources/PackDistribution.json')));
      if (!config.TrustedKeys.some(encoded => verify('sha256', bytes,
        createPublicKey({ key: Buffer.from(encoded, 'base64'), format: 'der', type: 'spki' }), signature))) throw new Error('Pack planning requires a verified catalog.');
      previous = path.join(metadata, 'catalog.json');
      fs.writeFileSync(previous, bytes); fs.writeFileSync(previous + '.sig', signature);
    } catch (error) { fs.rmSync(metadata, { recursive: true }); throw error; }
  }
  try {
    await command('dotnet', ['restore', path.join(root, 'PowerTrove.slnx'), '--nologo']);
    const sdk = (await command('dotnet', ['--version'])).trim();
    await command('dotnet', ['run', '--project', path.join(root, 'src/PowerTrove.ReleaseTools'), '-c', 'Release', '--',
      'pack-plan', '--repo-root', root, '--output-dir', output, '--sdk-version', sdk,
      ...(previous ? ['--previous-catalog-file', previous] : [])]);
    return JSON.parse(fs.readFileSync(path.join(output, 'pack-plan.json'), 'utf8'));
  } finally { if (fs.existsSync(metadata)) fs.rmSync(metadata, { recursive: true }); }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { values } = parseArgs({ options: { 'output-dir': { type: 'string' }, 'previous-catalog-file': { type: 'string' } } });
  try {
    if (!values['output-dir']) throw new Error('--output-dir is required.');
    const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
    await preparePackPlan({ repoRoot: root, outputDirectory: values['output-dir'], previousCatalogFile: values['previous-catalog-file'] });
    console.log('Pack versions and dependency changes planned.');
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
