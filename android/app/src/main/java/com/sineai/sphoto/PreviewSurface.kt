package com.sineai.sphoto

import android.content.Context
import android.opengl.GLSurfaceView
import javax.microedition.khronos.egl.EGLConfig
import javax.microedition.khronos.opengles.GL10

/** One adjustment of the core, in sp_params order. */
data class Adjustment(val index: Int, val key: String, val title: String, val range: ClosedFloatingPointRange<Float>) {
    companion object {
        /** Display names for the core's recipe keys. */
        private val titles = mapOf(
            "exposure" to "曝光", "contrast" to "对比度", "saturation" to "饱和度",
            "temperature" to "色温", "tint" to "色调", "highlights" to "高光",
            "shadows" to "阴影", "fade" to "褪色", "sharpen" to "锐化",
            "clarity" to "清晰度", "vignette" to "暗角", "grain" to "颗粒",
        )

        /** Every adjustment the core offers, read from the core itself. */
        val all: List<Adjustment> by lazy {
            val ranges = CoreBridge.nativeParamRanges()
            CoreBridge.nativeParamNames().mapIndexed { i, key ->
                Adjustment(i, key, titles[key] ?: key, ranges[i * 2]..ranges[i * 2 + 1])
            }
        }
    }
}

/** A built-in filter of the core. */
data class Filter(val id: String, val title: String) {
    companion object {
        private val titles = mapOf(
            "warm" to "暖阳", "film" to "胶片", "cool" to "冷调",
            "teal_orange" to "青橙", "mono" to "黑白", "mono_soft" to "柔和黑白",
        )

        val all: List<Filter> by lazy {
            CoreBridge.nativeFilterIds().map { Filter(it, titles[it] ?: it) }
        }
    }
}

/**
 * The edit: one value per [Adjustment], all 0 when untouched; the filter id
 * ("" for none) and its strength; and the geometry.
 */
data class EditParams(
    val values: List<Float> = List(Adjustment.all.size) { 0f },
    val filter: String = "",
    val strength: Float = 1f,
    val quarterTurns: Int = 0,
    val flip: Boolean = false,
    val straighten: Float = 0f,
) {
    fun with(index: Int, value: Float) = copy(values = values.toMutableList().also { it[index] = value })
}

/**
 * GLSurfaceView that shows the core's preview. Frames are drawn only when
 * the parameters change. [onFrameTime] reports the GPU time of each frame.
 */
class PreviewSurface(
    context: Context,
    private val onFrameTime: (Float) -> Unit,
) : GLSurfaceView(context) {

    private val renderer = CoreRenderer()

    init {
        setEGLContextClientVersion(3)
        preserveEGLContextOnPause = true
        setRenderer(renderer)
        renderMode = RENDERMODE_WHEN_DIRTY
    }

    fun update(params: EditParams) {
        queueEvent { renderer.params = params }
        requestRender()
    }

    private inner class CoreRenderer : Renderer {
        var params = EditParams()
        private var handle = 0L
        private var width = 1
        private var height = 1

        override fun onSurfaceCreated(gl: GL10?, config: EGLConfig?) {
            // A new surface means a new GL context: the old handle's GL
            // objects are gone with it.
            handle = CoreBridge.nativeCreate()
        }

        override fun onSurfaceChanged(gl: GL10?, w: Int, h: Int) {
            width = w
            height = h
        }

        override fun onDrawFrame(gl: GL10?) {
            if (handle == 0L) return
            CoreBridge.nativeSetParams(handle, params.values.toFloatArray())
            with(params) { CoreBridge.nativeSetLook(handle, filter, strength, quarterTurns, flip, straighten) }
            val ms = CoreBridge.nativeDraw(handle, width, height)
            post { onFrameTime(ms) }
        }
    }
}
