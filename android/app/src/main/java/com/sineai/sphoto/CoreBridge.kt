package com.sineai.sphoto

/**
 * Kotlin face of the C++ core. Renderer calls run only on the GL render
 * thread; the parameter queries are safe anywhere.
 */
object CoreBridge {
    init {
        System.loadLibrary("sphoto_jni")
    }

    external fun nativeCreate(): Long
    external fun nativeSetParams(handle: Long, values: FloatArray)
    external fun nativeSetLook(
        handle: Long, filter: String, strength: Float, quarterTurns: Int, flip: Boolean, straighten: Float,
    )
    external fun nativeFilterIds(): Array<String>
    external fun nativeParamNames(): Array<String>
    external fun nativeParamRanges(): FloatArray
    external fun nativeDraw(handle: Long, width: Int, height: Int): Float
    external fun nativeVersion(): String
    external fun nativeDestroy(handle: Long)
}
