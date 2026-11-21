import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { createReadStream, createWriteStream, promises as fs } from 'node:fs';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';

const MAGIC = Buffer.from('CELESTE1');
const IV_LENGTH = 12;
const TAG_LENGTH = 16;
const HEADER_LENGTH = MAGIC.length + IV_LENGTH;

function fail(message) {
  throw new Error(message);
}

const [, , operation, inputPath, outputPath] = process.argv;
if (!['encrypt', 'decrypt'].includes(operation) || !inputPath || !outputPath) {
  fail('Usage: node tools/cache-crypto.mjs <encrypt|decrypt> <input> <output>');
}

if (path.resolve(inputPath) === path.resolve(outputPath)) {
  fail('Input and output files must be different.');
}

const keyText = process.env.CACHE_KEY ?? '';
if (!/^[a-fA-F0-9]{64}$/.test(keyText)) {
  fail('CACHE_KEY must contain 64 hexadecimal characters.');
}
const key = Buffer.from(keyText, 'hex');
const tempPath = `${outputPath}.partial`;

async function encrypt() {
  const iv = randomBytes(IV_LENGTH);
  const cipher = createCipheriv('aes-256-gcm', key, iv);

  await fs.mkdir(path.dirname(path.resolve(outputPath)), { recursive: true });
  await fs.rm(tempPath, { force: true });
  await fs.writeFile(tempPath, Buffer.concat([MAGIC, iv]));

  try {
    await pipeline(
      createReadStream(inputPath),
      cipher,
      createWriteStream(tempPath, { flags: 'a' }),
    );
    await fs.appendFile(tempPath, cipher.getAuthTag());
    await fs.rm(outputPath, { force: true });
    await fs.rename(tempPath, outputPath);
  } catch (error) {
    await fs.rm(tempPath, { force: true });
    throw error;
  }
}

async function decrypt() {
  const stats = await fs.stat(inputPath);
  if (stats.size < HEADER_LENGTH + TAG_LENGTH) {
    fail('Encrypted cache file is too short.');
  }

  const handle = await fs.open(inputPath, 'r');
  const header = Buffer.alloc(HEADER_LENGTH);
  const tag = Buffer.alloc(TAG_LENGTH);
  try {
    const headerRead = await handle.read(header, 0, HEADER_LENGTH, 0);
    const tagRead = await handle.read(tag, 0, TAG_LENGTH, stats.size - TAG_LENGTH);
    if (headerRead.bytesRead !== HEADER_LENGTH || tagRead.bytesRead !== TAG_LENGTH) {
      fail('Could not read encrypted cache header.');
    }
  } finally {
    await handle.close();
  }

  if (!header.subarray(0, MAGIC.length).equals(MAGIC)) {
    fail('Encrypted cache file has an unknown format.');
  }

  const decipher = createDecipheriv('aes-256-gcm', key, header.subarray(MAGIC.length));
  decipher.setAuthTag(tag);
  const encryptedLength = stats.size - HEADER_LENGTH - TAG_LENGTH;

  await fs.mkdir(path.dirname(path.resolve(outputPath)), { recursive: true });
  await fs.rm(tempPath, { force: true });
  await fs.writeFile(tempPath, Buffer.alloc(0));

  try {
    if (encryptedLength > 0) {
      await pipeline(
        createReadStream(inputPath, {
          start: HEADER_LENGTH,
          end: stats.size - TAG_LENGTH - 1,
        }),
        decipher,
        createWriteStream(tempPath, { flags: 'a' }),
      );
    } else {
      await fs.appendFile(tempPath, decipher.final());
    }
    await fs.rm(outputPath, { force: true });
    await fs.rename(tempPath, outputPath);
  } catch (error) {
    await fs.rm(tempPath, { force: true });
    throw error;
  }
}

try {
  if (operation === 'encrypt') {
    await encrypt();
  } else {
    await decrypt();
  }
} catch (error) {
  console.error(`Cache ${operation} failed: ${error.message}`);
  process.exitCode = 1;
}
