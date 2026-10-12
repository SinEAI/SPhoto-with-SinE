#include <cstdio>
#include <string>
#include <vector>

#include "sphoto/sphoto.h"
#include "test.h"

namespace {

std::string write(const sp_params& p) {
  const int n = sp_recipe_write(&p, nullptr, 0);
  std::string text(n, '\0');
  sp_recipe_write(&p, text.data(), n + 1);
  return text;
}

}  // namespace

TEST(recipe_round_trips) {
  sp_params p{};
  p.exposure = 0.75f;
  p.contrast = -0.3f;
  p.grain = 0.125f;
  const std::string text = write(p);
  sp_params back;
  EXPECT(sp_recipe_read(text.c_str(), &back) == SP_OK);
  EXPECT(back.exposure == 0.75f && back.contrast == -0.3f && back.grain == 0.125f);
  EXPECT(back.saturation == 0.0f);
}

TEST(recipe_writes_only_changed_adjustments) {
  sp_params p{};
  p.fade = 0.5f;
  const std::string text = write(p);
  EXPECT(text.find("\"fade\": 0.5") != std::string::npos);
  EXPECT(text.find("exposure") == std::string::npos);
  sp_params empty{};
  EXPECT(write(empty).find("\"adjust\": {}") != std::string::npos);
}

TEST(recipe_ignores_unknown_keys_and_clamps) {
  const char* json = R"({
    "format": "sphoto-recipe", "version": 1, "note": ["from", {"a": true}],
    "adjust": { "exposure": 9, "sharpen": -1, "lens_blur": 0.5, "clarity": -2.5e-1 }
  })";
  sp_params p;
  EXPECT(sp_recipe_read(json, &p) == SP_OK);
  EXPECT(p.exposure == 4.0f);
  EXPECT(p.sharpen == 0.0f);
  EXPECT(p.clarity == -0.25f);
}

TEST(recipe_rejects_bad_input) {
  sp_params p{};
  p.exposure = 1.0f;
  EXPECT(sp_recipe_read("{\"format\": \"sphoto-recipe\", \"version\": 2}", &p) ==
         SP_ERR_UNSUPPORTED);
  EXPECT(sp_recipe_read("{\"version\": 1}", &p) == SP_ERR_FORMAT);
  EXPECT(sp_recipe_read("{\"format\": \"sphoto-recipe\", \"version\": 1,", &p) ==
         SP_ERR_FORMAT);
  EXPECT(sp_recipe_read("not json", &p) == SP_ERR_FORMAT);
  EXPECT(p.exposure == 1.0f);  // untouched on failure
}

TEST(recipe_write_reports_needed_size) {
  sp_params p{};
  p.exposure = -1.5f;
  char small[8];
  const int n = sp_recipe_write(&p, small, sizeof(small));
  EXPECT(n > 8);
  EXPECT(std::string(small).size() == 7);
}

TEST(edit_recipe_round_trips_filter_and_geometry) {
  sp_edit e{};
  std::snprintf(e.filter, sizeof(e.filter), "film");
  e.filter_strength = 0.8f;
  e.adjust.clarity = 0.4f;
  e.geometry.quarter_turns = 3;
  e.geometry.flip_horizontal = 1;
  e.geometry.straighten = -2.5f;
  e.geometry.vertical = 0.25f;
  e.geometry.crop_x = 0.1f;
  e.geometry.crop_width = 0.75f;
  const int n = sp_recipe_write_edit(&e, nullptr, 0);
  std::string text(n, '\0');
  sp_recipe_write_edit(&e, text.data(), n + 1);
  sp_edit back;
  EXPECT(sp_recipe_read_edit(text.c_str(), &back) == SP_OK);
  EXPECT(std::string(back.filter) == "film" && back.filter_strength == 0.8f);
  EXPECT(back.adjust.clarity == 0.4f);
  EXPECT(back.geometry.quarter_turns == 3 && back.geometry.flip_horizontal == 1);
  EXPECT(back.geometry.straighten == -2.5f && back.geometry.vertical == 0.25f);
  EXPECT(back.geometry.crop_x == 0.1f && back.geometry.crop_width == 0.75f);
  EXPECT(back.geometry.crop_height == 1.0f);
}

TEST(edit_recipe_without_geometry_or_filter_stays_short) {
  sp_edit e{};
  e.adjust.exposure = 1.0f;
  const int n = sp_recipe_write_edit(&e, nullptr, 0);
  std::string text(n, '\0');
  sp_recipe_write_edit(&e, text.data(), n + 1);
  EXPECT(text.find("geometry") == std::string::npos);
  EXPECT(text.find("filter") == std::string::npos);
  EXPECT(text.find("curves") == std::string::npos);
  EXPECT(text.find("hsl") == std::string::npos);
}

namespace {

std::string write_edit(const sp_edit& e) {
  const int n = sp_recipe_write_edit(&e, nullptr, 0);
  std::string text(n, '\0');
  sp_recipe_write_edit(&e, text.data(), n + 1);
  return text;
}

}  // namespace

TEST(edit_recipe_round_trips_curves_and_hsl) {
  sp_edit e{};
  e.curves[0] = {3, {0.0f, 0.3f, 1.0f}, {0.0f, 0.25f, 1.0f}};
  e.curves[3] = {2, {0.0f, 1.0f}, {0.1f, 0.9f}};
  e.curves[1] = {2, {0.0f, 1.0f}, {0.0f, 1.0f}};  // identity: not written
  e.hsl.hue[0] = 0.2f;
  e.hsl.luminance[7] = -0.5f;
  const std::string text = write_edit(e);
  EXPECT(text.find("\"rgb\": [[0, 0], [0.3, 0.25], [1, 1]]") != std::string::npos);
  EXPECT(text.find("\"blue\": [[0, 0.1], [1, 0.9]]") != std::string::npos);
  EXPECT(text.find("\"red\"") == std::string::npos);
  EXPECT(text.find("\"green\"") == std::string::npos);
  EXPECT(text.find("\"hue\": [0.2, 0, 0, 0, 0, 0, 0, 0]") != std::string::npos);
  EXPECT(text.find("\"saturation\"") == std::string::npos);
  sp_edit back;
  EXPECT(sp_recipe_read_edit(text.c_str(), &back) == SP_OK);
  EXPECT(back.curves[0].count == 3 && back.curves[0].x[1] == 0.3f &&
         back.curves[0].y[1] == 0.25f && back.curves[0].y[2] == 1.0f);
  EXPECT(back.curves[3].count == 2 && back.curves[3].y[0] == 0.1f &&
         back.curves[3].y[1] == 0.9f);
  EXPECT(back.curves[1].count == 0 && back.curves[2].count == 0);
  EXPECT(back.hsl.hue[0] == 0.2f && back.hsl.luminance[7] == -0.5f);
  EXPECT(back.hsl.saturation[0] == 0.0f && back.hsl.hue[1] == 0.0f);
}

TEST(recipe_curves_and_hsl_are_clamped) {
  const char* json = R"({"format": "sphoto-recipe", "version": 1,
    "curves": {"green": [[-1, 2], [0.5, 0.5], [3, -0.5], [1, 1], [1, 1],
                         [1, 1], [1, 1], [1, 1], [1, 1], [1, 1]],
               "alpha": [[0, 1]]},
    "hsl": {"saturation": [2, -3, 0.5], "luminance": [0, 0, 0, 0, 0, 0, 0, 0, 1, 1],
            "vibrance": [1]}})";
  sp_edit e;
  EXPECT(sp_recipe_read_edit(json, &e) == SP_OK);
  const sp_curve& g = e.curves[2];
  EXPECT(g.count == 8);  // points past the eighth are dropped
  EXPECT(g.x[0] == 0.0f && g.y[0] == 1.0f && g.x[2] == 1.0f && g.y[2] == 0.0f);
  EXPECT(e.hsl.saturation[0] == 1.0f && e.hsl.saturation[1] == -1.0f &&
         e.hsl.saturation[2] == 0.5f && e.hsl.saturation[3] == 0.0f);
  EXPECT(e.hsl.luminance[7] == 0.0f);
  EXPECT(sp_recipe_read_edit(R"({"format": "sphoto-recipe", "version": 1,
    "curves": {"rgb": [[0, 0, 0]]}})", &e) == SP_ERR_FORMAT);
}

TEST(unknown_filter_in_recipe_reads_as_none) {
  const char* json = R"({"format": "sphoto-recipe", "version": 1,
    "filter": {"id": "from-the-future", "strength": 1}})";
  sp_edit e;
  EXPECT(sp_recipe_read_edit(json, &e) == SP_OK);
  EXPECT(e.filter[0] == '\0' && e.filter_strength == 0.0f);
}
