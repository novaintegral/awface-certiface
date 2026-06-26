import { readJson, readVersion } from './version-utils.mjs';

const expectedVersion = readVersion();
const packageVersion = readJson('package.json').version;
const lockFile = readJson('package-lock.json');
const mismatches = [
  ['package.json', packageVersion],
  ['package-lock.json', lockFile.version],
  ['package-lock.json packages[""]', lockFile.packages?.['']?.version],
].filter(([, version]) => version !== expectedVersion);

if (mismatches.length > 0) {
  const details = mismatches
    .map(([file, version]) => `${file}=${version ?? '<ausente>'}`)
    .join(', ');
  throw new Error(
    `Versões divergentes. VERSION=${expectedVersion}; ${details}. Execute "npm run release:prepare -- <versão>".`
  );
}

console.log(`Versão AWFace validada: ${expectedVersion}`);
