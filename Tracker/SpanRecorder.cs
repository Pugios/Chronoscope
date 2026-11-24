using Chronoscope.Tracker.Sampling;
using Chronoscope.Tracker.Storage;

namespace Chronoscope.Tracker;

// Turns a stream of polls into spans: while consecutive samples describe the same thing, the
// open span is stretched; when they differ, it is closed and a new one opens.
//
// "The same thing" is the executable plus the exact window title, which is the granularity
// ManicTime's Applications timeline has (a browser tab switch or a changed document title is a
// new span). Idle is one state regardless of which window is behind it.
public sealed class SpanRecorder(ActivityStore store, TimeSpan idleAfter, TimeSpan maxGap)
{
    private long? _openId;
    private string? _openKey;
    private DateTimeOffset _lastPoll;

    public void Observe(ActivitySample sample, DateTimeOffset now)
    {
        // A poll that arrives long after the previous one means the machine slept or the process
        // was frozen. The open span already ends at the last heartbeat, which is the last moment
        // we know the user was there, so it is left alone: stretching it across the sleep would
        // invent hours of activity. The new sample starts a fresh span.
        if (_openId is not null && now - _lastPoll > maxGap)
        {
            _openId = null;
            _openKey = null;
        }
        _lastPoll = now;

        // Idle starts when the threshold is crossed, not backdated to the last keystroke. Reading
        // a long page or watching a video without touching the mouse is still time spent in that
        // window; only a threshold's worth of true absence counts as idle.
        SpanKind kind;
        string key;
        if (sample.IdleFor >= idleAfter)
        {
            kind = SpanKind.Idle;
            key = "idle";
        }
        else if (sample.Window is null)
        {
            // Nothing has focus (lock screen, secure desktop). Not activity, and not idle either.
            Close(now);
            return;
        }
        else
        {
            kind = SpanKind.Active;
            key = $"{sample.Window.ExePath}|{sample.Window.Title}";
        }

        if (_openId is not null && key == _openKey)
        {
            store.Extend(_openId.Value, now);
            return;
        }

        // The old span runs up to the moment the change was noticed, so consecutive spans
        // touch exactly and the timeline has no accidental gaps.
        Close(now);
        _openId = store.Open(now, kind, kind == SpanKind.Active ? sample.Window : null);
        _openKey = key;
    }

    // Final heartbeat on shutdown, so a clean exit loses nothing at all.
    public void Stop(DateTimeOffset now) => Close(now);

    private void Close(DateTimeOffset now)
    {
        if (_openId is not null)
        {
            store.Extend(_openId.Value, now);
        }
        _openId = null;
        _openKey = null;
    }
}
