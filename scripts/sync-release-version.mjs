import { readVersion, writeReleaseVersion } from './version-utils.mjs';

const version = readVersion();
writeReleaseVersion(version);
console.log(`Versao AWFace sincronizada no frontend: ${version}`);
