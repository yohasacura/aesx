#!/usr/bin/env node
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 yohasacura
//
// An independent reader for AESX version 1 files, written from docs/FORMAT.md alone, in another
// language, on another cryptography library (Node.js and the OpenSSL it ships with). It shares
// no code with aesx and needs no packages.
//
//   AESX_PASSWORD=... node aesx-decrypt.mjs <file.aesx> <output>
//
// It exists for two reasons. CI runs it against files that aesx wrote, which shows that the
// specification is complete enough to implement and that aesx follows it. And it is a way out:
// whoever holds the password can read their files with this page of code, with or without aesx.
// The output is written as it is verified and removed again if a later chunk fails.

import { closeSync, openSync, readSync, rmSync, writeSync } from 'node:fs';
import { createDecipheriv, pbkdf2Sync } from 'node:crypto';

const HEADER = 29;
const TAG = 16;
const CHUNK = 1 << 20;
const MAX_ITERATIONS = 10_000_000;

/** Fills the buffer unless the file ends first; returns the number of bytes read. */
function readFull(fd, buffer) {
  let total = 0;
  while (total < buffer.length) {
    const n = readSync(fd, buffer, total, buffer.length - total, null);
    if (n === 0) break;
    total += n;
  }
  return total;
}

function decrypt(input, output, password) {
  const fd = openSync(input, 'r');
  let out;
  let finished = false;
  try {
    // Step 1: the header.
    const header = Buffer.alloc(HEADER);
    if (readFull(fd, header) < HEADER || header.toString('latin1', 0, 4) !== 'AESX') throw new Error('not an AESX file');
    if (header[4] !== 1) throw new Error(`unsupported AESX format version ${header[4]}`);
    const iterations = header.readUInt32LE(5);
    if (iterations === 0 || iterations > MAX_ITERATIONS) throw new Error('invalid key-derivation parameters in header');
    const salt = header.subarray(9, 25);
    const noncePrefix = header.subarray(25, 29);

    // Step 2: the key.
    const key = pbkdf2Sync(Buffer.from(password, 'utf8'), salt, iterations, 32, 'sha256');

    // Steps 3 to 5: chunks, with one chunk of look-ahead to know which one is the last.
    out = openSync(output, 'wx');
    let current = Buffer.alloc(CHUNK + TAG);
    let next = Buffer.alloc(CHUNK + TAG);
    let currentLength = readFull(fd, current);
    for (let index = 0n; ; index++) {
      const nextLength = readFull(fd, next);
      const last = nextLength === 0;
      if (currentLength < TAG || (!last && currentLength !== CHUNK + TAG)) throw new Error('file is truncated or corrupted');

      const counter = Buffer.alloc(8);
      counter.writeBigUInt64BE(index);
      const nonce = Buffer.concat([noncePrefix, counter]);
      const aad = Buffer.concat([header, counter, Buffer.from([last ? 1 : 0])]);

      const decipher = createDecipheriv('aes-256-gcm', key, nonce);
      decipher.setAAD(aad);
      decipher.setAuthTag(current.subarray(currentLength - TAG, currentLength));
      let plain;
      try {
        // Nothing is released before final() has checked the tag.
        plain = Buffer.concat([decipher.update(current.subarray(0, currentLength - TAG)), decipher.final()]);
      } catch {
        throw new Error(index === 0n ? 'wrong password, or the file is corrupted' : `authentication failed at chunk ${index}`);
      }
      writeSync(out, plain);

      if (last) break;
      [current, next] = [next, current];
      currentLength = nextLength;
    }
    finished = true;
  } finally {
    closeSync(fd);
    if (out !== undefined) {
      closeSync(out);
      if (!finished) rmSync(output, { force: true }); // step 6: no partial output
    }
  }
}

const [input, output] = process.argv.slice(2);
const password = process.env.AESX_PASSWORD;
if (!input || !output || !password) {
  console.error('usage: AESX_PASSWORD=<password> node aesx-decrypt.mjs <file.aesx> <output>');
  process.exitCode = 2;
} else {
  try {
    decrypt(input, output, password);
  } catch (error) {
    console.error(`error: ${error.message}`);
    process.exitCode = 1;
  }
}
