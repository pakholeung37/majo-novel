// JavaScriptCore adapter: expose the original player's live deformed meshes as
// a packed Float32Array. No pose sampling or rasterized character frames.
let emotePlayer;
let emotePacked = new Float32Array(200000);
let emoteLayout;
function emoteInit(data, dimensions) {
  const p = new EmotePlayer();
  p.modelData = data;
  p.textures = {};
  for (const [key, size] of Object.entries(dimensions))
    p.textures[key] = { width: size[0], height: size[1], key };
  p.boxCache.clear();
  p.phys.clear();
  p.charaProfile = data.metadata?.charaProfile || null;
  p._initVariables();
  p._initTimelines();
  p._initPhysicsMeta();
  p.mainTl = null; p.diffTl = null; p.diffFade = null;
  p.isLoaded = true;
  p._evaluateVariables(0);
  p.buildScene();
  const box = p.getComputedBoxes().upper;
  const zoom = Math.min(560 / box.width, 860 / box.height);
  emoteLayout = [zoom, 300 - box.centerX * zoom, 450 - box.centerY * zoom];
  emotePlayer = p;
  p.playTimeline('表情_普通');
  return JSON.stringify(emoteLayout);
}
function emoteExpression(name) {
  if (!emotePlayer.playTimeline(name)) throw new Error('Unknown expression: ' + name);
}
function emoteMotion(name) {
  if (!emotePlayer.playTimeline(name)) throw new Error('Unknown motion: ' + name);
}
function emoteStopMotion() { emotePlayer.stopGesture(); }
function emoteTalking(value) { emotePlayer.isTalking = value; }
function emoteFrame(dt) {
  const p = emotePlayer;
  p.update(dt);
  const scene = p.buildScene();
  const keys = Array.from(scene.maskMap.keys());
  if (keys.length > 20) throw new Error('Too many E-mote masks: ' + keys.length);
  const membership = new Map();
  keys.forEach((key, i) => {
    for (const part of scene.maskMap.get(key))
      membership.set(part, (membership.get(part) || 0) | (1 << i));
  });
  const n = scene.drawList.length;
  const required = 2 + n * 174;
  if (required > emotePacked.length) emotePacked = new Float32Array(required * 2);
  let j = 0;
  emotePacked[j++] = n;
  emotePacked[j++] = keys.length;
  for (const part of scene.drawList) {
    emotePacked[j++] = Number(part.box.tex.slice(4));
    emotePacked[j++] = part.box.sx;
    emotePacked[j++] = part.box.sy;
    emotePacked[j++] = part.box.w;
    emotePacked[j++] = part.box.h;
    emotePacked[j++] = part.colorWeight[0];
    emotePacked[j++] = part.colorWeight[1];
    emotePacked[j++] = part.colorWeight[2];
    emotePacked[j++] = part.opacity * part.colorWeight[3];
    emotePacked[j++] = part.bm || 0;
    emotePacked[j++] = part.stencilKey == null ? -1 : keys.indexOf(part.stencilKey);
    emotePacked[j++] = membership.get(part) || 0;
    emotePacked.set(part.vertices, j); j += 162;
  }
  return emotePacked.subarray(0, j);
}
