import fs from 'node:fs';
import path from 'node:path';
import {
  assertVersion,
  readJson,
  readVersion,
  repositoryRoot,
  writeJson,
} from './version-utils.mjs';

const requested = process.argv[2];
if (!requested) {
  throw new Error('Informe major, minor, patch ou uma versão SemVer. Exemplo: npm run release:prepare -- patch');
}

const currentVersion = readVersion();
const stableVersion = currentVersion.split('-')[0].split('+')[0];
const [major, minor, patch] = stableVersion.split('.').map(Number);
const nextVersion = {
  major: `${major + 1}.0.0`,
  minor: `${major}.${minor + 1}.0`,
  patch: `${major}.${minor}.${patch + 1}`,
}[requested] ?? requested.replace(/^v/, '');

assertVersion(nextVersion);
fs.writeFileSync(path.join(repositoryRoot, 'VERSION'), `${nextVersion}\n`, 'utf8');

const packageJson = readJson('package.json');
packageJson.version = nextVersion;
writeJson('package.json', packageJson);

const packageLock = readJson('package-lock.json');
packageLock.version = nextVersion;
if (packageLock.packages?.['']) {
  packageLock.packages[''].version = nextVersion;
}
writeJson('package-lock.json', packageLock);

console.log(`Release preparada: ${currentVersion} -> ${nextVersion}`);
console.log('Atualize CHANGELOG.md, execute os builds e crie a tag somente após a validação.');
