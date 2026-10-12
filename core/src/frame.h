// Per-render values the pipeline needs besides the adjustments: where each
// output pixel comes from in the source (geometry), sampling scales, and
// whether there is a colour table. SpFrame in the shaders; only floats, so
// the C++, GLSL and Metal layouts agree.
#pragma once

#include "sphoto/sphoto.h"

#define SP_FRAME_LIST(X)                                                    \
  /* Output uv -> source uv, a row-major 3x3 homography. */                 \
  X(h00) X(h01) X(h02) X(h10) X(h11) X(h12) X(h20) X(h21) X(h22)            \
  /* One output pixel, in source texture coordinates. */                   \
  X(px_x) X(px_y)                                                           \
  /* Source size in pixels. */                                              \
  X(src_w) X(src_h)                                                         \
  /* Output width / height. */                                              \
  X(aspect)                                                                 \
  /* Clarity radius in output pixels. */                                    \
  X(radius)                                                                 \
  /* Colour table strength: 1 with a table, 0 when there is none. */       \
  X(lut_strength)

namespace sp {

struct Frame {
#define SP_FRAME_FIELD(name) float name;
  SP_FRAME_LIST(SP_FRAME_FIELD)
#undef SP_FRAME_FIELD
};

#define SP_FRAME_NAME(name) #name,
inline constexpr const char* kFrameFieldNames[] = {SP_FRAME_LIST(SP_FRAME_NAME)};
#undef SP_FRAME_NAME
inline constexpr int kFrameFieldCount =
    sizeof(kFrameFieldNames) / sizeof(kFrameFieldNames[0]);
static_assert(sizeof(Frame) == kFrameFieldCount * sizeof(float), "Frame layout");

#define SP_FRAME_GLSL(name) "float " #name ";"
inline constexpr const char* kShaderFrameStruct =
    "struct SpFrame {" SP_FRAME_LIST(SP_FRAME_GLSL) "};\n";
#undef SP_FRAME_GLSL

// The geometry of an edit for a given source size.
struct Geometry {
  double to_source[9];  // output uv -> source uv
  double width;         // edited frame size at full resolution, in source
  double height;        //   pixels (not rounded)
  int out_width;        // the same, rounded: the export size
  int out_height;
};

Geometry solve_geometry(const sp_geometry& g, int src_width, int src_height);

// Everything but the adjustments for rendering at out_width x out_height.
Frame make_frame(const Geometry& geometry, int src_width, int src_height,
                 int out_width, int out_height, float lut_strength);

}  // namespace sp
