// JNI bridge between the Kotlin UI and the SPhoto core. Every call runs on
// the GLSurfaceView render thread, where the GL context is current.
#include <GLES3/gl3.h>
#include <jni.h>

#include <chrono>
#include <vector>

#include "sphoto/sphoto.h"

namespace {

// A 12-megapixel frame, the size of a typical phone photo.
constexpr int kTestWidth = 4032;
constexpr int kTestHeight = 3024;

sp_renderer* from_handle(jlong handle) {
  return reinterpret_cast<sp_renderer*>(handle);
}

}  // namespace

extern "C" {

JNIEXPORT jlong JNICALL
Java_com_sineai_sphoto_CoreBridge_nativeCreate(JNIEnv*, jobject) {
  sp_renderer* renderer = sp_gles_renderer_create();
  if (!renderer) return 0;
  std::vector<uint8_t> image(static_cast<size_t>(kTestWidth) * kTestHeight * 4);
  sp_make_test_image(image.data(), kTestWidth, kTestHeight);
  if (sp_renderer_set_image(renderer, image.data(), kTestWidth, kTestHeight) !=
      SP_OK) {
    sp_renderer_destroy(renderer);
    return 0;
  }
  return reinterpret_cast<jlong>(renderer);
}

// values holds one float per adjustment, in sp_params order.
JNIEXPORT void JNICALL Java_com_sineai_sphoto_CoreBridge_nativeSetParams(
    JNIEnv* env, jobject, jlong handle, jfloatArray values) {
  sp_params params{};
  const jsize n = env->GetArrayLength(values);
  jfloat* v = env->GetFloatArrayElements(values, nullptr);
  for (jsize i = 0; i < n && i < sp_param_count(); ++i) sp_params_set(&params, i, v[i]);
  env->ReleaseFloatArrayElements(values, v, JNI_ABORT);
  sp_renderer_set_params(from_handle(handle), &params);
}

// The filter, its strength and the geometry. An empty id means no filter.
JNIEXPORT void JNICALL Java_com_sineai_sphoto_CoreBridge_nativeSetLook(
    JNIEnv* env, jobject, jlong handle, jstring filter, jfloat strength,
    jint quarter_turns, jboolean flip, jfloat straighten) {
  const char* id = env->GetStringUTFChars(filter, nullptr);
  sp_renderer_set_filter(from_handle(handle), id, strength);
  env->ReleaseStringUTFChars(filter, id);
  sp_geometry geometry{};
  geometry.quarter_turns = quarter_turns;
  geometry.flip_horizontal = flip ? 1 : 0;
  geometry.straighten = straighten;
  sp_renderer_set_geometry(from_handle(handle), &geometry);
}

// The built-in filters' ids. Safe on any thread.
JNIEXPORT jobjectArray JNICALL
Java_com_sineai_sphoto_CoreBridge_nativeFilterIds(JNIEnv* env, jobject) {
  const int n = sp_filter_count();
  jobjectArray ids =
      env->NewObjectArray(n, env->FindClass("java/lang/String"), nullptr);
  for (int i = 0; i < n; ++i) {
    jstring s = env->NewStringUTF(sp_filter_id(i));
    env->SetObjectArrayElement(ids, i, s);
    env->DeleteLocalRef(s);
  }
  return ids;
}

// The adjustments' recipe keys, in sp_params order. Safe on any thread.
JNIEXPORT jobjectArray JNICALL
Java_com_sineai_sphoto_CoreBridge_nativeParamNames(JNIEnv* env, jobject) {
  const int n = sp_param_count();
  jobjectArray names =
      env->NewObjectArray(n, env->FindClass("java/lang/String"), nullptr);
  for (int i = 0; i < n; ++i) {
    const char* name = nullptr;
    sp_param_info(i, &name, nullptr, nullptr);
    jstring s = env->NewStringUTF(name);
    env->SetObjectArrayElement(names, i, s);
    env->DeleteLocalRef(s);
  }
  return names;
}

// min, max pairs for each adjustment. Safe on any thread.
JNIEXPORT jfloatArray JNICALL
Java_com_sineai_sphoto_CoreBridge_nativeParamRanges(JNIEnv* env, jobject) {
  const int n = sp_param_count();
  std::vector<jfloat> ranges(static_cast<size_t>(n) * 2);
  for (int i = 0; i < n; ++i) {
    sp_param_info(i, nullptr, &ranges[i * 2], &ranges[i * 2 + 1]);
  }
  jfloatArray out = env->NewFloatArray(n * 2);
  env->SetFloatArrayRegion(out, 0, n * 2, ranges.data());
  return out;
}

// Draws one frame and returns the GPU time in milliseconds. glFinish makes
// the number meaningful for M0; the shipping app will not block here.
JNIEXPORT jfloat JNICALL Java_com_sineai_sphoto_CoreBridge_nativeDraw(
    JNIEnv*, jobject, jlong handle, jint width, jint height) {
  const auto start = std::chrono::steady_clock::now();
  if (sp_gles_renderer_draw(from_handle(handle), width, height) != SP_OK) {
    return -1.0f;
  }
  glFinish();
  const std::chrono::duration<float, std::milli> elapsed =
      std::chrono::steady_clock::now() - start;
  return elapsed.count();
}

JNIEXPORT jstring JNICALL
Java_com_sineai_sphoto_CoreBridge_nativeVersion(JNIEnv* env, jobject) {
  return env->NewStringUTF(sp_version());
}

JNIEXPORT void JNICALL Java_com_sineai_sphoto_CoreBridge_nativeDestroy(
    JNIEnv*, jobject, jlong handle) {
  sp_renderer_destroy(from_handle(handle));
}

}  // extern "C"
