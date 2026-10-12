// The list of edit parameters, in sp_params declaration order. Everything
// that walks the parameters (recipes, the generic accessors, the shader
// struct) expands this list, so adding a field to sp_params means adding it
// here too; the static_asserts below catch a mismatch.
#pragma once

#include <cstddef>

#include "sphoto/sphoto.h"

//         name         min    max
#define SP_PARAM_LIST(X)            \
  X(exposure, -4.0f, 4.0f)          \
  X(contrast, -1.0f, 1.0f)          \
  X(saturation, -1.0f, 1.0f)        \
  X(temperature, -1.0f, 1.0f)       \
  X(tint, -1.0f, 1.0f)              \
  X(highlights, -1.0f, 1.0f)        \
  X(shadows, -1.0f, 1.0f)           \
  X(fade, 0.0f, 1.0f)               \
  X(sharpen, 0.0f, 1.0f)            \
  X(clarity, -1.0f, 1.0f)           \
  X(vignette, -1.0f, 1.0f)          \
  X(grain, 0.0f, 1.0f)

namespace sp {

enum ParamIndex {
#define SP_PARAM_ENUM(name, lo, hi) kParam_##name,
  SP_PARAM_LIST(SP_PARAM_ENUM)
#undef SP_PARAM_ENUM
  kParamCount
};

struct ParamInfo {
  const char* name;
  float min;
  float max;
};

inline constexpr ParamInfo kParams[] = {
#define SP_PARAM_INFO(name, lo, hi) {#name, lo, hi},
    SP_PARAM_LIST(SP_PARAM_INFO)
#undef SP_PARAM_INFO
};

static_assert(sizeof(sp_params) == kParamCount * sizeof(float),
              "sp_params and SP_PARAM_LIST differ");
#define SP_PARAM_OFFSET(name, lo, hi)                                    \
  static_assert(offsetof(sp_params, name) == kParam_##name * sizeof(float), \
                "SP_PARAM_LIST is not in sp_params order: " #name);
SP_PARAM_LIST(SP_PARAM_OFFSET)
#undef SP_PARAM_OFFSET

// The same struct for the shaders (GLSL and Metal use identical syntax).
#define SP_PARAM_GLSL(name, lo, hi) "float " #name ";"
inline constexpr const char* kShaderParamsStruct =
    "struct SpParams {" SP_PARAM_LIST(SP_PARAM_GLSL) "};\n";
#undef SP_PARAM_GLSL

inline const float* param_data(const sp_params& p) {
  return reinterpret_cast<const float*>(&p);
}
inline float* param_data(sp_params& p) { return reinterpret_cast<float*>(&p); }

// Radius, in output pixels, of the clarity blur. Even, so half of it is a
// whole pixel too; every backend samples whole-pixel offsets.
inline int clarity_radius(int out_width, int out_height) {
  const int shorter = out_width < out_height ? out_width : out_height;
  const int r = 2 * ((shorter + 150) / 300);
  return r < 2 ? 2 : r;
}

}  // namespace sp
