using System.Media;
using NB.Core.Audio;
using NB.Core.Project;

namespace NB.Studio.Panels;

/// <summary>
/// The one music preview player of NB Studio (Properties' music rows and Audio › Music): ▶ decodes the cue's wave in the
/// background and plays it from memory (the temporary WAV is deleted at once, nothing is left in %TEMP%); a newer ▶ or
/// ■ Stop wins over a decode still running (generation counter), and either panel's Stop stops it.
/// </summary>
public static class MusicPreview
{
    static SoundPlayer? _player;
    static MemoryStream? _audio;
    static int _gen;

    /// <summary>Stops what plays and drops a decode still running.</summary>
    public static void Stop()
    {
        _gen++;
        try { _player?.Stop(); } catch (Exception) { }
        _player?.Dispose(); _player = null;
        _audio?.Dispose(); _audio = null;
    }

    /// <summary>Decodes and plays <paramref name="c"/>; <paramref name="done"/> gets "Playing …", "failed: …" or
    /// "superseded" on the UI thread of <paramref name="ui"/>. <paramref name="silent"/>: decode only (scripted runs).</summary>
    public static void Play(Control ui, Workspace ws, MusicCue c, string? vgmstream, bool silent, Action<string> done)
    {
        int gen = ++_gen;
        var tmp = Path.Combine(Path.GetTempPath(), $"nb_music_{Environment.ProcessId}_{gen}.wav");
        Task.Run(() =>
        {
            try { var e = MusicAudio.ExtractCue(ws, c, tmp, vgmstream); return (Entry: e, Bytes: File.ReadAllBytes(tmp)); }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
        }).ContinueWith(t =>
        {
            if (ui.IsDisposed || !ui.IsHandleCreated) return;
            ui.BeginInvoke(() =>
            {
                if (t.IsFaulted) { done("failed: " + t.Exception!.GetBaseException().Message); return; }
                if (gen != _gen) { done("superseded (a newer ▶ or ■ Stop)"); return; }
                try { _player?.Stop(); } catch (Exception) { }
                _player?.Dispose(); _audio?.Dispose();
                _audio = new MemoryStream(t.Result.Bytes);
                _player = null;
                if (!silent) { _player = new SoundPlayer(_audio); _player.Play(); }
                var e = t.Result.Entry;
                done($"Playing {c.Display} ({c.Bank} #{c.Wave}, {e.CodecName}, {e.Channels} ch, {e.SampleRate} Hz, {e.Seconds:F1} s; {t.Result.Bytes.Length:N0} bytes decoded)");
            });
        });
    }
}
