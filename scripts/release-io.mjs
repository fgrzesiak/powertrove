import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
export const repository = 'fgrzesiak/powertrove';
export const tag = 'packs-v1';
export const channel = `https://github.com/${repository}/releases/download/${tag}/`;
export function run(command, args, { cwd, signal, env, logOutput = false } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, signal, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '', stderr = '', exceeded = false;
    const timer = setTimeout(() => child.kill(), 45 * 60 * 1000);
    child.stdout.on('data', value => { if (logOutput) process.stdout.write(value); stdout += value; if (stdout.length > 32 * 1024 * 1024) { exceeded = true; child.kill(); } });
    child.stderr.on('data', value => { if (logOutput) process.stderr.write(value); stderr += value; if (stderr.length > 1024 * 1024) { exceeded = true; child.kill(); } });
    child.on('error', () => { clearTimeout(timer); reject(new Error(`${command} could not start. Check local tools and authentication.`)); });
    child.on('close', code => {
      clearTimeout(timer);
      if (code !== 0 || exceeded) reject(new Error(`${command} failed. No credential or command output is included in this error.`));
      else resolve(stdout);
    });
  });
}
export async function publicBytes(name, maximum, fetcher, signal, hashOnly = false) {
  let url = new URL(name, channel);
  for (let redirects = 0; redirects <= 5; redirects++) {
    const response = await fetcher(url, { redirect: 'manual', signal: AbortSignal.any([AbortSignal.timeout(30000), ...(signal ? [signal] : [])]) });
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const location = response.headers.get('location'); await response.body?.cancel();
      if (!location || redirects === 5) throw new Error('Public pack redirect limit exceeded.');
      url = new URL(location, url);
      if (url.protocol !== 'https:' || url.port || url.username || url.password || url.hash ||
          !['github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(url.hostname) ||
          url.hostname === 'github.com' && !url.href.startsWith(channel)) throw new Error('Unexpected public pack redirect.');
      continue;
    }
    if (response.status !== 200 || Number(response.headers.get('content-length') ?? 0) > maximum) {
      await response.body?.cancel(); throw new Error('Public pack metadata is unavailable or too large.');
    }
    const chunks = []; const hash = createHash('sha256'); let length = 0;
    try {
      for await (const chunk of response.body) {
        length += chunk.length;
        if (length > maximum) throw new Error('Public pack metadata exceeds its limit.');
        if (hashOnly) hash.update(chunk); else chunks.push(Buffer.from(chunk));
      }
    } finally { if (!response.body.locked) await response.body.cancel(); }
    return hashOnly ? hash.digest('hex') : Buffer.concat(chunks);
  }
}
