# Windows runtime diagnostics

The `runtime-tests` job in `windows-release.yml` runs the exact portable build
produced for the working-branch commit. It verifies the source commit and asset
checksums before executing the program. These checks do not publish a Release.

- `--runtime-test REPORT.json` measures the production scanner, fingerprinter,
  catalog builder, render worker, native renderer and full-image CPU exporter.
  Libraries contain 1,000 and 10,000 distinct generated BMPs. Preview inputs are
  12 MP and 24 MP, with 1,100 px and 2,560 px output limits.
- The same test checks distinct-photo identity, preview request coalescing and
  unchanged 17,000 px image export. Malformed recipes are tested in disposable
  child processes with timeouts; a valid recipe is the positive control.
- `--stress-test LOG 50000` exercises the actual WinUI grid, scrolling and
  thumbnail loading with 50,000 generated files. Most files contain copies of
  eight small JPEGs. This measures file/grid scale, not a catalog of 50,000
  unique high-resolution images.
- `--edit-return-test LOG` exercises the actual editor/viewer transition using
  generated 3.84 MP, 24 MP and gray images.

The first scan starts with an empty index; file-system caches have not been
flushed after fixture generation. Repeated scans reuse the populated index.
Timed preview loops follow warmup. Reused-RGBA controls omit worker channel
conversion, per-frame destination allocation and UI bitmap display; their
timings are not complete on-screen frame latencies. The preview measurement
includes the real worker's render/readback/BGRA path but excludes UI display.

Allocation figures measure managed allocations on the measuring thread,
including marshalling and instrumentation. They exclude native allocations and
other threads. Working-set figures sample the entire process and can include
retained resources from earlier cases. Measurements on a hosted runner are a
baseline, not a physical-GPU, battery or user-device benchmark. ANGLE's renderer
name is recorded so software rendering is distinguishable from hardware.

Failing checks produce a nonzero exit code and remain visible in the result
artifact. Independent suites continue so one failure does not hide the rest.
Only selected, privacy-checked JSON/text reports are uploaded. Generated
pictures, stage captures, databases and raw crash logs stay in the temporary
profile. No real photos or private account data are needed.
