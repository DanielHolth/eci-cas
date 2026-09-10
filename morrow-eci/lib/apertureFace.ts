import type { Expression } from "@/types/events";

/**
 * The machine face, as a renderer with no framework in it.
 *
 * This is a port of the aperture-face prototype: an iris with nine blades, a
 * 48-cell telemetry ring and two brow bars, the whole thing one fragment
 * shader with no SVG, no sprite sheet and no animation library. It runs on
 * WebGPU where the browser has it and falls back to WebGL2, which is the
 * same pair of paths the prototype proved.
 *
 * It is deliberately not a component. React owns when this exists and what
 * mood it is in; it owns the frame loop, the GPU objects and the easing, and
 * those have no business re-running because a parent re-rendered. The
 * handle is the whole contract: set a mood, set whether it is speaking,
 * destroy it.
 *
 * **The face reads degree, not just category.** Impulse appraises alertness
 * and warmth as scalars and then collapses them into one of six words. The
 * table below carries both back: each word owns a colour pair, a brow angle
 * and an aperture, but also the two scalars it was collapsed from, which
 * drive blink rate, ring density and bloom. That is why `alert` does not
 * look like `scared` even though both open the iris wide.
 */
const FACE: Record<
  Expression,
  { a: [number, number, number]; b: [number, number, number]; brow: number; aper: number; alert: number; warm: number }
> = {
  angry:   { a: [0.973, 0.443, 0.443], b: [0.725, 0.110, 0.110], brow:  0.244, aper: 0.30, alert: 0.70, warm: 0.10 },
  scared:  { a: [0.753, 0.518, 0.988], b: [0.427, 0.157, 0.851], brow: -0.244, aper: 0.95, alert: 0.90, warm: 0.15 },
  sad:     { a: [0.576, 0.773, 0.992], b: [0.114, 0.306, 0.847], brow: -0.175, aper: 0.20, alert: 0.20, warm: 0.30 },
  warm:    { a: [0.988, 0.827, 0.302], b: [0.851, 0.467, 0.024], brow: -0.070, aper: 0.60, alert: 0.35, warm: 0.90 },
  alert:   { a: [0.992, 0.729, 0.455], b: [0.918, 0.345, 0.047], brow: -0.035, aper: 1.00, alert: 1.00, warm: 0.40 },
  neutral: { a: [0.796, 0.835, 0.882], b: [0.392, 0.455, 0.545], brow:  0.000, aper: 0.50, alert: 0.30, warm: 0.50 },
};

// One shader body, written twice. Uniforms are packed as four vec4s so the
// WGSL and GLSL versions agree on layout without any padding arithmetic:
//   R = resolution.xy, time, speaking
//   A = mood light rgb, brow radians
//   B = mood deep rgb, aperture 0..1
//   M = alertness, warmth, gaze.xy
const BODY = `
float ngon(vec2 p, float r, float n, float ro) {
  float a = atan(p.y, p.x) + ro;
  float b = 6.28318530718 / n;
  return cos(floor(0.5 + a / b) * b - a) * length(p) - r;
}
float seg(vec2 p, vec2 a, vec2 b) {
  vec2 pa = p - a, ba = b - a;
  float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
  return length(pa - ba * h);
}
float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
`;

const SCENE = `
vec3 scene(vec2 fc, vec4 R, vec4 A, vec4 B, vec4 M) {
  float mn = min(R.x, R.y);
  vec2 p = (fc - 0.5 * R.xy) / mn;
  float t = R.z, speak = R.w;
  float brow = A.w, aperK = B.w, alert = M.x, warm = M.y;
  vec2 gaze = M.zw;
  float r = length(p);
  float px = 1.5 / mn;

  float breath = 0.5 + 0.5 * sin(t * (0.7 + alert * 1.4));

  vec3 col = B.rgb * 0.07 * exp(-r * 2.1);

  float hd = r - 0.40;
  float housing = smoothstep(px, -px, hd);
  col = mix(col, vec3(0.035, 0.045, 0.060), housing);
  col += A.rgb * smoothstep(0.008, 0.0, abs(hd)) * (0.35 + 0.35 * warm);

  float rr = r - 0.455;
  float band = smoothstep(0.011, 0.0, abs(rr));
  float ang = (atan(p.y, p.x) + 3.14159265) / 6.28318530718;
  float idx = floor(ang * 48.0);
  float cell = fract(ang * 48.0);
  float tick = smoothstep(0.52, 0.30, abs(cell - 0.5));
  float lit = step(hash(vec2(idx, floor(t * 2.5 + idx * 0.07))), 0.08 + alert * 0.72);
  col += A.rgb * band * tick * (0.10 + 0.70 * lit);

  float ap = mix(0.075, 0.225, aperK) * (1.0 + 0.025 * breath);
  float blades = ngon(p, ap, 9.0, t * 0.09);
  float irisMask = smoothstep(px, -px, r - 0.335) * smoothstep(-px, px, blades);
  vec3 iris = mix(B.rgb * 0.30, A.rgb * (0.55 + 0.45 * warm), smoothstep(0.34, 0.05, r));
  col = mix(col, iris, irisMask);

  float sa = fract((atan(p.y, p.x) + t * 0.09) / (6.28318530718 / 9.0));
  col += A.rgb * 0.30 * smoothstep(0.46, 0.5, abs(sa - 0.5)) * irisMask;
  col += A.rgb * smoothstep(0.005, 0.0, abs(blades)) * 1.15 * smoothstep(px, -px, r - 0.345);

  vec2 gp = p - gaze * 0.05;
  float pu = length(gp) - ap * 0.60;
  col = mix(col, vec3(0.012, 0.016, 0.026), smoothstep(px, -px, pu));
  col += A.rgb * smoothstep(0.004, 0.0, abs(pu)) * 1.3;
  col += vec3(1.0) * 0.45 * smoothstep(0.022, 0.0, length(gp - vec2(-0.022, 0.026)));

  float wv = 0.5 + 0.5 * sin(r * 74.0 - t * 9.5);
  col += A.rgb * speak * wv * 0.13 * smoothstep(0.40, 0.10, r) * housing;

  vec2 bl = p - vec2(-0.170, 0.285);
  bl = vec2(bl.x * cos(-brow) - bl.y * sin(-brow), bl.x * sin(-brow) + bl.y * cos(-brow));
  vec2 br = p - vec2(0.170, 0.285);
  br = vec2(br.x * cos(brow) - br.y * sin(brow), br.x * sin(brow) + br.y * cos(brow));
  float bw = min(seg(bl, vec2(-0.082, 0.0), vec2(0.082, 0.0)),
                 seg(br, vec2(-0.082, 0.0), vec2(0.082, 0.0))) - 0.013;
  col = mix(col, A.rgb * 1.05, smoothstep(px, -px, bw));
  col += A.rgb * 0.30 * exp(-max(bw, 0.0) * 38.0);

  float cyc = fract(t * (0.16 + alert * 0.20));
  float bl2 = exp(-pow((cyc - 0.06) / 0.022, 2.0));
  float lid = bl2 * 0.41;
  float cover = smoothstep(0.0, 0.012, abs(p.y) - (0.40 - lid));
  col = mix(col, vec3(0.030, 0.038, 0.052), cover * housing);

  col *= 0.93 + 0.07 * sin(fc.y * 1.7 + t * 1.6);
  col *= 1.0 - 0.40 * smoothstep(0.42, 1.05, r);
  col += (hash(fc + fract(t)) - 0.5) * 0.028;
  return col;
}
`;

const GLSL_FRAG = `#version 300 es
precision highp float;
out vec4 outColor;
uniform vec4 R; uniform vec4 A; uniform vec4 B; uniform vec4 M;
${BODY}
${SCENE}
void main() { outColor = vec4(scene(gl_FragCoord.xy, R, A, B, M), 1.0); }`;

const GLSL_VERT = `#version 300 es
void main() {
  vec2 v = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
  gl_Position = vec4(v * 2.0 - 1.0, 0.0, 1.0);
}`;

const WGSL = `
struct U { R: vec4<f32>, A: vec4<f32>, B: vec4<f32>, M: vec4<f32> };
@group(0) @binding(0) var<uniform> u: U;

fn ngon(p: vec2<f32>, r: f32, n: f32, ro: f32) -> f32 {
  let a = atan2(p.y, p.x) + ro;
  let b = 6.28318530718 / n;
  return cos(floor(0.5 + a / b) * b - a) * length(p) - r;
}
fn seg(p: vec2<f32>, a: vec2<f32>, b: vec2<f32>) -> f32 {
  let pa = p - a; let ba = b - a;
  let h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
  return length(pa - ba * h);
}
fn hash(p: vec2<f32>) -> f32 { return fract(sin(dot(p, vec2<f32>(12.9898, 78.233))) * 43758.5453); }

@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
  var v = vec2<f32>(f32((i << 1u) & 2u), f32(i & 2u));
  return vec4<f32>(v * 2.0 - 1.0, 0.0, 1.0);
}

@fragment fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
  let R = u.R; let A = u.A; let B = u.B; let M = u.M;
  // WebGPU's framebuffer origin is top-left; GLSL's is bottom-left. Flip
  // once here so the rest of the body is the same arithmetic.
  let fc = vec2<f32>(pos.x, R.y - pos.y);
  let mn = min(R.x, R.y);
  let p = (fc - 0.5 * R.xy) / mn;
  let t = R.z; let speak = R.w;
  let brow = A.w; let aperK = B.w; let alert = M.x; let warm = M.y;
  let gaze = M.zw;
  let r = length(p);
  let px = 1.5 / mn;

  let breath = 0.5 + 0.5 * sin(t * (0.7 + alert * 1.4));
  var col = B.rgb * 0.07 * exp(-r * 2.1);

  let hd = r - 0.40;
  let housing = smoothstep(-px, px, -hd);
  col = mix(col, vec3<f32>(0.035, 0.045, 0.060), housing);
  col = col + A.rgb * smoothstep(0.0, 0.008, 0.008 - abs(hd)) * (0.35 + 0.35 * warm);

  let rr = r - 0.455;
  let band = smoothstep(0.0, 0.011, 0.011 - abs(rr));
  let ang = (atan2(p.y, p.x) + 3.14159265) / 6.28318530718;
  let idx = floor(ang * 48.0);
  let cell = fract(ang * 48.0);
  let tick = 1.0 - smoothstep(0.30, 0.52, abs(cell - 0.5));
  let lit = step(hash(vec2<f32>(idx, floor(t * 2.5 + idx * 0.07))), 0.08 + alert * 0.72);
  col = col + A.rgb * band * tick * (0.10 + 0.70 * lit);

  let ap = mix(0.075, 0.225, aperK) * (1.0 + 0.025 * breath);
  let blades = ngon(p, ap, 9.0, t * 0.09);
  let irisMask = smoothstep(-px, px, 0.335 - r) * smoothstep(-px, px, blades);
  let iris = mix(B.rgb * 0.30, A.rgb * (0.55 + 0.45 * warm), 1.0 - smoothstep(0.05, 0.34, r));
  col = mix(col, iris, irisMask);

  let sa = fract((atan2(p.y, p.x) + t * 0.09) / (6.28318530718 / 9.0));
  col = col + A.rgb * 0.30 * smoothstep(0.46, 0.5, abs(sa - 0.5)) * irisMask;
  col = col + A.rgb * (1.0 - smoothstep(0.0, 0.005, abs(blades))) * 1.15 * smoothstep(-px, px, 0.345 - r);

  let gp = p - gaze * 0.05;
  let pu = length(gp) - ap * 0.60;
  col = mix(col, vec3<f32>(0.012, 0.016, 0.026), smoothstep(-px, px, -pu));
  col = col + A.rgb * (1.0 - smoothstep(0.0, 0.004, abs(pu))) * 1.3;
  col = col + vec3<f32>(1.0) * 0.45 * (1.0 - smoothstep(0.0, 0.022, length(gp - vec2<f32>(-0.022, 0.026))));

  let wv = 0.5 + 0.5 * sin(r * 74.0 - t * 9.5);
  col = col + A.rgb * speak * wv * 0.13 * (1.0 - smoothstep(0.10, 0.40, r)) * housing;

  var bl = p - vec2<f32>(-0.170, 0.285);
  bl = vec2<f32>(bl.x * cos(-brow) - bl.y * sin(-brow), bl.x * sin(-brow) + bl.y * cos(-brow));
  var br = p - vec2<f32>(0.170, 0.285);
  br = vec2<f32>(br.x * cos(brow) - br.y * sin(brow), br.x * sin(brow) + br.y * cos(brow));
  let bw = min(seg(bl, vec2<f32>(-0.082, 0.0), vec2<f32>(0.082, 0.0)),
               seg(br, vec2<f32>(-0.082, 0.0), vec2<f32>(0.082, 0.0))) - 0.013;
  col = mix(col, A.rgb * 1.05, smoothstep(-px, px, -bw));
  col = col + A.rgb * 0.30 * exp(-max(bw, 0.0) * 38.0);

  let cyc = fract(t * (0.16 + alert * 0.20));
  let bl2 = exp(-pow((cyc - 0.06) / 0.022, 2.0));
  let lid = bl2 * 0.41;
  let cover = smoothstep(0.0, 0.012, abs(p.y) - (0.40 - lid));
  col = mix(col, vec3<f32>(0.030, 0.038, 0.052), cover * housing);

  col = col * (0.93 + 0.07 * sin(fc.y * 1.7 + t * 1.6));
  col = col * (1.0 - 0.40 * smoothstep(0.42, 1.05, r));
  col = col + (hash(fc + fract(t)) - 0.5) * 0.028;
  return vec4<f32>(col, 1.0);
}`;

export type Backend = "webgpu" | "webgl2" | "none";

export interface FaceHandle {
  setMood(expression: Expression): void;
  setSpeaking(speaking: boolean): void;
  /** Which path actually started, once one has. Null until then. */
  backend(): Backend | null;
  destroy(): void;
}

/**
 * Start the face on a canvas. Returns immediately with a live handle —
 * WebGPU's adapter request is async, and a caller should not have to await a
 * GPU to decide what mood to be in. Calls made before the backend is up land
 * on the state object and are simply there when the first frame runs.
 *
 * `onBackend` reports which path won, or "none" if neither did, so the
 * caller can put a drawn face up instead. That fallback matters: the
 * prototype had no fallback drawing to show, but the app does.
 */
export function mountApertureFace(
  canvas: HTMLCanvasElement,
  initial: { expression: Expression; speaking?: boolean },
  onBackend?: (backend: Backend) => void,
): FaceHandle {
  const reduced =
    typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;

  const start = FACE[initial.expression];
  // Current and target. Everything eases from one to the other, so a change
  // of mood reads as the face moving into it rather than cutting.
  const S = {
    a: [...start.a] as number[],
    b: [...start.b] as number[],
    brow: start.brow,
    aper: start.aper,
    alert: start.alert,
    warm: start.warm,
    speak: initial.speaking ? 1 : 0,
    gaze: [0, 0],
  };
  const T = {
    a: [...start.a] as number[],
    b: [...start.b] as number[],
    brow: start.brow,
    aper: start.aper,
    alert: start.alert,
    warm: start.warm,
    speak: initial.speaking ? 1 : 0,
    gaze: [0, 0],
  };

  let disposed = false;
  let raf = 0;
  let backend: Backend | null = null;

  const lerp = (a: number, b: number, k: number) => a + (b - a) * k;

  function step(dt: number) {
    const k = reduced ? 1 : Math.min(1, dt * 3.4);
    for (let i = 0; i < 3; i++) {
      S.a[i] = lerp(S.a[i], T.a[i], k);
      S.b[i] = lerp(S.b[i], T.b[i], k);
    }
    S.brow = lerp(S.brow, T.brow, k);
    S.aper = lerp(S.aper, T.aper, k);
    S.alert = lerp(S.alert, T.alert, k);
    S.warm = lerp(S.warm, T.warm, k);
    S.speak = lerp(S.speak, T.speak, Math.min(1, dt * 8));
    S.gaze[0] = lerp(S.gaze[0], T.gaze[0], Math.min(1, dt * 5));
    S.gaze[1] = lerp(S.gaze[1], T.gaze[1], Math.min(1, dt * 5));
  }

  const U = new Float32Array(16);
  function pack(w: number, h: number, t: number) {
    U[0] = w; U[1] = h; U[2] = reduced ? 0 : t; U[3] = S.speak;
    U[4] = S.a[0]; U[5] = S.a[1]; U[6] = S.a[2]; U[7] = S.brow;
    U[8] = S.b[0]; U[9] = S.b[1]; U[10] = S.b[2]; U[11] = S.aper;
    U[12] = S.alert; U[13] = S.warm; U[14] = S.gaze[0]; U[15] = S.gaze[1];
  }

  const dpr = Math.min(typeof devicePixelRatio === "number" ? devicePixelRatio : 1, 2);
  function sizeTo() {
    const rect = canvas.getBoundingClientRect();
    const w = Math.max(1, Math.round(rect.width * dpr));
    const h = Math.max(1, Math.round(rect.height * dpr));
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
    }
  }

  // The gaze follows the pointer anywhere on the page, not just over the
  // canvas: the face is small and sits above the transcript, so a pointer
  // that only counted while it was on top of the face would almost never
  // count at all. Passive, because this never prevents a default.
  function steer(event: PointerEvent) {
    const rect = canvas.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return;
    const cx = rect.left + rect.width / 2;
    const cy = rect.top + rect.height / 2;
    // Normalised against a comfortable arm's reach rather than the canvas
    // itself, which is ~100px: dividing by the canvas would peg the gaze at
    // the clamp for any pointer position outside the face.
    const reach = Math.max(rect.width * 4, 240);
    T.gaze = [
      Math.max(-1, Math.min(1, (event.clientX - cx) / reach)),
      Math.max(-1, Math.min(1, -(event.clientY - cy) / reach)),
    ];
  }
  window.addEventListener("pointermove", steer, { passive: true });

  function loop(render: (t: number) => void) {
    let last = performance.now();
    const frame = (now: number) => {
      if (disposed) return;
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      sizeTo();
      step(dt);
      pack(canvas.width, canvas.height, now / 1000);
      render(now);
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
  }

  async function webgpu(): Promise<boolean> {
    const gpu = (navigator as Navigator & { gpu?: GPU }).gpu;
    if (!gpu) return false;
    const adapter = await gpu.requestAdapter();
    if (!adapter || disposed) return false;
    const device = await adapter.requestDevice();
    if (disposed) { device.destroy(); return false; }
    const ctx = canvas.getContext("webgpu") as GPUCanvasContext | null;
    if (!ctx) return false;

    const format = gpu.getPreferredCanvasFormat();
    ctx.configure({ device, format, alphaMode: "opaque" });

    const shader = device.createShaderModule({ code: WGSL });
    const pipeline = device.createRenderPipeline({
      layout: "auto",
      vertex: { module: shader, entryPoint: "vs" },
      fragment: { module: shader, entryPoint: "fs", targets: [{ format }] },
      primitive: { topology: "triangle-list" },
    });
    const buf = device.createBuffer({ size: 64, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    const bind = device.createBindGroup({
      layout: pipeline.getBindGroupLayout(0),
      entries: [{ binding: 0, resource: { buffer: buf } }],
    });

    loop(() => {
      device.queue.writeBuffer(buf, 0, U);
      const enc = device.createCommandEncoder();
      const pass = enc.beginRenderPass({
        colorAttachments: [{
          view: ctx.getCurrentTexture().createView(),
          loadOp: "clear",
          storeOp: "store",
          clearValue: { r: 0.024, g: 0.031, b: 0.047, a: 1 },
        }],
      });
      pass.setPipeline(pipeline);
      pass.setBindGroup(0, bind);
      pass.draw(3);
      pass.end();
      device.queue.submit([enc.finish()]);
    });
    return true;
  }

  function webgl(): boolean {
    const gl = canvas.getContext("webgl2", { antialias: false, alpha: false });
    if (!gl) return false;

    const compile = (type: number, src: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) ?? "shader");
      return s;
    };
    const prog = gl.createProgram()!;
    gl.attachShader(prog, compile(gl.VERTEX_SHADER, GLSL_VERT));
    gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, GLSL_FRAG));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog) ?? "link");
    gl.useProgram(prog);

    const uR = gl.getUniformLocation(prog, "R");
    const uA = gl.getUniformLocation(prog, "A");
    const uB = gl.getUniformLocation(prog, "B");
    const uM = gl.getUniformLocation(prog, "M");
    gl.bindVertexArray(gl.createVertexArray());

    loop(() => {
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.uniform4f(uR, U[0], U[1], U[2], U[3]);
      gl.uniform4f(uA, U[4], U[5], U[6], U[7]);
      gl.uniform4f(uB, U[8], U[9], U[10], U[11]);
      gl.uniform4f(uM, U[12], U[13], U[14], U[15]);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    });
    return true;
  }

  void (async () => {
    try {
      if (await webgpu()) { backend = "webgpu"; onBackend?.("webgpu"); return; }
    } catch {
      // Fall through: a device that advertises WebGPU and then fails to
      // give one is exactly what the WebGL2 path is for.
    }
    if (disposed) return;
    try {
      if (webgl()) { backend = "webgl2"; onBackend?.("webgl2"); return; }
    } catch {
      // No context, or a driver that will not compile the program.
    }
    backend = "none";
    onBackend?.("none");
  })();

  return {
    setMood(expression: Expression) {
      const f = FACE[expression];
      T.a = [...f.a];
      T.b = [...f.b];
      T.brow = f.brow;
      T.aper = f.aper;
      T.alert = f.alert;
      T.warm = f.warm;
    },
    setSpeaking(speaking: boolean) {
      T.speak = speaking ? 1 : 0;
    },
    backend: () => backend,
    destroy() {
      disposed = true;
      cancelAnimationFrame(raf);
      window.removeEventListener("pointermove", steer);
    },
  };
}
