// E-mote 3.x runtime for Steins;Gate Elite (SGRE) character motions.
//
// Scene evaluation model (reverse-engineered from the PSB/JSON data):
//  * Every motion has `parameter[]` (variable id, rangeBegin/End, division). A layer's local
//    time is taken from parameter[layer.parameterize ?? 0].
//  * frameList: type 2 = hold key, type 3 = key interpolated toward the next key,
//    type 0 = "no content" (the layer is absent until the next key / terminator).
//  * Layer types: 0 = mesh deformer / sprite (4x4 bicubic Bezier patch `mesh.bp`),
//    2 = transform group (coord/angle/opa/cw), 3 = reference to another motion,
//    12 = stencil group (children clipped by `stencilCompositeMaskLayerList`).
//  * meshCombine=1 adds the patch displacement into the parent deformer chain,
//    meshSyncChildMask 8 deforms descendants' meshes, 1 only moves child origins.
//  * Draw order = accumulated coord[2] (larger is in front), stable tree order.

const BEZ_ID = new Float32Array(32);
for (let r = 0; r < 4; r++) {
  for (let c = 0; c < 4; c++) {
    BEZ_ID[(r * 4 + c) * 2] = c / 3;
    BEZ_ID[(r * 4 + c) * 2 + 1] = r / 3;
  }
}

const NEUTRAL_CW = [1, 1, 1, 1];
const GRID_N = 8;

function clamp(v, lo, hi) {
  return v < lo ? lo : v > hi ? hi : v;
}

function decodeCw(cw) {
  if (cw === undefined || cw === null) return NEUTRAL_CW;
  const u = cw >>> 0;
  return [((u >>> 24) & 0xff) / 128, ((u >>> 16) & 0xff) / 128, ((u >>> 8) & 0xff) / 128, (u & 0xff) / 128];
}

function lerpAngle(a, b, t) {
  const d = ((((b - a) % 360) + 540) % 360) - 180;
  return a + d * t;
}

// Keyframe easing used by timelineControl (codes observed: 0, ±0.5, ±1, 101..107)
function ease(code, t) {
  if (!code) return t;
  if (code >= -1 && code <= 1) {
    const p = 1 + Math.abs(code) * 2;
    return code > 0 ? 1 - Math.pow(1 - t, p) : Math.pow(t, p);
  }
  switch (code) {
    case 101: return t * t;
    case 102: return 1 - (1 - t) * (1 - t);
    case 103: return t * t * (3 - 2 * t);
    case 104: return t * t * t;
    case 105: return 1 - Math.pow(1 - t, 3);
    case 106: return 1 - Math.pow(1 - t, 4);
    case 107: return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
    default: return t * t * (3 - 2 * t);
  }
}

// ---------- transform chain (immutable linked list, local -> world) ----------
// kind 0: affine {a,b,c,d,e,f};  kind 1: bezier patch {w,h,ox,oy,P}
function pushAffine(parent, a, b, c, d, e, f) {
  if (parent && parent.kind === 0) {
    return {
      kind: 0,
      a: parent.a * a + parent.c * b,
      b: parent.b * a + parent.d * b,
      c: parent.a * c + parent.c * d,
      d: parent.b * c + parent.d * d,
      e: parent.a * e + parent.c * f + parent.e,
      f: parent.b * e + parent.d * f + parent.f,
      parent: parent.parent,
    };
  }
  return { kind: 0, a, b, c, d, e, f, parent };
}

function pushPatch(parent, patch) {
  if (!patch || patch.identity) return parent;
  return { kind: 1, patch, parent };
}

// Evaluates a patch at local point (x, y). Writes result to out[0], out[1].
function evalPatch(p, x, y, out) {
  const u = (x + p.ox) / p.w;
  const v = (y + p.oy) / p.h;
  const uc = u < 0 ? 0 : u > 1 ? 1 : u;
  const vc = v < 0 ? 0 : v > 1 ? 1 : v;
  const su = 1 - uc;
  const u0 = su * su * su;
  const u1 = 3 * uc * su * su;
  const u2 = 3 * uc * uc * su;
  const u3 = uc * uc * uc;

  const sv = 1 - vc;
  const v0 = sv * sv * sv;
  const v1 = 3 * vc * sv * sv;
  const v2 = 3 * vc * vc * sv;
  const v3 = vc * vc * vc;

  const P = p.P;
  const X =
    v0 * (u0 * P[0] + u1 * P[2] + u2 * P[4] + u3 * P[6]) +
    v1 * (u0 * P[8] + u1 * P[10] + u2 * P[12] + u3 * P[14]) +
    v2 * (u0 * P[16] + u1 * P[18] + u2 * P[20] + u3 * P[22]) +
    v3 * (u0 * P[24] + u1 * P[26] + u2 * P[28] + u3 * P[30]);
  const Y =
    v0 * (u0 * P[1] + u1 * P[3] + u2 * P[5] + u3 * P[7]) +
    v1 * (u0 * P[9] + u1 * P[11] + u2 * P[13] + u3 * P[15]) +
    v2 * (u0 * P[17] + u1 * P[19] + u2 * P[21] + u3 * P[23]) +
    v3 * (u0 * P[25] + u1 * P[27] + u2 * P[29] + u3 * P[31]);

  out[0] = (X + (u - uc)) * p.w - p.ox;
  out[1] = (Y + (v - vc)) * p.h - p.oy;
}

const _tmp = new Float64Array(2);
function applyXf(node, x, y, out) {
  while (node) {
    if (node.kind === 0) {
      const nx = node.a * x + node.c * y + node.e;
      y = node.b * x + node.d * y + node.f;
      x = nx;
    } else {
      evalPatch(node.patch, x, y, _tmp);
      x = _tmp[0];
      y = _tmp[1];
    }
    node = node.parent;
  }
  out[0] = x;
  out[1] = y;
}

function makePatch(box, bp) {
  if (!bp) return { w: box.w, h: box.h, ox: box.ox, oy: box.oy, P: BEZ_ID, identity: true };
  const P = new Float32Array(32);
  let identity = true;
  for (let i = 0; i < 32; i++) {
    P[i] = bp[i];
    if (Math.abs(bp[i] - BEZ_ID[i]) > 1e-5) identity = false;
  }
  return { w: box.w, h: box.h, ox: box.ox, oy: box.oy, P, identity };
}

function combinePatch(base, add) {
  if (add.identity) return base;
  if (base.identity) return { ...add, w: base.w, h: base.h, ox: base.ox, oy: base.oy };
  const P = new Float32Array(32);
  for (let i = 0; i < 32; i++) P[i] = base.P[i] + add.P[i] - BEZ_ID[i];
  return { w: base.w, h: base.h, ox: base.ox, oy: base.oy, P, identity: false };
}

function boxEq(a, b) {
  return a && b && Math.abs(a.w - b.w) < 0.5 && Math.abs(a.h - b.h) < 0.5 && Math.abs(a.ox - b.ox) < 0.5 && Math.abs(a.oy - b.oy) < 0.5;
}

// ---------------------------------------------------------------------------

class EmotePlayer {
  constructor() {
    this.modelData = null;
    this.textures = {};
    this.isLoaded = false;

    this.varMeta = new Map(); // id -> meta
    this.targets = {}; // base targets (user sliders + diff:0 expression timelines)
    this.values = {}; // smoothed base
    this.vars = {}; // final evaluated variables (after diff, blink, talk, selectors)
    this.instantVars = new Set();

    this.timelines = [];
    this.mainTl = null; // diff:0
    this.diffTl = null; // diff:1
    this.diffFade = null; // previous diff contributions fading out

    this.autoIdle = true;
    this.autoBlink = true;
    this.isTalking = false;
    this.enablePhysics = true;
    this.lookAt = null; // {x:-1..1, y:-1..1} or null

    this.idleTime = 0;
    this.blink = { timer: 1.5, phase: -1, dur: 0.27 };
    this.talkPhase = 0;
    this.talkValue = 0;

    this.phys = new Map();
    this.physDt = 0;
    this.boxCache = new Map();
    this.vertPool = [];
    this.lastScene = null;
  }

  // ------------------------------------------------------------------ loading
  async loadModel(jsonUrl, textureBasePath) {
    this.isLoaded = false;
    const resp = await fetch(jsonUrl);
    if (!resp.ok) throw new Error(`Failed to load ${jsonUrl} (${resp.status})`);
    const data = await resp.json();

    const textures = {};
    await Promise.all(
      Object.keys(data.source || {}).map(
        (texKey) =>
          new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => {
              textures[texKey] = img;
              resolve();
            };
            img.onerror = () => {
              const fb = new Image();
              fb.onload = () => {
                textures[texKey] = fb;
                resolve();
              };
              fb.onerror = () => reject(new Error(`Failed to load texture ${texKey}`));
              fb.src = `${textureBasePath}/${encodeURIComponent(texKey)}-texture.png`;
            };
            img.src = `${textureBasePath}/${texKey.replace('#', '_')}-texture.png`;
          })
      )
    );

    this.modelData = data;
    this.textures = textures;
    this.boxCache.clear();
    this.phys.clear();
    this.charaProfile = data.metadata?.charaProfile || null;
    this._initVariables();
    this._initTimelines();
    this._initPhysicsMeta();
    this.mainTl = null;
    this.diffTl = null;
    this.diffFade = null;
    this.isLoaded = true;
    this._evaluateVariables(0);
    return this;
  }

  _initVariables() {
    const meta = this.modelData.metadata || {};
    this.varMeta.clear();
    this.targets = {};
    this.values = {};
    for (const m of meta.variableMetaInfoList || []) {
      this.varMeta.set(m.id, m);
      this.targets[m.id] = m.defaultValue ?? 0;
    }
    for (const v of meta.variableList || []) {
      if (!(v.label in this.targets)) this.targets[v.label] = 0;
    }
    this.instantVars = new Set(meta.instantVariableList || []);
    for (const imm of meta.immediateControl || []) this.instantVars.add(imm.label);

    this.selectors = [...(meta.customSelectorList || []), ...(meta.selectorControl || [])].filter((s) => s.enabled !== 0);
    this.selectorOptionLabels = new Set();
    this.selectorSources = new Set();
    for (const s of this.selectors) {
      this.selectorSources.add(s.label);
      for (const o of s.optionList || []) if (o.label) this.selectorOptionLabels.add(o.label);
    }
    this.clampControl = (meta.clampControl || []).filter((c) => c.enabled);
    this.eyeControl = (meta.eyeControl || []).find((e) => e.enabled) || null;
    this.rangeControls = new Map();
    for (const rc of meta.eyeControl || []) {
      if (!rc.enabled || !rc.edge) continue;
      for (const id of [rc.label, rc.varL, rc.varR]) {
        if (id) this.rangeControls.set(id, rc.edge);
      }
    }
    const eyeParam = (meta.eyeParameter || []).find((p) => p.label === this.eyeControl?.parameter);
    this.blinkCfg = {
      frames: eyeParam?.blinkFrameCount ?? this.eyeControl?.blinkFrameCount ?? 16,
      min: eyeParam?.blinkIntervalMin ?? 30,
      max: eyeParam?.blinkIntervalMax ?? 180,
    };
    this.values = { ...this.targets };
    this._applyInitTimeline();
  }

  _applyInitTimeline() {
    const init = (this.modelData.metadata?.timelineControl || []).find((t) => t.label === '初期化');
    if (!init) return;
    for (const track of init.variableList || []) {
      const f = (track.frameList || []).find((fr) => fr.content && typeof fr.content.value === 'number');
      if (f) this._setTarget(track.label, f.content.value);
    }
    this.values = { ...this.targets };
  }

  _initTimelines() {
    const list = this.modelData.metadata?.timelineControl || [];
    this.timelines = [];
    let group = '';
    for (const tl of list) {
      if (tl.label.startsWith('---')) {
        group = tl.label.replace(/[-<>]/g, '').trim();
        continue;
      }
      if (!tl.variableList?.length || tl.label === '初期化') continue;
      let last = 0;
      for (const track of tl.variableList) {
        for (const f of track.frameList || []) last = Math.max(last, f.time ?? 0);
      }
      const looping = tl.loopBegin >= 0 && tl.loopEnd > tl.loopBegin;
      this.timelines.push({
        label: tl.label,
        diff: tl.diff === 1,
        group: group || (tl.diff === 1 ? '仕草' : '表情'),
        lastTime: last,
        loopBegin: tl.loopBegin,
        loopEnd: tl.loopEnd,
        looping,
        tracks: tl.variableList,
      });
    }
  }

  _initPhysicsMeta() {
    const meta = this.modelData.metadata || {};
    this.velvety = new Map();
    for (const s of meta.velvetyStringControl || []) this.velvety.set(s.uid, { kind: 'string', cfg: s });
    for (const s of meta.velvetyJellyControl || []) this.velvety.set(s.uid, { kind: 'jelly', cfg: s });
    for (const s of meta.velvetySynchronizeControl || []) this.velvety.set(s.uid, { kind: 'sync', cfg: s });
  }

  // ---------------------------------------------------------------- variables
  getVariableInfo() {
    return [...this.varMeta.values()];
  }

  getVariable(name) {
    return this.vars[name] ?? this.targets[name] ?? 0;
  }

  getBaseVariable(name) {
    return this.targets[name] ?? 0;
  }

  _setTarget(name, value) {
    const m = this.varMeta.get(name);
    if (m && typeof m.min === 'number' && typeof m.max === 'number' && m.max > m.min) value = clamp(value, m.min, m.max);
    this.targets[name] = value;
    for (const dst of m?.proxyDests || []) this.targets[dst] = value;
  }

  /** User-driven change (slider). */
  setVariable(name, value, instant = false) {
    this._setTarget(name, value);
    if (instant || this.instantVars.has(name)) {
      this.values[name] = this.targets[name];
      for (const dst of this.varMeta.get(name)?.proxyDests || []) this.values[dst] = this.targets[dst];
    }
  }

  resetPose() {
    this.mainTl = null;
    this.stopGesture(false);
    for (const [id, m] of this.varMeta) {
      if (this.selectorSources.has(id) || this.selectorOptionLabels.has(id)) continue;
      if (id.startsWith('speed_')) continue;
      this.targets[id] = m.defaultValue ?? 0;
    }
    this._applyInitTimeline();
  }

  // ---------------------------------------------------------------- timelines
  getTimelines() {
    return this.timelines;
  }

  playTimeline(label) {
    const tl = this.timelines.find((t) => t.label === label);
    if (!tl) return false;
    const inst = { tl, time: 0, finished: false };
    if (tl.diff) {
      if (this.diffTl) this._startDiffFade();
      this.diffTl = inst;
    } else {
      // Reset non-selector base expression/pose targets so previous expression tracks (e.g. face_cheek, head_slant) don't stick
      for (const [id, m] of this.varMeta) {
        if (this.selectorSources.has(id) || this.selectorOptionLabels.has(id)) continue;
        if (id.startsWith('speed_') || id === 'arm_change') continue;
        this._setTarget(id, m.defaultValue ?? 0);
        if (this.instantVars.has(id) && !this.rangeControls.has(id)) {
          this.values[id] = this.targets[id];
        }
      }
      this.mainTl = inst;
      this._applyMainTimeline(0, true);
    }
    return true;
  }

  stopGesture(fade = true) {
    if (this.diffTl && fade) this._startDiffFade();
    this.diffTl = null;
  }

  getTimelineState() {
    const d = this.diffTl;
    return {
      main: this.mainTl?.tl.label || null,
      diff: d ? { label: d.tl.label, time: d.time, lastTime: d.tl.lastTime, looping: d.tl.looping, finished: d.finished } : null,
    };
  }

  _startDiffFade() {
    this.diffFade = { values: this._sampleDiff(this.diffTl), t: 0, dur: 0.3 };
  }

  _sampleTrack(track, t) {
    const fl = track.frameList || [];
    let prev = null, next = null;
    for (let i = 0; i < fl.length; i++) {
      const f = fl[i];
      if (!f.content || typeof f.content.value !== 'number') continue;
      if ((f.time ?? 0) <= t) prev = f;
      else {
        next = f;
        break;
      }
    }
    if (!prev) return next ? next.content.value : undefined;
    if (!next) return prev.content.value;
    const t0 = prev.time ?? 0, t1 = next.time ?? 0;
    const a = t1 > t0 ? clamp((t - t0) / (t1 - t0), 0, 1) : 1;
    return prev.content.value + (next.content.value - prev.content.value) * ease(prev.content.easing, a);
  }

  _sampleDiff(inst) {
    const out = {};
    if (!inst) return out;
    for (const track of inst.tl.tracks) {
      const v = this._sampleTrack(track, inst.time);
      if (v !== undefined) out[track.label] = v;
    }
    return out;
  }

  _sameContinuousRange(id, a, b) {
    const edges = this.rangeControls?.get(id);
    if (!edges) return false;
    for (let i = 0; i < edges.length; i++) {
      const [lo, hi] = edges[i];
      if (hi > lo && a >= lo && a <= hi && b >= lo && b <= hi) return true;
    }
    return false;
  }

  _applyMainTimeline(t, first) {
    const inst = this.mainTl;
    for (const track of inst.tl.tracks) {
      const v = this._sampleTrack(track, t);
      if (v === undefined) continue;
      this._setTarget(track.label, v);
      if (first && this.instantVars.has(track.label)) {
        const cur = this.values[track.label] ?? 0;
        const tgt = this.targets[track.label];
        if (!this._sameContinuousRange(track.label, cur, tgt)) {
          this.values[track.label] = tgt;
          for (const dst of this.varMeta.get(track.label)?.proxyDests || []) {
            this.values[dst] = this.targets[dst];
          }
        }
      }
    }
  }

  _advance(inst, dtTicks) {
    if (!inst || inst.finished) return;
    const tl = inst.tl;
    inst.time += dtTicks;
    if (tl.looping && inst.time > tl.loopEnd) {
      inst.time = tl.loopBegin + ((inst.time - tl.loopBegin) % (tl.loopEnd - tl.loopBegin));
    } else if (!tl.looping && inst.time >= tl.lastTime) {
      inst.time = tl.lastTime;
      inst.finished = true;
    }
  }

  // ------------------------------------------------------------------- update
  update(dt) {
    if (!this.isLoaded) return;
    dt = Math.min(dt, 0.1);
    this.idleTime += dt;
    this.physDt = dt;
    const ticks = dt * 60;

    if (this.mainTl && !this.mainTl.finished) {
      this._advance(this.mainTl, ticks);
      this._applyMainTimeline(this.mainTl.time, false);
    }
    this._advance(this.diffTl, ticks);
    if (this.diffFade) {
      this.diffFade.t += dt;
      if (this.diffFade.t >= this.diffFade.dur) this.diffFade = null;
    }

    // smooth base values toward targets
    const k = 1 - Math.exp(-dt * 12);
    for (const id in this.targets) {
      const tgt = this.targets[id];
      const cur = this.values[id];
      if (cur === undefined || this.selectorSources.has(id)) {
        this.values[id] = tgt;
      } else if (this.instantVars.has(id) && !this._sameContinuousRange(id, cur, tgt)) {
        this.values[id] = tgt;
      } else {
        this.values[id] = Math.abs(tgt - cur) < 1e-3 ? tgt : cur + (tgt - cur) * k;
      }
    }

    this._updateBlink(dt);
    this._updateTalk(dt);
    this._evaluateVariables(dt);
  }

  _updateBlink(dt) {
    const b = this.blink;
    if (!this.autoBlink || !this.eyeControl) {
      b.phase = -1;
      return;
    }
    if (b.phase < 0) {
      b.timer -= dt;
      if (b.timer <= 0) {
        b.phase = 0;
        b.dur = Math.max(0.1, this.blinkCfg.frames / 60);
      }
    } else {
      b.phase += dt / b.dur;
      if (b.phase >= 1) {
        b.phase = -1;
        const { min, max } = this.blinkCfg;
        b.timer = (min + Math.random() * (max - min)) / 60;
        if (Math.random() < 0.15) b.timer = 0.12; // occasional double blink
      }
    }
  }

  _updateTalk(dt) {
    if (this.isTalking) {
      this.talkPhase += dt;
      const s = 0.5 + 0.5 * Math.sin(this.talkPhase * 17.0) * Math.sin(this.talkPhase * 5.3 + 1.1);
      const target = s > 0.35 ? 1 + s * 4 : 0;
      this.talkValue += (target - this.talkValue) * Math.min(1, dt * 25);
    } else {
      this.talkValue += (0 - this.talkValue) * Math.min(1, dt * 20);
    }
  }

  _evaluateVariables() {
    const v = { ...this.values };

    // diff (additive gesture) timelines
    const addDiff = (contrib, w) => {
      for (const id in contrib) {
        if (this.selectorSources.has(id) || this.instantVars.has(id)) {
          if (w >= 1) {
            // discrete state switch (arm_type, cheek...) persists as base state
            this.targets[id] = contrib[id];
            v[id] = contrib[id];
            this.values[id] = contrib[id];
          }
        } else {
          v[id] = (v[id] ?? 0) + contrib[id] * w;
        }
      }
    };
    if (this.diffFade) addDiff(this.diffFade.values, 1 - this.diffFade.t / this.diffFade.dur);
    if (this.diffTl) addDiff(this._sampleDiff(this.diffTl), 1);

    // idle sway / breathing
    if (this.autoIdle) {
      const t = this.idleTime;
      v.head_LR = (v.head_LR ?? 0) + Math.sin(t * 0.55) * 3.5;
      v.head_UD = (v.head_UD ?? 0) + Math.sin(t * 0.8 + 1.3) * 2.5;
      v.body_UD = (v.body_UD ?? 0) + Math.sin(t * 1.6) * 3.0;
      v.body_LR = (v.body_LR ?? 0) + Math.sin(t * 0.4 + 0.5) * 2.0;
    }

    // mouse look-at
    if (this.lookAt) {
      const { x, y } = this.lookAt;
      v.head_LR = (v.head_LR ?? 0) + x * 55;
      v.head_UD = (v.head_UD ?? 0) + y * 45;
      v.body_LR = (v.body_LR ?? 0) + x * 20;
      v.face_eye_LR = (v.face_eye_LR ?? 0) + x * 70;
      v.face_eye_UD = (v.face_eye_UD ?? 0) + y * 60;
    }

    // blink (smoothly steps through open FFD [0..4] -> half-closed sprite & FFD [5..9] -> closed [10])
    if (this.blink.phase >= 0 && this.eyeControl) {
      const p = this.blink.phase;
      const closeAmt = p < 0.44 ? p / 0.44 : p < 0.56 ? 1 : 1 - (p - 0.56) / 0.44;
      const edge = this.eyeControl.edge?.[0] || [-10, 20];
      const halfFrame = this.eyeControl.node?.[0]?.[0] ?? 5;
      const endFrame = this.eyeControl.endFrame ?? 10;
      for (const id of [this.eyeControl.varL, this.eyeControl.varR, this.eyeControl.label]) {
        if (!id) continue;
        const cur = v[id] ?? 0;
        if (cur >= edge[0] && cur < endFrame) {
          if (closeAmt >= 0.96) {
            v[id] = endFrame;
          } else if (cur < halfFrame) {
            if (closeAmt < 0.48) {
              const u = closeAmt / 0.48;
              v[id] = cur + (halfFrame - 1 - cur) * u;
            } else {
              const u = (closeAmt - 0.48) / 0.48;
              v[id] = halfFrame + (endFrame - 1 - halfFrame) * u;
            }
          } else {
            const u = closeAmt / 0.96;
            v[id] = cur + (endFrame - 1 - cur) * u;
          }
        }
      }
    }

    // lip-sync
    if (this.talkValue > 0.01) v.face_talk = Math.max(v.face_talk ?? 0, this.talkValue);

    // clamp
    for (const c of this.clampControl) {
      const a = v[c.var_lr] ?? 0, b = v[c.var_ud] ?? 0;
      const len = Math.hypot(a, b);
      if (len > c.max) {
        v[c.var_lr] = (a / len) * c.max;
        v[c.var_ud] = (b / len) * c.max;
      }
    }
    for (const [id, m] of this.varMeta) {
      if (v[id] === undefined || typeof m.min !== 'number' || !(m.max > m.min)) continue;
      v[id] = clamp(v[id], m.min, m.max);
    }

    // selectors
    for (const s of this.selectors) {
      const val = Math.round(v[s.label] ?? 0);
      for (const o of s.optionList || []) {
        if (!o.label) continue;
        v[o.label] = val >= o.lower && val <= o.upper ? o.onValue : o.offValue;
      }
    }
    this.vars = v;
  }

  // -------------------------------------------------------------------- scene
  _box(src, icon) {
    const key = src + '|' + icon;
    let b = this.boxCache.get(key);
    if (b !== undefined) return b;
    b = null;
    if (src === 'blank') {
      const p = String(icon).split(':').map(Number);
      if (p.length === 4 && p[0] > 0 && p[1] > 0) b = { w: p[0], h: p[1], ox: p[2], oy: p[3], tex: null };
    } else if (src && src.startsWith('tex')) {
      const ic = this.modelData.source?.[src]?.icon?.[icon];
      if (ic && ic.width > 0 && ic.height > 0) {
        b = { w: ic.width, h: ic.height, ox: ic.originX || 0, oy: ic.originY || 0, tex: src, sx: ic.left || 0, sy: ic.top || 0 };
      }
    }
    this.boxCache.set(key, b);
    return b;
  }

  _paramTime(p) {
    const val = this.vars[p.id] ?? 0;
    const span = p.rangeEnd - p.rangeBegin;
    if (!span) return 0;
    let t = ((val - p.rangeBegin) / span) * p.division;
    t = clamp(t, 0, p.division);
    if (p.discretization) t = Math.round(t);
    return t;
  }

  /** Evaluate a layer frameList at time t. Returns null when the layer is absent. */
  _evalLayer(layer, t) {
    const fl = layer.frameList;
    if (!fl || fl.length === 0) return null;
    let i = 0;
    for (let k = 0; k < fl.length; k++) {
      if (fl[k].time <= t + 1e-4) i = k;
      else break;
    }
    let f = fl[i];
    if (f.type === 0 || !f.content) {
      // past the final terminator: hold the last real key
      if (i === fl.length - 1 && i > 0 && fl[i - 1].content && fl[i - 1].type !== 0) {
        f = fl[i - 1];
        i -= 1;
      } else return null;
    }
    const c = f.content;
    const out = {
      src: c.src,
      icon: c.icon,
      coord: c.coord || null,
      angle: c.angle || 0,
      opa: c.opa ?? 255,
      cw: c.cw,
      bp: c.mesh?.bp || null,
      bm: c.bm || 0,
    };
    const n = fl[i + 1];
    if (f.type === 3 && n && n.type !== 0 && n.content && n.time > f.time) {
      const a = clamp((t - f.time) / (n.time - f.time), 0, 1);
      if (a > 0) {
        const nc = n.content;
        const c0 = c.coord || [0, 0, 0], c1 = nc.coord || [0, 0, 0];
        if (c.coord || nc.coord) {
          out.coord = [c0[0] + (c1[0] - c0[0]) * a, c0[1] + (c1[1] - c0[1]) * a, (c0[2] || 0) + ((c1[2] || 0) - (c0[2] || 0)) * a];
        }
        if (c.angle || nc.angle) out.angle = lerpAngle(c.angle || 0, nc.angle || 0, a);
        const o1 = nc.opa ?? 255;
        out.opa = out.opa + (o1 - out.opa) * a;
        if (c.cw !== undefined || nc.cw !== undefined) {
          const w0 = decodeCw(c.cw), w1 = decodeCw(nc.cw);
          out.cwv = [0, 1, 2, 3].map((k) => w0[k] + (w1[k] - w0[k]) * a);
        }
        const b0 = c.mesh?.bp, b1 = nc.mesh?.bp;
        if (b0 || b1) {
          const bp = new Float32Array(32);
          for (let k = 0; k < 32; k++) {
            const x0 = b0 ? b0[k] : BEZ_ID[k];
            const x1 = b1 ? b1[k] : BEZ_ID[k];
            bp[k] = x0 + (x1 - x0) * a;
          }
          out.bp = bp;
        }
      }
    }
    if (!out.cwv) out.cwv = decodeCw(out.cw);
    return out;
  }

  /** Build the full deformed scene for the current variables. */
  buildScene() {
    if (!this.isLoaded) return null;
    const data = this.modelData;
    const base = data.metadata?.base || { chara: 'all_parts', motion: 'タイムライン構造' };

    const sprites = [];
    const stencils = [];
    const labelIndex = new Map(); // motionInst|label -> [uid]
    let uidCounter = 0;
    let motionCounter = 0;
    let order = 0;
    const self = this;

    function walkMotion(objName, motionName, st, path, depth) {
      if (depth > 16) return;
      const m = data.object?.[objName]?.motion?.[motionName];
      if (!m) return;
      const times = (m.parameter || []).map((p) => self._paramTime(p));
      const defTime = times.length ? times[0] : 0;
      const minst = ++motionCounter;
      const layers = m.layer || [];
      for (let i = 0; i < layers.length; i++) {
        walkLayer(layers[i], { ...st, comb: null }, times, defTime, minst, objName, path + '/' + i, depth);
      }
    }

    function walkLayer(layer, st, times, defTime, minst, objName, path, depth) {
      const t = layer.parameterize !== null && layer.parameterize !== undefined ? times[layer.parameterize] ?? defTime : defTime;
      const fc = self._evalLayer(layer, t);
      if (!fc) return;

      const uid = ++uidCounter;
      const key = minst + '|' + layer.label;
      let arr = labelIndex.get(key);
      if (!arr) labelIndex.set(key, (arr = []));
      arr.push(uid);

      const chain = { uid, prev: st.chain };
      const opa = st.opa * (fc.opa / 255);
      const cwv = fc.cwv;
      const cw = cwv === NEUTRAL_CW ? st.cw : [st.cw[0] * cwv[0], st.cw[1] * cwv[1], st.cw[2] * cwv[2], st.cw[3] * cwv[3]];
      let z = st.z + (fc.coord && fc.coord.length > 2 ? fc.coord[2] || 0 : 0);
      let xf = st.xf;
      let sync = st.sync;
      let comb = null;
      let stencilKey = st.stencilKey;

      const type = layer.type;
      if (type === 2 || (type === 0 && fc.coord)) {
        let tx = fc.coord ? fc.coord[0] : 0;
        let ty = fc.coord ? fc.coord[1] : 0;
        if (sync && fc.coord) {
          evalPatch(sync, tx, ty, _tmp);
          tx = _tmp[0];
          ty = _tmp[1];
          sync = null;
        }
        if (tx || ty || fc.angle) {
          const r = (fc.angle * Math.PI) / 180;
          const cs = Math.cos(r), sn = Math.sin(r);
          xf = pushAffine(xf, cs, sn, -sn, cs, tx, ty);
        }
      }

      if (type === 0) {
        const box = self._box(fc.src, fc.icon);
        if (box) {
          let own = makePatch(box, fc.bp);
          if (layer.label === 'EMBED' && self.enablePhysics && layer.uid && self.velvety.has(layer.uid)) {
            own = self._physicsPatch(layer.uid, path, box, own, xf);
          }
          let patch, baseXf, baseSync, ms;
          if (layer.meshCombine === 1 && st.comb && boxEq(st.comb.box, box)) {
            patch = combinePatch(st.comb.patch, own);
            baseXf = st.comb.baseXf;
            baseSync = st.comb.baseSync;
            ms = st.comb.ms | (layer.meshSyncChildMask ?? 0);
          } else {
            patch = own;
            baseXf = xf;
            baseSync = sync;
            ms = layer.meshSyncChildMask ?? 0;
          }
          if (box.tex && self.textures[box.tex]) {
            sprites.push({
              uid,
              chain,
              img: self.textures[box.tex],
              box,
              patch,
              xf: baseXf,
              opacity: opa,
              colorWeight: [Math.min(cw[0], 2), Math.min(cw[1], 2), Math.min(cw[2], 2), Math.min(cw[3], 1)],
              bm: fc.bm,
              z,
              order: order++,
              stencilKey,
              obj: objName,
              label: layer.label,
              path,
            });
          }
          if (ms & 8) {
            xf = pushPatch(baseXf, patch);
            sync = baseSync;
          } else if (ms & 1) {
            xf = baseXf;
            sync = patch.identity ? baseSync : patch;
          } else {
            xf = baseXf;
            sync = baseSync;
          }
          comb = { box, patch, baseXf, baseSync, ms };
        }
      } else if (type === 12) {
        const list = layer.stencilCompositeMaskLayerList || [];
        if (list.length) {
          stencilKey = 's' + uid;
          stencils.push({ key: stencilKey, uid, minst, labels: list });
        }
      }

      const childState = { xf, sync, comb, opa, cw, z, chain, stencilKey };

      if (type === 3 && fc.src && data.object?.[fc.src]) {
        walkMotion(fc.src, fc.icon, childState, path + '>', depth + 1);
      }

      const kids = layer.children || [];
      for (let i = 0; i < kids.length; i++) {
        walkLayer(kids[i], childState, times, defTime, minst, objName, path + '.' + i, depth);
      }
    }

    walkMotion(
      base.chara,
      base.motion,
      { xf: null, sync: null, comb: null, opa: 1, cw: NEUTRAL_CW, z: 0, chain: null, stencilKey: null },
      '',
      0
    );

    // compute deformed vertices (only for visible sprites)
    const N = GRID_N;
    const invN = 1 / N;
    const vertLen = (N + 1) * (N + 1) * 2;
    let poolIdx = 0;
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    let fMinX = Infinity, fMinY = Infinity, fMaxX = -Infinity, fMaxY = -Infinity;
    const pt = new Float64Array(2);
    const drawList = [];
    const activeStencilKeys = new Set();

    for (let si = 0; si < sprites.length; si++) {
      const s = sprites[si];
      const effAlpha = s.opacity * s.colorWeight[3];
      if (effAlpha <= 0.002) {
        s.vertices = null;
        continue;
      }
      let verts = this.vertPool[poolIdx];
      if (!verts || verts.length !== vertLen) {
        verts = this.vertPool[poolIdx] = new Float32Array(vertLen);
      }
      poolIdx++;

      const { w, h, ox, oy } = s.box;
      const stepX = w * invN;
      const stepY = h * invN;
      let p = 0;
      const countBounds = effAlpha > 0.01 && s.bm === 0;
      const isFace = s.obj === 'face_parts';
      const xf = s.xf;
      const patch = s.patch;
      const simpleAffine = patch.identity && xf && xf.kind === 0 && !xf.parent;

      if (simpleAffine) {
        const { a, b, c, d, e, f } = xf;
        for (let r = 0; r <= N; r++) {
          const ly = -oy + r * stepY;
          const baseX = c * ly + e;
          const baseY = d * ly + f;
          for (let col = 0; col <= N; col++) {
            const lx = -ox + col * stepX;
            const wx = a * lx + baseX;
            const wy = b * lx + baseY;
            verts[p++] = wx;
            verts[p++] = wy;
            if (countBounds) {
              if (wx < minX) minX = wx;
              if (wx > maxX) maxX = wx;
              if (wy < minY) minY = wy;
              if (wy > maxY) maxY = wy;
              if (isFace) {
                if (wx < fMinX) fMinX = wx;
                if (wx > fMaxX) fMaxX = wx;
                if (wy < fMinY) fMinY = wy;
                if (wy > fMaxY) fMaxY = wy;
              }
            }
          }
        }
      } else {
        for (let r = 0; r <= N; r++) {
          const ly = -oy + r * stepY;
          for (let col = 0; col <= N; col++) {
            let lx = -ox + col * stepX;
            let ly2 = ly;
            if (!patch.identity) {
              evalPatch(patch, lx, ly2, pt);
              lx = pt[0];
              ly2 = pt[1];
            }
            applyXf(xf, lx, ly2, pt);
            const wx = pt[0];
            const wy = pt[1];
            verts[p++] = wx;
            verts[p++] = wy;
            if (countBounds) {
              if (wx < minX) minX = wx;
              if (wx > maxX) maxX = wx;
              if (wy < minY) minY = wy;
              if (wy > maxY) maxY = wy;
              if (isFace) {
                if (wx < fMinX) fMinX = wx;
                if (wx > fMaxX) fMaxX = wx;
                if (wy < fMinY) fMinY = wy;
                if (wy > fMaxY) fMaxY = wy;
              }
            }
          }
        }
      }
      s.vertices = verts;
      s.sx = s.box.sx;
      s.sy = s.box.sy;
      s.sw = s.box.w;
      s.sh = s.box.h;
      drawList.push(s);
      if (s.stencilKey) activeStencilKeys.add(s.stencilKey);
    }

    // stencil masks (only build for stencilKeys actively referenced in drawList)
    const maskMap = new Map();
    for (let si = 0; si < stencils.length; si++) {
      const stc = stencils[si];
      if (!activeStencilKeys.has(stc.key)) continue;
      const uids = new Set();
      for (const lbl of stc.labels) {
        const list = labelIndex.get(stc.minst + '|' + lbl);
        if (list) for (let u = 0; u < list.length; u++) uids.add(list[u]);
      }
      const masks = [];
      if (uids.size) {
        for (let k = 0; k < drawList.length; k++) {
          const s = drawList[k];
          let hasStc = false;
          let matchesUid = false;
          for (let node = s.chain; node !== null; node = node.prev) {
            if (node.uid === stc.uid) {
              hasStc = true;
              break;
            }
            if (!matchesUid && uids.has(node.uid)) {
              matchesUid = true;
            }
          }
          if (!hasStc && matchesUid) masks.push(s);
        }
      }
      maskMap.set(stc.key, masks);
    }

    drawList.sort(
      (a, b) =>
        a.z - b.z ||
        (a.stencilKey && b.stencilKey && a.stencilKey !== b.stencilKey ? (a.stencilKey < b.stencilKey ? -1 : 1) : 0) ||
        a.order - b.order
    );

    const bounds = minX < Infinity ? { minX, minY, maxX, maxY } : null;
    const face = fMinX < Infinity ? { minX: fMinX, minY: fMinY, maxX: fMaxX, maxY: fMaxY } : null;
    this.activeDrawCount = drawList.length;
    this.lastScene = { drawList, maskMap, bounds, face, spriteCount: sprites.length };
    return this.lastScene;
  }

  getComputedBoxes() {
    const sc = this.lastScene || this.buildScene();
    if (!sc || !sc.bounds) return null;
    const b = sc.bounds;
    const full = {
      minX: b.minX,
      maxX: b.maxX,
      minY: b.minY,
      maxY: b.maxY,
      width: b.maxX - b.minX,
      height: b.maxY - b.minY,
      centerX: (b.minX + b.maxX) / 2,
      centerY: (b.minY + b.maxY) / 2,
    };

    const upperH = full.height * 0.48;
    const upperW = full.width * 0.88;
    const upper = {
      minX: full.centerX - upperW / 2,
      maxX: full.centerX + upperW / 2,
      minY: b.minY - 30,
      maxY: b.minY + upperH,
      width: upperW,
      height: upperH + 30,
      centerX: full.centerX,
      centerY: b.minY + upperH / 2,
    };

    let face;
    if (sc.face) {
      const f = sc.face;
      const padX = 110;
      const padY = 120;
      const fMinX = f.minX - padX;
      const fMaxX = f.maxX + padX;
      const fMinY = f.minY - padY * 1.15;
      const fMaxY = f.maxY + padY * 0.85;
      face = {
        minX: fMinX,
        maxX: fMaxX,
        minY: fMinY,
        maxY: fMaxY,
        width: fMaxX - fMinX,
        height: fMaxY - fMinY,
        centerX: (fMinX + fMaxX) / 2,
        centerY: (fMinY + fMaxY) / 2,
      };
    } else {
      const faceW = full.width * 0.45;
      const faceH = full.height * 0.22;
      face = {
        minX: full.centerX - faceW / 2,
        maxX: full.centerX + faceW / 2,
        minY: b.minY - 20,
        maxY: b.minY + faceH,
        width: faceW,
        height: faceH + 20,
        centerX: full.centerX,
        centerY: b.minY + faceH / 2,
      };
    }

    return { full, upper, face };
  }

  // ------------------------------------------------------------------ physics
  // Lightweight approximation of velvety string/jelly physics: a damped spring driven by the
  // world-space acceleration of the EMBED anchor, bending the patch away from its fixed edge.
  _physicsPatch(uid, path, box, own, xf) {
    const info = this.velvety.get(uid);
    const dt = this.physDt;
    let st = this.phys.get(path);
    const pt = new Float64Array(2);
    applyXf(xf, 0, -box.oy, pt);
    if (!st) {
      st = { px: pt[0], py: pt[1], vx: 0, vy: 0, dx: 0, dy: 0, ddx: 0, ddy: 0, init: true };
      this.phys.set(path, st);
    }
    let dx, dy;
    if (info.kind === 'sync') {
      const src = [...this.phys.entries()].find(([, s]) => s.uid === info.cfg.sourceUid);
      const w = info.cfg.weight ?? 1;
      dx = src ? src[1].dx * w : 0;
      dy = src ? src[1].dy * w : 0;
    } else {
      st.uid = uid;
      const cfg = info.cfg;
      const speed = (this.vars.speed_physics ?? 1) * (cfg.speed ?? 1);
      if (dt > 0 && speed > 0) {
        const elastic = info.kind === 'string' ? cfg.elastic ?? 6 : ((cfg.elasticH ?? 6) + (cfg.elasticV ?? 6)) / 2;
        const friction = info.kind === 'string' ? ((cfg.frictionTop ?? 8) + (cfg.frictionBottom ?? 8)) / 2 : cfg.friction ?? 8;
        const kSpring = 18 + elastic * 9;
        const damp = 2.2 + friction * 0.35;
        const sens = (cfg.sensitivity ?? 1) * 0.9;
        const vx = (pt[0] - st.px) / dt, vy = (pt[1] - st.py) / dt;
        const ax = st.init ? 0 : (vx - st.vx) / dt, ay = st.init ? 0 : (vy - st.vy) / dt;
        st.init = false;
        st.px = pt[0];
        st.py = pt[1];
        st.vx = vx;
        st.vy = vy;
        const sub = 4;
        const h = (dt * speed) / sub;
        for (let i = 0; i < sub; i++) {
          const fx = -kSpring * st.dx - damp * st.ddx - ax * sens * 0.02;
          const fy = -kSpring * st.dy - damp * st.ddy - ay * sens * 0.02;
          st.ddx += fx * h;
          st.ddy += fy * h;
          st.dx += st.ddx * h;
          st.dy += st.ddy * h;
        }
        const lim = 0.35;
        st.dx = clamp(st.dx, -lim * box.w, lim * box.w);
        st.dy = clamp(st.dy, -lim * box.h, lim * box.h);
      }
      dx = st.dx;
      dy = st.dy;
    }
    if (Math.abs(dx) < 0.05 && Math.abs(dy) < 0.05) return own;
    const P = new Float32Array(own.P);
    const ux = dx / box.w, vy = dy / box.h;
    for (let r = 0; r < 4; r++) {
      for (let c = 0; c < 4; c++) {
        let wgt;
        if (info.kind === 'jelly') {
          wgt = (r === 1 || r === 2) && (c === 1 || c === 2) ? 1 : 0.35;
        } else {
          wgt = Math.pow(r / 3, 1.6);
        }
        P[(r * 4 + c) * 2] += ux * wgt;
        P[(r * 4 + c) * 2 + 1] += vy * wgt * (info.kind === 'jelly' ? 1 : 0.35);
      }
    }
    return { w: own.w, h: own.h, ox: own.ox, oy: own.oy, P, identity: false };
  }
}
