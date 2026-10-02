using System.Diagnostics;
using System.IO;
using NB.Core.IO;
using NB.Core.Live;
using NB.Core.Mods;

namespace NB.Multiplayer.Services;

/// <summary>
/// Character Select for every game NB Multiplayer starts (solo, Xbox LIVE rooms and co-op): writes the chosen character
/// into the running game's "charsel" exe-mod mailbox (<see cref="Characters"/>) and keeps it there (a restarted game
/// starts with Banjo again). The game uses it at the next player spawn. Games without the Character Select mod (the
/// exe mod's hook word is missing) are left alone.
/// </summary>
public sealed class CharacterWriter : IDisposable
{
    readonly Func<Process?> _game;
    readonly Func<Character> _character;
    readonly Func<string?> _xex;
    readonly CancellationTokenSource _stop = new();
    /// <summary>What the running game has: null = no game / no Character Select in it.</summary>
    public Character? InGame { get; private set; }

    public CharacterWriter(Func<Process?> game, Func<Character> character, Func<string?> xexPath)
    {
        _game = game; _character = character; _xex = xexPath;
        new Thread(Run) { IsBackground = true, Name = "NB character select" }.Start();
    }

    void Run()
    {
        XeniaLive? x = null; int pid = 0;
        void Detach() { try { x?.Dispose(); } catch (Exception) { } x = null; pid = 0; InGame = null; }
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var p = _game();
                bool running = p != null && !Exited(p);
                if (!running) { Detach(); Thread.Sleep(1500); continue; }
                if (p!.Id != pid)
                {
                    Detach();
                    var xex = _xex();
                    if (xex == null || !File.Exists(xex)) { Thread.Sleep(1500); continue; }
                    var img = NB.Core.Formats.XexFile.Read(File.ReadAllBytes(xex)).GetImage();
                    x = XeniaLive.AttachPid(p.Id, img.AsSpan((int)(XeniaLive.TextStart - 0x82000000), 64).ToArray());
                    pid = p.Id;
                }
                if (x!.U32(Characters.Hook) != Characters.HookWord) { InGame = null; Thread.Sleep(1500); continue; }   // no Character Select
                var c = _character();
                var (town, any) = Characters.MailboxWords(c);
                if (x.U32(Characters.MailboxTown) != town) W32(x, Characters.MailboxTown, town);
                if (x.U32(Characters.MailboxAny) != any) W32(x, Characters.MailboxAny, any);
                InGame = c;
            }
            catch (Exception) { Detach(); }   // the game is still starting (no image yet) or has closed
            Thread.Sleep(1000);
        }
        Detach();
    }

    static void W32(XeniaLive x, uint a, uint v) { var b = new byte[4]; BE.W32(b, 0, v); x.Write(a, b); }
    static bool Exited(Process p) { try { return p.HasExited; } catch (Exception) { return true; } }

    public void Dispose() => _stop.Cancel();
}
