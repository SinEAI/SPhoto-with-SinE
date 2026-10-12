package com.sineai.sphoto

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import android.os.Build

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { SPhotoTheme { EditorScreen() } }
    }
}

@Composable
private fun SPhotoTheme(content: @Composable () -> Unit) {
    // Photo editors use a dark canvas; dynamic colour where the system has it.
    val context = LocalContext.current
    val colors = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        dynamicDarkColorScheme(context)
    } else {
        darkColorScheme()
    }
    MaterialTheme(colorScheme = colors, content = content)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EditorScreen() {
    var params by remember { mutableStateOf(EditParams()) }
    var frameMs by remember { mutableFloatStateOf(0f) }
    var surface by remember { mutableStateOf<PreviewSurface?>(null) }

    fun apply(next: EditParams) {
        params = next
        surface?.update(next)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("SPhoto") },
                actions = {
                    TextButton(onClick = { apply(params.copy(quarterTurns = (params.quarterTurns + 1) % 4)) }) {
                        Text("旋转")
                    }
                    TextButton(onClick = { apply(params.copy(flip = !params.flip)) }) { Text("翻转") }
                    TextButton(onClick = { apply(EditParams()) }) { Text("还原") }
                },
            )
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            AndroidView(
                factory = { context ->
                    PreviewSurface(context) { frameMs = it }.also { surface = it }
                },
                modifier = Modifier.weight(1f).fillMaxWidth(),
            )
            Surface(tonalElevation = 2.dp) {
                Column(
                    Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    Text(
                        "GPU 帧耗时 %.1f ms · 核心 %s".format(frameMs, CoreBridge.nativeVersion()),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Row(
                        Modifier.horizontalScroll(rememberScrollState()),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        FilterChip(
                            selected = params.filter.isEmpty(),
                            onClick = { apply(params.copy(filter = "")) },
                            label = { Text("原图") },
                        )
                        for (filter in Filter.all) {
                            FilterChip(
                                selected = params.filter == filter.id,
                                onClick = { apply(params.copy(filter = filter.id)) },
                                label = { Text(filter.title) },
                            )
                        }
                    }
                    Column(Modifier.heightIn(max = 280.dp).verticalScroll(rememberScrollState())) {
                        if (params.filter.isNotEmpty()) {
                            AdjustmentSlider("滤镜强度", params.strength, 0f..1f) { apply(params.copy(strength = it)) }
                        }
                        AdjustmentSlider("拉直", params.straighten, -45f..45f) { apply(params.copy(straighten = it)) }
                        for (adjustment in Adjustment.all) {
                            AdjustmentSlider(adjustment.title, params.values[adjustment.index], adjustment.range) {
                                apply(params.with(adjustment.index, it))
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun AdjustmentSlider(
    label: String,
    value: Float,
    range: ClosedFloatingPointRange<Float>,
    onChange: (Float) -> Unit,
) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.padding(end = 12.dp), style = MaterialTheme.typography.bodyMedium)
        Slider(value = value, onValueChange = onChange, valueRange = range, modifier = Modifier.weight(1f))
        Text("%+.2f".format(value), Modifier.padding(start = 12.dp), style = MaterialTheme.typography.labelMedium)
    }
}
