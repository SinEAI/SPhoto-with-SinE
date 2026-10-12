// Built-in filters: each is a colour transform on sRGB-encoded values, baked
// with the rest of the colour grading into one 3D lookup table (grading.h).
#pragma once

namespace sp {

int filter_count();
const char* filter_id(int index);
bool is_filter(const char* id);

// The index of filter id, -1 when there is none of that id.
int filter_index(const char* id);

// Applies the look of filter index to one colour, clamped to 0 .. 1.
void apply_filter(int index, float rgb[3]);

}  // namespace sp
