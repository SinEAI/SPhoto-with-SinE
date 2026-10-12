// Backend-independent part of the renderer behind the C API.
#pragma once

#include <string>
#include <vector>

#include "frame.h"
#include "grading.h"
#include "sphoto/sphoto.h"

struct sp_renderer {
  virtual ~sp_renderer() = default;
  virtual int set_image(const uint8_t* rgba, int width, int height) = 0;
  virtual int render_rgba(uint8_t* dst, int out_width, int out_height) = 0;

  // The frame values for drawing the edit at out_width x out_height.
  sp::Frame frame_for(int out_width, int out_height) const {
    const sp::Geometry g = sp::solve_geometry(geometry, image_width, image_height);
    return sp::make_frame(g, image_width, image_height, out_width, out_height,
                          lut.empty() ? 0.0f : 1.0f);
  }

  // Rebakes the colour table after the filter, its strength, the curves or
  // the HSL changed. The filter id is known to be valid.
  void bake() {
    sp::bake_grading(filter.c_str(), filter_strength, curves, hsl, &lut);
    lut_changed = true;
  }

  sp_params params{};
  sp_geometry geometry{};
  // Colour grading: everything the table holds.
  std::string filter;
  float filter_strength = 0.0f;
  sp_curve curves[4] = {};
  sp_hsl hsl{};
  // The baked colour table, empty for none, and whether the backend's copy
  // is out of date.
  std::vector<float> lut;
  bool lut_changed = false;
  int image_width = 0;
  int image_height = 0;
};
