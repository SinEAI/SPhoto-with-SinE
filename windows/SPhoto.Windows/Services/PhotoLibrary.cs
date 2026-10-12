using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPhoto.Library;

namespace SPhoto.Services;

/// <summary>One photo on disk. Taken is the shooting date, or the file date
/// when the photo carries none.</summary>
internal sealed record PhotoEntry(string Path, DateTime Taken, long Bytes)
{
    /// "Make Model" from EXIF, when the photo says.
    internal string? Camera { get; init; }
    internal long MtimeTicks { get; init; }

    internal string Name => System.IO.Path.GetFileName(Path);
    internal string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// The folders the user keeps photos in and the photos inside them. Folder
/// choices live in settings.json and everything learned about the photos in
/// library.db (SPhoto.Library's index), both under %LOCALAPPDATA%\SPhoto,
/// so a later start only reads files that are new or changed. After each
/// scan, fingerprints and then image analysis fill in in the background.
/// </summary>
internal sealed class PhotoLibrary : IDisposable
{
    internal static readonly HashSet<string> Extensions = new(Scanner.Extensions, StringComparer.OrdinalIgnoreCase);

    private sealed class Settings
    {
        public List<string> Folders { get; set; } = new();
    }

    private readonly string _dataDir;
    private readonly object _lock = new();
    private Settings _settings = new();
    private readonly LibraryIndex _index;
    private readonly LibraryAnalyzer _analyzer;
    private readonly ThumbnailPreparation _thumbnails;
    private int _scanning;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private volatile bool _hashing;
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _debounce;
    private CancellationTokenSource? _fingerprinting;
    private readonly Timer _catalogTimer;
    private int _catalogPending;
    private bool _disposed;

    /// Raised, on a worker thread, a moment after files in a folder change.
    internal event Action? Changed;

    /// <summary>
    /// Raised, on a worker thread and at most about once a second, when
    /// background work changed what Catalog() returns: fingerprints (copies,
    /// tags and pages resolve) or analysis (stacks form).
    /// </summary>
    internal event Action? CatalogChanged;

    /// dataDir is for tests; seedPictures adds the Pictures folder on a
    /// first start.
    internal PhotoLibrary(string? dataDir = null, bool seedPictures = true)
    {
        _dataDir = dataDir ?? DataDir;
        _catalogTimer = new Timer(_ => RaiseCatalogChanged(), null, Timeout.Infinite, Timeout.Infinite);
        Load(seedPictures);
        _index = OpenIndex();
        _analyzer = new LibraryAnalyzer(_index);
        _analyzer.Stored += CatalogChangedSoon;
        _thumbnails = new ThumbnailPreparation(_dataDir);
    }

    /// Where settings live: %LOCALAPPDATA%\SPhoto, or SPHOTO_DATA_DIR (tests).
    internal static string DataDir =>
        Environment.GetEnvironmentVariable("SPHOTO_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPhoto");

    /// The library's database: tags, pages, stacks, versions and the file index.
    internal LibraryIndex Index => _index;

    /// Background image analysis; the editor pauses it while it needs the machine.
    internal LibraryAnalyzer Analyzer => _analyzer;
    internal ThumbnailPreparation Thumbnails => _thumbnails;
    internal bool IsOrganizing => Volatile.Read(ref _scanning) > 0 || _hashing || _analyzer.IsWorking || _thumbnails.Progress.Running;

    internal string WorkSummary
    {
        get
        {
            var p = _thumbnails.Progress;
            if (p.Running) return $"正在整理图片 · {p.Completed}/{p.Total}";
            if (IsOrganizing) return "正在整理图片…";
            return p.Failed > 0 ? $"整理完成 · {p.Failed} 张暂时无法生成预览" : "照片已就绪";
        }
    }

    /// A snapshot of the library (图库): photos by fingerprint, stacks and versions folded.
    internal Catalog Catalog() => _index.Catalog();

    internal static bool IsPhoto(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The photos in the folder of path, in name order as Explorer shows
    /// them: what to step through after opening one file from Explorer.
    /// </summary>
    internal static List<PhotoEntry> Siblings(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var list = new List<PhotoEntry>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (!IsPhoto(file)) continue;
                var info = new FileInfo(file);
                var exif = Exif.Read(file);
                list.Add(new PhotoEntry(file, exif.Taken ?? info.LastWriteTime, info.Length) { Camera = exif.Camera });
            }
        }
        catch (Exception)
        {
            // An unreadable folder still shows the one photo.
        }
        if (!list.Any(p => string.Equals(p.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            var info = new FileInfo(path);
            list.Add(new PhotoEntry(Path.GetFullPath(path), info.Exists ? info.LastWriteTime : DateTime.Now, info.Exists ? info.Length : 0));
        }
        list.Sort((a, b) => NaturalCompare(Path.GetFileName(a.Path), Path.GetFileName(b.Path)));
        return list;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// Explorer's name order (IMG_2 before IMG_10).
    internal static int NaturalCompare(string a, string b)
    {
        try
        {
            return StrCmpLogicalW(a, b);
        }
        catch (Exception)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal IReadOnlyList<string> Folders
    {
        get { lock (_lock) return _settings.Folders.ToList(); }
    }

    private string SettingsPath => Path.Combine(_dataDir, "settings.json");

    private void Load(bool seedPictures)
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new();
            }
            else if (seedPictures)
            {
                // First start: the user's Pictures folder, like the Photos app.
                var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (Directory.Exists(pictures)) _settings.Folders.Add(pictures);
            }
        }
        catch (Exception)
        {
            // A damaged settings file only costs the folder list.
        }
        Watch();
    }

    /// <summary>
    /// Opens library.db. A file that will not open is set aside, not
    /// deleted, since it holds the user's tags and pages; failing that the
    /// library runs from memory for this session.
    /// </summary>
    private LibraryIndex OpenIndex()
    {
        var path = Path.Combine(_dataDir, "library.db");
        try
        {
            Directory.CreateDirectory(_dataDir);
            // index.json was only a cache of what library.db now holds.
            var old = Path.Combine(_dataDir, "index.json");
            if (File.Exists(old)) File.Delete(old);
            return LibraryIndex.Open(path);
        }
        catch (Exception e)
        {
            Crash.Log(e);
        }
        try
        {
            var aside = path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix);
            }
            return LibraryIndex.Open(path);
        }
        catch (Exception e)
        {
            Crash.Log(e);
            return LibraryIndex.Open(":memory:");
        }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings));
    }

    /// Adds a folder; false when it, or a folder containing it, is already in.
    internal bool AddFolder(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        lock (_lock)
        {
            if (_settings.Folders.Any(f => IsInside(folder, f))) return false;
            // A new parent replaces the folders inside it.
            _settings.Folders.RemoveAll(f => IsInside(f, folder));
            _settings.Folders.Add(folder);
            SaveSettings();
        }
        Watch();
        return true;
    }

    /// Stops showing a folder. The files stay where they are.
    internal void RemoveFolder(string folder)
    {
        lock (_lock)
        {
            _settings.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
        }
        Watch();
    }

    internal static bool IsInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The saved snapshot is enough for the first screen; folder I/O follows.
    /// </summary>
    internal Task<List<PhotoEntry>> CachedPhotosAsync() => Task.Run(() =>
    {
        var folders = Folders;
        return Entries(_index.AllFiles().Values.Where(f => folders.Any(root => IsInside(f.Path, root))));
    });

    private static List<PhotoEntry> Entries(IEnumerable<FileRecord> files) => files
        .Select(f => new PhotoEntry(f.Path, f.Taken ?? new DateTime(f.MtimeTicks, DateTimeKind.Utc).ToLocalTime(), f.Size)
        {
            Camera = f.Camera, MtimeTicks = f.MtimeTicks,
        })
        .OrderByDescending(p => p.Taken).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// Refresh the index, then prepare thumbnails and analysis in the background.
    internal Task<List<PhotoEntry>> ScanAsync(CancellationToken cancel = default) => Task.Run(async () =>
    {
        await _scanGate.WaitAsync(cancel).ConfigureAwait(false);
        Interlocked.Increment(ref _scanning);
        try
        {
            var known = _index.AllFiles();
            var result = Scanner.Scan(_index, Folders, cancel);
            foreach (var f in result.Files)
            {
                if (!known.TryGetValue(f.Path, out var old) || !old.Matches(f.Size, f.MtimeTicks))
                    Views.Thumbnails.Forget(f.Path);
            }
            var photos = Entries(result.Files);
            _thumbnails.Start(photos);
            StartBackground();
            return photos;
        }
        finally
        {
            Interlocked.Decrement(ref _scanning);
            _scanGate.Release();
        }
    }, cancel);

    /// <summary>
    /// (Re)starts fingerprinting what the last scan left pending, then wakes
    /// the analyzer. A newer scan cancels the run of an older one.
    /// </summary>
    private void StartBackground()
    {
        CancellationTokenSource cancel;
        lock (_lock)
        {
            if (_disposed) return;
            _fingerprinting?.Cancel();
            _fingerprinting = cancel = new CancellationTokenSource();
            _hashing = true;
        }
        Task.Factory.StartNew(() =>
        {
            try
            {
                Fingerprinter.Run(_index, cancel.Token, new Progress(CatalogChangedSoon));
                _analyzer.Wake();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
                // Closing while it ran.
            }
            catch (Exception e)
            {
                Crash.Log(e);
            }
            finally
            {
                lock (_lock)
                {
                    if (_fingerprinting == cancel)
                    {
                        _fingerprinting = null;
                        _hashing = false;
                    }
                    cancel.Dispose();
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// Calls back on the reporting thread, unlike Progress&lt;T&gt;.
    private sealed class Progress : IProgress<int>
    {
        private readonly Action _report;
        internal Progress(Action report) => _report = report;
        public void Report(int value) => _report();
    }

    /// Raises CatalogChanged about a second from now, once for any number of changes meanwhile.
    private void CatalogChangedSoon()
    {
        if (Interlocked.Exchange(ref _catalogPending, 1) == 0) _catalogTimer.Change(1000, Timeout.Infinite);
    }

    private void RaiseCatalogChanged()
    {
        Interlocked.Exchange(ref _catalogPending, 0);
        try
        {
            CatalogChanged?.Invoke();
        }
        catch (Exception e)
        {
            Crash.Log(e);
        }
    }

    private void Watch()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in Folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception)
            {
                // Network drives may refuse; the user can still refresh.
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Our own temporary files come and go during a save.
        if (e.FullPath.EndsWith(".sphoto-tmp", StringComparison.OrdinalIgnoreCase)) return;
        // library.db changes on every scan: with the data folder inside a
        // photo folder, reacting to it would rescan forever.
        if (IsInside(e.FullPath, _dataDir)) return;
        // A copy of many photos raises hundreds of events: wait for quiet.
        _debounce?.Dispose();
        _debounce = new Timer(_ => Changed?.Invoke(), null, 800, Timeout.Infinite);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _fingerprinting?.Cancel();
        }
        foreach (var w in _watchers) w.Dispose();
        _debounce?.Dispose();
        _catalogTimer.Dispose();
        _thumbnails.Dispose();
        _analyzer.Dispose();
        _index.Dispose();
    }
}

/// <summary>
/// The shooting date of a JPEG without decoding it; SPhoto.Library's Exif
/// reader, kept under its old name for the rest of the app.
/// </summary>
internal static class ExifDate
{
    internal static DateTime? Read(string path) => Exif.Read(path).Taken;

    internal static DateTime? Parse(ReadOnlySpan<byte> jpeg) => Exif.Parse(jpeg).Taken;
}
