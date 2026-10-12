// GLSL ES 3.0 shaders for the GLES backend. The editing itself is
// pipeline.inc; the fragment shader is kFragmentPrelude, the SpParams and
// SpFrame structs, that pipeline and kFragmentMain, in that order.
#pragma once

namespace sp::gles {

// A quad from gl_VertexID. u_rect is the target rectangle in clip space
// (x0, y0, x1, y1). u_flip_y is 1 when rendering for glReadPixels, whose
// first row is the bottom of the framebuffer.
inline constexpr const char* kVertexShader = R"(#version 300 es
uniform vec4 u_rect;
uniform float u_flip_y;
out vec2 v_uv;
void main() {
  vec2 p = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
  v_uv = vec2(p.x, mix(1.0 - p.y, p.y, u_flip_y));
  gl_Position = vec4(mix(u_rect.xy, u_rect.zw, p), 0.0, 1.0);
}
)";

inline constexpr const char* kFragmentPrelude = R"(#version 300 es
precision highp float;
precision highp int;
// Texture lookup precision follows the sampler, not the default float
// precision. Gamut-boundary calculations amplify low-precision RGB reads.
precision highp sampler2D;
precision highp sampler3D;
uniform sampler2D u_image;
uniform sampler3D u_lut;
#define SP_TEX_PARAMS
#define SP_TEX_ARGS
vec3 sp_fetch(vec2 uv) { return texture(u_image, uv).xyz; }
vec3 sp_lut(vec3 c) {
  float n = float(textureSize(u_lut, 0).x);
  return texture(u_lut, c * ((n - 1.0) / n) + 0.5 / n).xyz;
}
)";

inline constexpr const char* kFragmentMain = R"(
uniform SpParams u_p;
uniform SpFrame u_f;
in vec2 v_uv;
out vec4 o_color;
void main() {
  vec3 c = sp_process(u_p, u_f, v_uv);
  o_color = vec4(c, texture(u_image, sp_source_uv(u_f, v_uv)).w);
}
)";

}  // namespace sp::gles
