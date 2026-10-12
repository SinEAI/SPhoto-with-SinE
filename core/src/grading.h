// Colour grading on sRGB-encoded values: HSL, tone curves and the built-in
// filter, baked together into one 3D lookup table that every backend samples
// the same way. The pipeline sees only the table, so grading adds no shader
// inputs however many controls it has.
#pragma once

#include <vector>

#include "sphoto/sphoto.h"

namespace sp {

// Points per side of the colour table.
inline constexpr int kLutSize = 33;

// A curve is the identity when it has fewer than two usable points, or when
// they all lie on the diagonal from (0, 0) to (1, 1).
bool curve_is_identity(const sp_curve& curve);
bool hsl_is_identity(const sp_hsl& hsl);

// Fills rgb with kLutSize^3 RGB triples, red varying fastest, then green,
// then blue: HSL, then the curves (rgb, then each channel), then the filter
// mixed in at strength. Leaves rgb empty when the table would be the
// identity. Returns false for an unknown filter id ("" is no filter).
bool bake_grading(const char* filter, float strength, const sp_curve curves[4],
                  const sp_hsl& hsl, std::vector<float>* rgb);

}  // namespace sp
