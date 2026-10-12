// The editing pipeline, written once and compiled three ways: as C++ for the
// CPU reference (cpu_pipeline.cpp), and as text for the GLSL ES 3.00
// (gles/shaders.h) and Metal (metal/metal_renderer.mm) shaders, which each
// prepend a small prelude. Only the common subset of the three languages is
// allowed here:
//   - float literals carry the f suffix; no implicit int/float conversion;
//   - vector components are read as .x .y .z, never swizzled;
//   - clamp/max/mix on vectors take vector arguments, never scalars;
//   - no preprocessor directives, string literals or apostrophes;
//   - the image is read only through sp_fetch(SP_TEX_ARGS uv), which returns
//     the bilinearly filtered sRGB-encoded colour, edges clamped, and the
//     colour table only through sp_lut(SP_TEX_ARGS rgb), a trilinear lookup.
// The GPU tests compare every backend against the CPU build of this file.
//
// Order, as in the plan: geometry (one homography from output to source
// coordinates); white balance, exposure, highlights / shadows, contrast,
// highlight roll-off, saturation in linear light; then, encoded, the colour
// table (HSL, curves and the filter, baked into one), sharpen and clarity,
// fade, vignette and grain.
SP_PIPELINE_SOURCE(

float sp_srgb_to_linear(float c) {
  return c <= 0.04045f ? c / 12.92f : pow((c + 0.055f) / 1.055f, 2.4f);
}

float sp_linear_to_srgb(float c) {
  return c <= 0.0031308f ? c * 12.92f : 1.055f * pow(c, 1.0f / 2.4f) - 0.055f;
}

vec3 sp_decode(vec3 c) {
  return vec3(sp_srgb_to_linear(c.x), sp_srgb_to_linear(c.y), sp_srgb_to_linear(c.z));
}

vec3 sp_encode(vec3 c) {
  return vec3(sp_linear_to_srgb(c.x), sp_linear_to_srgb(c.y), sp_linear_to_srgb(c.z));
}

float sp_luma(vec3 c) {
  return dot(c, vec3(0.2126f, 0.7152f, 0.0722f));
}

/* Mean encoded luma of the source on the 8 points of a square ring, offset
   (d.x, d.y) in texture coordinates from uv. */
float sp_ring(SP_TEX_PARAMS vec2 uv, vec2 d) {
  float s = sp_luma(sp_fetch(SP_TEX_ARGS uv + vec2(d.x, 0.0f)));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv - vec2(d.x, 0.0f)));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv + vec2(0.0f, d.y)));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv - vec2(0.0f, d.y)));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv + d));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv - d));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv + vec2(d.x, -d.y)));
  s += sp_luma(sp_fetch(SP_TEX_ARGS uv + vec2(-d.x, d.y)));
  return s * 0.125f;
}

/* Film-like grain: a fixed pseudo-random value per source pixel, so a
   preview and the export of the same photo agree. Range -0.5 .. 0.5. */
float sp_grain_noise(vec2 uv, vec2 src_size) {
  uint x = uint(floor(uv.x * src_size.x));
  uint y = uint(floor(uv.y * src_size.y));
  uint h = x * 0x8da6b343u ^ y * 0xd8163841u;
  h ^= h >> 13u;
  h *= 0x5bd1e995u;
  h ^= h >> 15u;
  return float(h & 0xffffu) / 65535.0f - 0.5f;
}

/* Where output uv comes from in the source. */
vec2 sp_source_uv(SpFrame f, vec2 uv) {
  float w = f.h20 * uv.x + f.h21 * uv.y + f.h22;
  return vec2((f.h00 * uv.x + f.h01 * uv.y + f.h02) / w,
              (f.h10 * uv.x + f.h11 * uv.y + f.h12) / w);
}

/* The tone steps in linear light, before saturation: white balance,
   exposure, highlights / shadows and contrast. */
vec3 sp_tone(vec3 c, SpParams p) {
  /* White balance: channel gains that keep the luminance. */
  vec3 wb = vec3(exp2(0.3f * p.temperature), exp2(-0.25f * p.tint), exp2(-0.3f * p.temperature));
  c = c * wb / sp_luma(wb);

  c = c * exp2(p.exposure);

  /* Highlights and shadows: up to 1.5 stops, weighted by perceptual
     brightness so each acts on its own end of the range. */
  float l = sp_linear_to_srgb(clamp(sp_luma(c), 0.0f, 1.0f));
  float dark = 1.0f - l;
  c = c * exp2(1.5f * (p.shadows * dark * dark + p.highlights * l * l));

  /* Contrast: a power curve in linear light that pivots on 18% grey. */
  return 0.18f * pow(max(c, vec3(0.0f)) / 0.18f, vec3(1.0f + 0.5f * p.contrast));
}

/* Highlight roll-off for a scalar, when the brightest source value lands
   at w > 1: the identity up to the knee k, then d / (1 + a d) for d = x - k,
   which has slope 1 at the knee and, with a = 1 / (1 - k) - 1 / (w - k),
   takes w exactly to 1. */
float sp_shoulder(float x, float w) {
  float k = 0.6f;
  if (x <= k) {
    return x;
  }
  float d = x - k;
  float a = 1.0f / (1.0f - k) - 1.0f / (w - k);
  return k + d / (1.0f + a * d);
}

/* Tone and roll-off along the source colour ray. Luminance-weighted
   highlights / shadows mean that white is NOT an upper bound on coloured
   pixels: for example exposure +1, highlights -1 puts white below 1 but
   pure red above 1. Instead, tone the endpoint of this same colour ray
   (its largest source channel is 1). For the supported adjustment ranges,
   sp_tone is monotone along every nonnegative ray, so this bounds c.

   Apply one scale to all channels, preserving the linear RGB ratios after
   white balance and contrast. Separate channel shoulders change those
   ratios and can turn warm highlights yellow. Neutral rays keep exactly
   the existing grey shoulder, including its 0.6 knee and white endpoint.
   This is display-referred highlight handling, not recovery of clipped
   scene radiance. See docs/color-kernel.md for the bound and tradeoffs. */
vec3 sp_tone_mapped(vec3 source, SpParams p) {
  vec3 c = sp_tone(source, p);
  float peak = max(c.x, max(c.y, c.z));
  if (peak > 0.6f) {
    /* A positive toned peak implies a positive source peak. Avoid the
       endpoint calculation altogether below the shoulder, also at black. */
    float source_peak = max(source.x, max(source.y, source.z));
    vec3 endpoint = sp_tone(source / source_peak, p);
    float w = max(endpoint.x, max(endpoint.y, endpoint.z));
    if (w > 1.0f) {
      c = c * (sp_shoulder(peak, w) / peak);
    }
  }
  return c;
}

/* Saturation at constant linear luminance. Limit the requested distance
   from neutral as a whole when it would leave the RGB cube, rather than
   clipping channels independently. In-gamut requests stay unchanged;
   constrained ones keep their neutral-axis direction and luminance.
   This is not a claim of constant perceptual hue in every colour space. */
vec3 sp_saturate(vec3 c, float amount) {
  float y = clamp(sp_luma(c), 0.0f, 1.0f);
  vec3 chroma = c - vec3(y);
  float scale = 1.0f + amount;
  float hi = max(chroma.x, max(chroma.y, chroma.z));
  float lo = -max(-chroma.x, max(-chroma.y, -chroma.z));
  if (hi * scale > 1.0f - y) {
    scale = (1.0f - y) / hi;
  }
  if (lo * scale < -y) {
    scale = -y / lo;
  }
  /* The clamp only guards floating-point boundary error. */
  return clamp(vec3(y) + chroma * scale, vec3(0.0f), vec3(1.0f));
}

/* Edits the output pixel at uv. */
vec3 sp_process(SP_TEX_PARAMS SpParams p, SpFrame f, vec2 uv) {
  vec2 suv = sp_source_uv(f, uv);
  vec2 px = vec2(f.px_x, f.px_y);
  vec2 src_size = vec2(f.src_w, f.src_h);
  vec3 source = sp_fetch(SP_TEX_ARGS suv);
  vec3 c = sp_tone_mapped(sp_decode(source), p);

  c = sp_saturate(c, p.saturation);

  vec3 e = sp_encode(c);

  if (f.lut_strength > 0.0f) {
    e = e + (sp_lut(SP_TEX_ARGS e) - e) * f.lut_strength;
  }

  /* Sharpen and clarity add back source detail: the difference between a
     pixel and the mean of a ring around it, 1 pixel out for sharpen and
     radius pixels out for clarity, which acts mostly on midtones. Clarity
     uses a slightly gentler midtone mask and a lower coarse gain so strong
     clarity is less blotchy at the halo. */
  if (p.sharpen != 0.0f || p.clarity != 0.0f) {
    float centre = sp_luma(source);
    float fine = centre - sp_ring(SP_TEX_ARGS suv, px);
    float coarse = centre - 0.5f * (sp_ring(SP_TEX_ARGS suv, px * f.radius) +
                                    sp_ring(SP_TEX_ARGS suv, px * (0.5f * f.radius)));
    float le = sp_luma(e);
    float mid = 3.2f * le * (1.0f - le);
    e = e + vec3(1.5f * p.sharpen * fine + 0.55f * p.clarity * mid * coarse);
  }

  /* Fade lifts the blacks towards 15% grey and leaves white alone. */
  e = e + (vec3(1.0f) - e) * (0.15f * p.fade);

  /* Vignette: darkens (or, negative, lightens) towards the corners of the
     output frame. Falloff starts later and uses a smootherstep so moderate
     amounts are less abrupt in the corners. */
  vec2 aspect = vec2(f.aspect, 1.0f);
  float r = length((uv - vec2(0.5f)) * aspect) / length(aspect * 0.5f);
  float vt = clamp((r - 0.42f) / 0.58f, 0.0f, 1.0f);
  float fall = vt * vt * vt * (vt * (vt * 6.0f - 15.0f) + 10.0f);
  e = e * (1.0f - 0.75f * p.vignette * fall);

  /* Grain weighted by local encoded luma: quieter in deep blacks, a bit
     more texture in brights. Amount 0 adds nothing. */
  float le_g = clamp(sp_luma(e), 0.0f, 1.0f);
  float gw = 0.22f + 0.78f * le_g;
  e = e + vec3(0.15f * p.grain * gw * sp_grain_noise(suv, src_size));

  return clamp(e, vec3(0.0f), vec3(1.0f));
}

)
