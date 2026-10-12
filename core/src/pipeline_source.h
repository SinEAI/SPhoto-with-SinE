// pipeline.inc as shader source text, for the GPU backends to prepend their
// prelude to. Stringifying drops the comments and joins the lines.
#pragma once

namespace sp {

#define SP_PIPELINE_SOURCE(...) #__VA_ARGS__
inline constexpr const char* kPipelineSource =
#include "pipeline.inc"
    ;
#undef SP_PIPELINE_SOURCE

}  // namespace sp
