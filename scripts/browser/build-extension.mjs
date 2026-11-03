#!/usr/bin/env node
/**
 * Deterministic Chromium extension package. No dependencies, keys or store API access.
 * Creates a Zip containing the reviewed MV3 extension files, not development tests.
 */
import fs from 'node:fs';
import path from 'node:path';
import { deflateRawSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const source = path.join(root, 'extensions', 'chromium');
const generated = path.join(root, 'artifacts', 'browser-extension');
const output = process.argv.includes('--output')
  ? path.resolve(process.argv[process.argv.indexOf('--output')+1]||'')
  : path.join(generated, 'PowerTrove-Browser-Companion.zip');

const allowed = new Set(['.js','.html','.css','.json','.png','.svg']);
const forbidden = /(?:^|\W)(?:eval\s*\(|new\s+Function\s*\(|import\s*\(\s*['"]https?:|https?:\/\/[^'"]+\.js)/;
const required = ['manifest.json','background.js','fullpage-capture.js','companion.js','protocol.js','popup.html','popup.css','popup.js',
  'icons/icon16.png','icons/icon32.png','icons/icon48.png','icons/icon128.png'];
function collect(directory, prefix='') {
  const files=[];
  for(const entry of fs.readdirSync(directory,{withFileTypes:true})) {
    const name=prefix+entry.name;
    const location=path.join(directory,entry.name);
    if(entry.isSymbolicLink()) throw Error('Symlink in extension package');
    if(entry.isDirectory()) {
      if(name==='tests'||name.startsWith('tests/'))continue;
      files.push(...collect(location,name+'/'));
    } else if(entry.isFile()) {
      if(!allowed.has(path.extname(entry.name).toLowerCase())||
        name.includes('..')||name.includes('\\'))throw Error('Unexpected extension file');
      files.push({name,data:fs.readFileSync(location)});
    } else throw Error('Unexpected extension entry');
  }
  return files.sort((a,b)=>a.name.localeCompare(b.name,'en'));
}
function validate(files) {
  const map=new Map(files.map(f=>[f.name,f.data]));
  for(const name of required)if(!map.has(name))throw Error('Missing required extension source');
  const manifest=JSON.parse(map.get('manifest.json').toString('utf8'));
  if(manifest.manifest_version!==3||manifest.background?.service_worker!=='background.js')
    throw Error('Manifest V3 service worker required');
  for (const [size,name] of Object.entries(manifest.icons||{})) {
    if(name!==`icons/icon${size}.png` || !['16','32','48','128'].includes(size) ||
       !map.has(name) || map.get(name).subarray(0,8).toString('hex')!=='89504e470d0a1a0a')
      throw Error('Unapproved or invalid extension icon');
  }
  if(!['16','32','48','128'].every(size=>manifest.icons?.[size]===`icons/icon${size}.png`))
    throw Error('Required store icon sizes are missing');
  const permissions=new Set(manifest.permissions||[]);
  const permitted=new Set(['activeTab','scripting','contextMenus','downloads','clipboardWrite',
    'nativeMessaging','pageCapture','storage','debugger']);
  if([...permissions].some(p=>!permitted.has(p))||permissions.size!==(manifest.permissions||[]).length)
    throw Error('Unreviewed required permission');
  if(manifest.host_permissions?.length||
    manifest.content_security_policy?.extension_pages?.includes('unsafe-eval'))
    throw Error('Unreviewed extension privileges');
  const origins=manifest.optional_host_permissions||[];
  // <all_urls>: screenshots started from PowerTrove have no click in the extension, so activeTab never
  // applies, and Chrome allows captureVisibleTab only with <all_urls>. Still optional, granted in the popup.
  const optional = manifest.optional_permissions || [];
  if(origins.some(p=>!['<all_urls>','http://*/*','https://*/*'].includes(p))||
    !Array.isArray(optional) ||
    optional.some(p=>p!=='tabs') ||
    new Set(optional).size !== optional.length ||
    optional.some(p=>permissions.has(p)))
    throw Error('Unreviewed optional permission');
  if(!Array.isArray(manifest.permissions)||!permissions.has('activeTab')||!permissions.has('nativeMessaging')||!permissions.has('debugger'))
    throw Error('Missing required consent boundary');
  const languages=['en','de'].map(lang=>JSON.parse(map.get('_locales/'+lang+'/messages.json')));
  if(JSON.stringify(Object.keys(languages[0]).sort())!==JSON.stringify(Object.keys(languages[1]).sort()))
    throw Error('English/German localization keys differ');
  for(const file of files) {
    if(file.data.byteLength>1024*1024)throw Error('Unexpectedly large extension source');
    if(file.name.endsWith('.js')&&forbidden.test(file.data.toString('utf8')))
      throw Error('Unreviewed remote or dynamic script execution');
  }
  if(files.some(f=>f.name.startsWith('tests/')))throw Error('Tests must not ship in extension');
}
let crcTable;
function crc32(data){
  if(!crcTable)crcTable=Array.from({length:256},(_,n)=>{
    for(let i=0;i<8;i++)n=n&1?0xedb88320^(n>>>1):n>>>1;
    return n>>>0;
  });
  let crc=0xffffffff;
  for(const byte of data)crc=(crc>>>8)^crcTable[(crc^byte)&255];
  return (crc^0xffffffff)>>>0;
}
function zip(files) {
  const pieces=[], central=[];
  let offset=0;
  for(const file of files){
    const name=Buffer.from(file.name,'utf8'), input=file.data;
    const compressed=deflateRawSync(input,{level:9});
    const crc=crc32(input);
    const local=Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50,0);local.writeUInt16LE(20,4);
    local.writeUInt16LE(0x800,6);local.writeUInt16LE(8,8);
    // DOS date 1980-01-01, time 00:00:00
    local.writeUInt16LE(0,10);local.writeUInt16LE(33,12);
    local.writeUInt32LE(crc,14);local.writeUInt32LE(compressed.length,18);
    local.writeUInt32LE(input.length,22);local.writeUInt16LE(name.length,26);
    pieces.push(local,name,compressed);
    const meta=Buffer.alloc(46);
    meta.writeUInt32LE(0x02014b50,0);meta.writeUInt16LE(20,4);
    meta.writeUInt16LE(20,6);meta.writeUInt16LE(0x800,8);
    meta.writeUInt16LE(8,10);meta.writeUInt16LE(0,12);
    meta.writeUInt16LE(33,14);meta.writeUInt32LE(crc,16);
    meta.writeUInt32LE(compressed.length,20);meta.writeUInt32LE(input.length,24);
    meta.writeUInt16LE(name.length,28);meta.writeUInt32LE(offset,42);
    central.push(meta,name);
    offset+=local.length+name.length+compressed.length;
  }
  const centralBytes=Buffer.concat(central);
  const end=Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50,0);
  end.writeUInt16LE(files.length,8);end.writeUInt16LE(files.length,10);
  end.writeUInt32LE(centralBytes.length,12);end.writeUInt32LE(offset,16);
  return Buffer.concat([...pieces,centralBytes,end]);
}
const files=collect(source);
validate(files);
if(process.argv.includes('--check')) {
  process.stdout.write('Extension sources and permissions validated.\n');
} else {
  if(output===root||!output.endsWith('.zip'))throw Error('Supply a .zip output path');
  fs.mkdirSync(path.dirname(output),{recursive:true});
  fs.writeFileSync(output,zip(files));
  process.stdout.write('Deterministic extension ZIP created. Manual store review still required.\n');
}
