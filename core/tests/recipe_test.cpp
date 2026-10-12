#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "sphoto/sphoto.h"
#include "test.h"

TEST(recipe_rejects_non_finite_numbers_at_each_destination) {
  const std::vector<std::string> sections = {
    R"("adjust":{"exposure":VALUE})",
    R"("filter":{"id":"film","strength":VALUE})",
    R"("geometry":{"quarter_turns":VALUE})",
    R"("geometry":{"crop":[0,0,VALUE,1]})",
    R"("curves":{"rgb":[[0,0],[1,VALUE]]})",
    R"("hsl":{"hue":[VALUE]})",
    R"("future":VALUE)"
  };
  for (const auto& section : sections) {
    for (const auto& value : {"0e400", "-0E+400", "1e400", "-1e309"}) {
      std::string field = section;
      field.replace(field.find("VALUE"), 5, value);
      const std::string json = R"({"format":"sphoto-recipe","version":1,)" + field + "}";
      sp_edit e{};
      e.adjust.exposure = 0.75f;
      EXPECT(sp_recipe_read_edit(json.c_str(), &e) == SP_ERR_FORMAT);
      EXPECT(e.adjust.exposure == 0.75f);
    }
    std::string field = section;
    field.replace(field.find("VALUE"), 5, "0.25");
    const std::string json = R"({"format":"sphoto-recipe","version":1,)" + field + "}";
    sp_edit e{};
    EXPECT(sp_recipe_read_edit(json.c_str(), &e) == SP_OK);
  }
  sp_params p{};
  p.exposure = 0.75f;
  EXPECT(sp_recipe_read(R"({"format":"sphoto-recipe","version":0e400})", &p) == SP_ERR_FORMAT);
  EXPECT(p.exposure == 0.75f);
}

TEST(recipe_numbers_keep_finite_clamping_underflow_and_future_versions) {
  sp_params p{};
  EXPECT(sp_recipe_read(R"({"format":"sphoto-recipe","version":1,"adjust":{"exposure":1e308,"grain":1e-400}})", &p) == SP_OK);
  EXPECT(p.exposure == 4.0f && p.grain == 0.0f);
  EXPECT(sp_recipe_read(R"({"format":"sphoto-recipe","version":1e308})", &p) == SP_ERR_UNSUPPORTED);
  for (const auto& value : {"0e309", "0.0e999999", "1e999999"}) {
    const std::string json = R"({"format":"sphoto-recipe","version":1,"adjust":{"exposure":)" + std::string(value) + "}}";
    EXPECT(sp_recipe_read(json.c_str(), &p) == SP_ERR_FORMAT);
  }
  const std::string json = R"({"format":"sphoto-recipe","version":1,"adjust":{"exposure":)" + std::string(400, '9') + "e-400}}";
  EXPECT(sp_recipe_read(json.c_str(), &p) == SP_ERR_FORMAT);
}

TEST(recipe_unknown_nesting_is_bounded_at_all_fallbacks) {
  const std::vector<std::string> sections = {"", "adjust", "filter", "geometry", "curves", "hsl"};
  for (const auto& section : sections) {
    std::string value = "0";
    for (int i = 0; i < 40; ++i) value = R"([{"key":)" + value + "}]";
    const std::string field = section.empty() ? R"("future":)" + value : "\"" + section + R"(":{"future":)" + value + "}";
    const std::string json = R"({"format":"sphoto-recipe","version":1,)" + field + "}";
    sp_edit e{};
    e.adjust.exposure = 0.75f;
    EXPECT(sp_recipe_read_edit(json.c_str(), &e) == SP_ERR_FORMAT);
    EXPECT(e.adjust.exposure == 0.75f);
  }
  for (int depth : {64, 65, 100000}) {
    const std::string json = R"({"format":"sphoto-recipe","version":1,"future":)" + std::string(depth, '[') + "0" + std::string(depth, ']') + "}";
    sp_edit e{};
    EXPECT(sp_recipe_read_edit(json.c_str(), &e) == (depth == 64 ? SP_OK : SP_ERR_FORMAT));
  }
}

TEST(recipe_unknown_siblings_do_not_accumulate_depth) {
  std::string json = R"({"format":"sphoto-recipe","version":1)";
  for (int i = 0; i < 1000; ++i) json += R"(,"future":[{"ok":true}])";
  json += "}";
  sp_edit e{};
  EXPECT(sp_recipe_read_edit(json.c_str(), &e) == SP_OK);
}

TEST(recipe_large_finite_quarter_turns_are_normalized_before_integer_conversion) {
  for (const auto& value : {"1e300", "-1e300", "3.5", "-3.5"}) {
    const std::string json = R"({"format":"sphoto-recipe","version":1,"geometry":{"quarter_turns":)" + std::string(value) + "}}";
    sp_edit e{};
    EXPECT(sp_recipe_read_edit(json.c_str(), &e) == SP_OK);
    EXPECT(e.geometry.quarter_turns == 0);
  }
}

TEST(recipe_late_failure_preserves_the_complete_output) {
  sp_edit edit{};
  std::snprintf(edit.filter, sizeof(edit.filter), "film");
  edit.filter_strength = 0.5f;
  edit.geometry.quarter_turns = 3;
  edit.adjust.exposure = 0.75f;
  sp_edit before;
  std::memcpy(&before, &edit, sizeof(edit));
  const char* bad = R"({"format":"sphoto-recipe","version":1,"adjust":{"exposure":0.2},"hsl":{"hue":[0.1,0e400]}})";
  EXPECT(sp_recipe_read_edit(bad, &edit) == SP_ERR_FORMAT);
  EXPECT(std::memcmp(&before, &edit, sizeof(edit)) == 0);
  for (int depth : {64, 65}) {
    std::string value = "{}";
    for (int i = 1; i < depth; ++i) value = R"({"child":)" + value + "}";
    const std::string json = R"({"format":"sphoto-recipe","version":1,"future":)" + value + "}";
    EXPECT(sp_recipe_read_edit(json.c_str(), &edit) == (depth == 64 ? SP_OK : SP_ERR_FORMAT));
  }
}

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
