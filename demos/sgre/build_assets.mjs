// Copy the original E-mote model and source textures into the demo.
// The game evaluates these files in real time; no animation frames are baked.
import { createRequire } from 'node:module';
import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';

const root = dirname(fileURLToPath(import.meta.url));
const animation = process.env.SGRE_ANIMATION || join(homedir(), 'web/SGRE-animation');
const converted = process.env.SGRE_CONVERTED || join(homedir(), 'Downloads/SGRE-converted');
const require = createRequire(join(animation, 'package.json'));
const sharp = require('sharp');
const assets = join(root, 'assets');
const modelDir = join(assets, 'characters/红莉栖/emote');
const uiDir = join(assets, 'ui');
for (const path of [modelDir, uiDir, join(assets, 'backgrounds'), join(assets, 'audio/bgm'), join(assets, 'audio/voice')])
  await mkdir(path, { recursive: true });
const source = await readFile(join(animation, 'src/emotePlayer.js'), 'utf8');
if (!source.includes('export class EmotePlayer {')) throw new Error('The E-mote player export was not found.');
await writeFile(join(root, 'emote/emotePlayer.js'), source.replace('export class EmotePlayer {', 'class EmotePlayer {'));
await copyFile(join(animation, 'public/motions/牧瀬紅莉栖ab.json'), join(modelDir, 'model.json'));
for (const i of ['000', '001', '002', '003'])
  await copyFile(join(animation, 'public/motions/牧瀬紅莉栖ab', `tex#${i}-texture.png`), join(modelDir, `tex_${i}.png`));
for (const [texture, name, left, top, width, height] of [
  ['000', 'window', 1, 293, 1920, 290],
  ['000', 'name-decoration', 965, 1821, 1053, 56],
  ['001', 'name-plate', 137, 237, 226, 59],
  ['001', 'next', 261, 1333, 49, 49],
]) {
  await sharp(join(converted, 'motions/main', `tex#${texture}-texture.png`))
    .extract({ left, top, width, height }).png().toFile(join(uiDir, `${name}.png`));
}
await copyFile(join(converted, 'images/resg_bg022a1_mob/resg_bg022a1_mob.png'), join(assets, 'backgrounds/lab.png'));
await copyFile(join(converted, 'audio/windows/sound/bgm09/bgm09.wav'), join(assets, 'audio/bgm/bgm09.wav'));
for (const voice of ['krs0000', 'krs0001', 'oka0013', 'oka0014', 'oka0015', 'oka0016', 'krs0002', 'oka0017', 'oka0018', 'krs0003']) {
  const name = `resg01_04_${voice}`;
  await copyFile(join(converted, 'audio/windows/voice', name, `${name}.wav`), join(assets, 'audio/voice', `${name}.wav`));
}
