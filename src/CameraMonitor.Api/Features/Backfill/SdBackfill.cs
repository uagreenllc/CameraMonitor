using System.Collections.Concurrent;
using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Events;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using CameraMonitor.Api.Infrastructure.Xm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Backfill;

/// <summary>
/// Fills holes in the archive of xm cameras from their microSD: queries the card's files over a second DVRIP session,
/// downloads them, remuxes them into 10-s TS segments at their true camera time and indexes them as Source "sd".
/// A live segment always wins: sd segments that overlap any indexed row are dropped.
/// </summary>
public sealed class SdBackfill(
    IServiceScopeFactory scopes, XmAgentStore agents, StreamStateStore streams, IOptionsMonitor<CamerasOptions> cameras,
    IOptions<ServerOptions> server, SdRemuxer remuxer, BackfillStatus status, TimeProvider clock, ILogger<SdBackfill> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxFileSkew = TimeSpan.FromHours(1);
    public const int MaxGapsPerRun = 20;

    /// <summary>Files downloaded per camera per run: one run must not hold the gate for hours on a new install.</summary>
    public const int MaxFilesPerRun = 20;

    private static readonly TimeSpan QuerySlack = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);   // the live recorder may still be writing the last minutes
    private static readonly TimeSpan MaxRowLength = TimeSpan.FromHours(1);

    // ponytail: rows touching by less than this are not an overlap; HLS durations are rounded, a strict test would drop whole segments
    private static readonly TimeSpan OverlapTolerance = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, byte> _pending = new();

    // ponytail: lost on restart, so a no-files gap is queried once more after a restart
    private readonly HashSet<(StreamKey Key, Gap Gap)> _exhausted = [];

    // ponytail: marked once downloaded, even if the remux then fails; a corrupt file must not be fetched every run. Lost on restart.
    private readonly HashSet<(StreamKey Key, string FileName, DateTime EndLocal)> _doneFiles = [];
    private readonly SemaphoreSlim _gate = new(1, 1);   // one run at a time: guards _exhausted, _doneFiles, _filesLeft and the status counters
    private int _filesLeft = MaxFilesPerRun;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeZoneInfo? _tz;

    internal IReadOnlySet<string> Pending => _pending.Keys.ToHashSet();

    /// <summary>Marks an xm camera pending and wakes the loop; it runs as soon as its DVRIP agent is connected.</summary>
    public void Trigger(string cameraId)
    {
        if (!cameras.CurrentValue.Items.Any(c => c.Id == cameraId && c.Vendor == "xm"))
        {
            return;
        }

        _pending[cameraId] = 0;
        Volatile.Read(ref _wake).TrySetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        streams.StatusChanged += OnStatusChanged;
        agents.Connected += Trigger;
        try
        {
            DeleteDir(Path.Combine(server.Value.DataDir, "tmp", "sd"));   // leftovers of a crash
            var nextAll = clock.GetUtcNow() + Interval;
            while (!ct.IsCancellationRequested)
            {
                // A new wake before the pending set is read: a Trigger from here on wakes the wait below.
                var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _wake, wake);
                try
                {
                    var due = clock.GetUtcNow() >= nextAll;
                    if (due)
                    {
                        nextAll = clock.GetUtcNow() + Interval;
                    }

                    foreach (var cam in cameras.CurrentValue.Items.Where(c => c.Vendor == "xm").ToList())
                    {
                        if ((due || _pending.ContainsKey(cam.Id)) && IsConnected(cam.Id))
                        {
                            _pending.TryRemove(cam.Id, out _);
                            await RunSafeAsync(cam.Id, ct);
                        }
                    }
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(e, "SD backfill loop failed");
                }

                var wait = nextAll - clock.GetUtcNow();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(wake.Task, Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, clock, stop.Token));
                await stop.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            logger.LogError(e, "SD backfill stopped");
        }
        finally
        {
            streams.StatusChanged -= OnStatusChanged;
            agents.Connected -= Trigger;
        }
    }

    /// <summary>All lenses of one xm camera, newest gaps first, at most <see cref="MaxGapsPerRun"/> per lens.</summary>
    public async Task RunOnceAsync(string cameraId, CancellationToken ct)
    {
        var cam = cameras.CurrentValue.Items.FirstOrDefault(c => c.Id == cameraId && c.Vendor == "xm");
        if (cam is null || agents.Agent(cameraId) is not { } agent)
        {
            return;
        }

        var st = status.GetOrAdd(cameraId);
        await _gate.WaitAsync(ct);
        try
        {
            st.LastError = null;
            var o = server.Value;
            var now = clock.GetUtcNow().UtcDateTime;
            var from = now.AddDays(-o.SdLoopDays);
            var to = now - Settle;
            var minGap = TimeSpan.FromSeconds(o.SdMinGapSeconds);
            _exhausted.RemoveWhere(e => e.Gap.ToUtc < from);
            _doneFiles.RemoveWhere(f => ToUtc(f.EndLocal) < from);
            _filesLeft = MaxFilesPerRun;
            var work = new List<(LensConfig Lens, Gap Gap)>();
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var lens in cam.Lenses)
                {
                    var rows = await db.Segments.Where(s => s.CameraId == cam.Id && s.LensId == lens.Id && s.StartUtc >= from)
                        .OrderBy(s => s.StartUtc).Select(s => new { s.StartUtc, s.DurationMs }).ToListAsync(ct);
                    var key = new StreamKey(cam.Id, lens.Id);
                    work.AddRange(GapFinder.Find(rows.Select(r => (r.StartUtc, r.DurationMs)), from, to, minGap)
                        .Where(g => !IsExhausted(key, g)).Take(MaxGapsPerRun).Select(g => (lens, g)));
                }
            }

            if (work.Count == 0)
            {
                return;
            }

            st.Queued = work.Count;
            await using var client = await agent.CreateDownloadClientAsync(ct);
            foreach (var (lens, gap) in work)
            {
                st.Current = $"{lens.Id} {ToLocal(gap.FromUtc):HH:mm}–{ToLocal(gap.ToUtc):HH:mm}";
                try
                {
                    await FillGapAsync(cam, lens, gap, client, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    st.LastError = $"{lens.Id}: {e.Message}";
                    logger.LogWarning("SD backfill {Camera}/{Lens} {From:o}–{To:o} failed: {Error}", cam.Id, lens.Id, gap.FromUtc, gap.ToUtc, e.Message);
                }

                st.Queued--;
                if (_filesLeft == 0)
                {
                    Trigger(cameraId);   // continue soon, after the other pending cameras had their turn
                    break;
                }
            }
        }
        finally
        {
            st.Queued = 0;
            st.Current = null;
            st.LastRunUtc = clock.GetUtcNow();
            _gate.Release();
        }
    }

    /// <summary>Downloads the card's files around one gap and indexes the segments that fall into it. Returns the segments added.</summary>
    internal async Task<int> FillGapAsync(CameraConfig cam, LensConfig lens, Gap gap, DvripClient client, CancellationToken ct)
    {
        var st = status.GetOrAdd(cam.Id);
        var key = new StreamKey(cam.Id, lens.Id);
        var o = server.Value;
        var lo = gap.FromUtc - QuerySlack;
        var hi = gap.ToUtc + QuerySlack;
        var files = await client.QueryFilesAsync(lens.DvripChannel, ToLocal(lo), ToLocal(hi), ct);
        if (files.Count == 0)
        {
            _exhausted.Add((key, gap));
            st.GapsNoFiles++;
            logger.LogInformation("SD backfill {Camera}/{Lens}: no files on the card for {From:o}–{To:o}", cam.Id, lens.Id, gap.FromUtc, gap.ToUtc);
            return 0;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var since = lo - MaxRowLength;
        var taken = (await db.Segments.Where(s => s.CameraId == cam.Id && s.LensId == lens.Id && s.StartUtc < hi && s.StartUtc > since)
                .Select(s => new { s.StartUtc, s.DurationMs }).ToListAsync(ct))
            .Select(s => (Start: s.StartUtc, End: s.StartUtc.AddMilliseconds(s.DurationMs))).ToList();
        var recDir = streams.TryGet(key, out var state) && state.RecDir.Length > 0
            ? state.RecDir
            : FfmpegArgs.NormalizePath(Path.Combine(o.DataDir, "rec", cam.Id, lens.Id));

        var added = 0;
        var cutShort = false;
        // Newest first: the card overwrites its oldest files, and the newest gaps are the ones people look for.
        foreach (var file in files.Where(f => !_doneFiles.Contains((key, f.FileName, f.EndLocal))).OrderByDescending(f => f.BeginLocal))
        {
            if (_filesLeft == 0)
            {
                cutShort = true;   // not exhausted: the next run continues with the older files
                break;
            }

            _filesLeft--;
            var tmp = Path.Combine(o.DataDir, "tmp", "sd", cam.Id, lens.Id, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var parser = new XmFrameParser();
                var annexB = Path.Combine(tmp, "in.264");
                await using (var output = File.Create(annexB))
                {
                    await client.DownloadAsync(file, new XmStripStream(parser, output), ct);
                }

                _doneFiles.Add((key, file.FileName, file.EndLocal));

                if (parser.FirstIFrame is not { } first)
                {
                    logger.LogWarning("SD backfill {Camera}/{Lens}: {File} has no video, skipped", cam.Id, lens.Id, file.FileName);
                    continue;
                }

                var fileStart = ToUtc(first.CameraTime ?? file.BeginLocal);
                if (fileStart < gap.FromUtc - MaxFileSkew || fileStart > gap.ToUtc + MaxFileSkew)
                {
                    logger.LogWarning("SD backfill {Camera}/{Lens}: {File} starts at {Start:o}, too far from the gap {From:o}–{To:o}; skipped",
                        cam.Id, lens.Id, file.FileName, fileStart, gap.FromUtc, gap.ToUtc);
                    continue;
                }

                var fps = first.Fps is > 0 and <= 60 ? first.Fps : 15;
                var segments = await remuxer.RemuxAsync(annexB, fps, tmp, ct);
                var firstKept = true;
                foreach (var seg in segments)
                {
                    var start = fileStart + seg.Start;
                    var durationMs = (int)Math.Round((seg.End - seg.Start).TotalMilliseconds);
                    var end = start.AddMilliseconds(durationMs);
                    if (end <= gap.FromUtc || start >= gap.ToUtc
                        || taken.Any(t => start < t.End - OverlapTolerance && t.Start < end - OverlapTolerance))
                    {
                        continue;
                    }

                    var target = $"{recDir}/{start:yyyyMMdd}/{start:HH}/{start:HHmmss}.ts";
                    if (File.Exists(target))
                    {
                        continue;   // never overwrite
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try
                    {
                        // Fresh mtime: DirectoryScanner indexes unknown files older than MinAge by mtime, and remuxing plus
                        // moving a long card file takes longer than that. Wall clock on purpose: the scanner compares file mtimes.
                        File.SetLastWriteTimeUtc(seg.Path, DateTime.UtcNow);
                        File.Move(seg.Path, target);
                    }
                    catch (IOException e)
                    {
                        logger.LogDebug("SD backfill {Camera}/{Lens}: {Target} not moved: {Error}", cam.Id, lens.Id, target, e.Message);
                        continue;
                    }

                    // The row goes in right after the move (DirectoryScanner would index an unknown file by its mtime),
                    // and is not cancellable: a moved file without its row would be indexed at the wrong time.
                    var row = new SegmentRow
                    {
                        CameraId = cam.Id,
                        LensId = lens.Id,
                        StartUtc = start,
                        DurationMs = durationMs,
                        Path = SegmentIndexer.RelativePath(o.DataDir, target),
                        Bytes = new FileInfo(target).Length,
                        Discontinuity = firstKept,
                        Source = "sd",
                    };
                    db.Segments.Add(row);
                    try
                    {
                        await db.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        db.Entry(row).State = EntityState.Detached;
                        if (e is DbUpdateException && await db.Segments.AnyAsync(r => r.Path == row.Path, CancellationToken.None))
                        {
                            // Someone (DirectoryScanner) indexed the file meanwhile: its row points at it, so keep the file.
                            logger.LogWarning("SD backfill {Camera}/{Lens}: {Target} was indexed by someone else, kept", cam.Id, lens.Id, target);
                            continue;
                        }

                        try
                        {
                            File.Delete(target);
                        }
                        catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                        {
                            logger.LogWarning("SD backfill {Camera}/{Lens}: {Target} not deleted: {Error}", cam.Id, lens.Id, target, deleteError.Message);
                        }

                        if (e is not DbUpdateException)
                        {
                            throw;
                        }

                        logger.LogDebug("SD backfill {Camera}/{Lens}: {Target} not indexed: {Error}", cam.Id, lens.Id, target, e.Message);
                        continue;
                    }

                    taken.Add((start, end));
                    firstKept = false;
                    added++;
                    st.SegmentsAdded++;
                }

                st.FilesDone++;
            }
            finally
            {
                DeleteDir(tmp);
            }
        }

        if (added == 0 && !cutShort)
        {
            _exhausted.Add((key, gap));   // the card has nothing usable here: do not download the same files every run
        }

        logger.LogInformation("SD backfill {Camera}/{Lens}: {Added} segments added for {From:o}–{To:o}", cam.Id, lens.Id, added, gap.FromUtc, gap.ToUtc);
        return added;
    }

    /// <summary>
    /// Inside a gap already found empty: the leading gap starts at now − SdLoopDays and so shifts every run,
    /// but a shifted copy of an empty gap is still empty.
    /// </summary>
    private bool IsExhausted(StreamKey key, Gap gap) =>
        _exhausted.Any(e => e.Key == key && e.Gap.FromUtc <= gap.FromUtc && gap.ToUtc <= e.Gap.ToUtc);

    private bool IsConnected(string cameraId) =>
        agents.TryGet(cameraId, out var s) && s.Connected && agents.Agent(cameraId) is not null;

    private void OnStatusChanged(StreamState state, StreamStatus previous)
    {
        if (previous == StreamStatus.Offline && state.Status == StreamStatus.Online)
        {
            Trigger(state.Key.CameraId);
        }
    }

    private async Task RunSafeAsync(string cameraId, CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(cameraId, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            status.GetOrAdd(cameraId).LastError = e.Message;
            logger.LogWarning("SD backfill {Camera} failed: {Error}", cameraId, e.Message);
        }
    }

    private TimeZoneInfo Tz => _tz ??= ResolveTimeZone();

    private TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(server.Value.TimeZone);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogError("Unknown Server:TimeZone '{Zone}', SD backfill treats camera time as UTC", server.Value.TimeZone);
            return TimeZoneInfo.Utc;
        }
    }

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    private DateTime ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // ponytail: a time in the spring-forward hole does not exist on the wall clock; shift it by 1 h (the European DST delta).
        // An ambiguous autumn hour resolves to standard time, so up to 1 h of a fall-back night may land 1 h off.
        if (Tz.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, Tz);
    }

    private void DeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("SD backfill: temp dir {Dir} not deleted: {Error}", dir, e.Message);
        }
    }
}
