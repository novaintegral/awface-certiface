import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = path.resolve(scriptDirectory, '..');
export const versionFile = path.join(repositoryRoot, 'VERSION');
export const semverPattern =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;

export function readVersion() {
  const version = fs.readFileSync(versionFile, 'utf8').trim();
  assertVersion(version);
  return version;
}

export function assertVersion(version) {
  if (!semverPattern.test(version)) {
    throw new Error(`Versão inválida em VERSION: "${version}". Use SemVer, por exemplo 1.2.3.`);
  }
}

export function readJson(relativePath) {
  return JSON.parse(fs.readFileSync(path.join(repositoryRoot, relativePath), 'utf8'));
}

export function writeJson(relativePath, value) {
  fs.writeFileSync(
    path.join(repositoryRoot, relativePath),
    `${JSON.stringify(value, null, 2)}\n`,
    'utf8'
  );
}
