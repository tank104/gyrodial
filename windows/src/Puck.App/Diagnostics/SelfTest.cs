using System.IO;
using System.Text;
using Puck.App.Transport;
using Puck.Core;

namespace Puck.App.Diagnostics;

/// <summary>
/// <c>Puck.exe --self-test</c>: runs the connection logic against an in-memory puck that answers the
/// way the firmware does (docs/PROTOCOL.md): handshake, pairing, keepalive, messages both ways,
/// forgetting. No hardware, no ports, no Bluetooth. Prints PASS/FAIL; exit code 0 when all pass.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var file = Path.Combine(Path.GetTempPath(), $"puck-selftest-{Environment.ProcessId}.json");
        int failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok) failures++;
        }
        static bool WaitFor(Func<bool> condition, int ms = 2000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        try
        {
            var settings = new SettingsStore(file);
            using var hub = new ConnectionManager(settings, discovery: false);
            var volumes = new List<int>();
            using var sub = hub.On(Protocol.T.Vol, m => { lock (volumes) volumes.Add(m.GetInt("v", -1)); });
            bool? connectedEvent = null;
            hub.ConnectionChanged += (_, c) => connectedEvent = c;
            hub.Start();

            // 1. an unknown puck: our hello makes it show the pairing numbers
            var puck = new FakePuck("a1b2c3d4e5f60708", "Puck-1A2B");
            hub.AttachLink(puck);
            Check("hello sent on attach", WaitFor(() => puck.Received(Protocol.T.Hello).Any()));
            Check("hello carries the host id", puck.Received(Protocol.T.Hello).FirstOrDefault()?.GetString("id") == settings.Current.HostId);
            Check("unknown puck -> Pairing", WaitFor(() => hub.State == LinkState.Pairing));
            Check("pairing shows the puck's name", hub.PairingPuckName == "Puck-1A2B");
            Check("nothing but hello/ping before pairing", puck.All.All(m => m.Type is Protocol.T.Hello or Protocol.T.Ping or Protocol.T.Pair));

            // 2. the user dials the numbers in
            puck.CompletePairing();
            Check("pair ok -> Connected", WaitFor(() => hub.IsConnected && hub.State == LinkState.Connected));
            Check("ConnectionChanged(true) raised", WaitFor(() => connectedEvent == true));
            Check("puck remembered in settings", settings.Current.Pucks.Any(p => p.Id == "a1b2c3d4e5f60708" && p.Name == "Puck-1A2B"));
            Check("time sent on link-up", WaitFor(() => puck.Received(Protocol.T.Time).Any()));
            Check("cfg sent on link-up", WaitFor(() => puck.Received(Protocol.T.Cfg).Any()));
            var timeIndex = puck.All.FindIndex(m => m.Type == Protocol.T.Time);
            var cfgIndex = puck.All.FindIndex(m => m.Type == Protocol.T.Cfg);
            Check("time before cfg", timeIndex >= 0 && cfgIndex > timeIndex);
            Check("PuckInfo from hello", hub.Puck is { Name: "Puck-1A2B", Firmware: "0.1.0", Transport: "usb", Battery: 87 });

            // 3. messages both ways
            puck.Say("""{"t":"vol","v":40}""");
            puck.Say("""{"t":"vol","v":41}""");
            Check("puck -> app messages reach handlers, in order", WaitFor(() => { lock (volumes) return volumes.SequenceEqual([40, 41]); }));
            puck.Say("""{"t":"face","f":"claude"}""");
            Check("face tracked", WaitFor(() => hub.CurrentFace == "claude"));
            puck.Say("""{"t":"bat","p":55,"chg":false,"usb":false}""");
            Check("battery tracked", WaitFor(() => hub.Puck?.Battery == 55 && hub.Puck?.Charging == false));
            hub.Send(Protocol.T.State, new { vol = 33, mute = false });
            Check("app -> puck messages delivered", WaitFor(() => puck.Received(Protocol.T.State).Any(m => m.GetInt("vol") == 33)));
            puck.Say("I (1234) boot: not json");
            Thread.Sleep(100);
            Check("log noise ignored", hub.IsConnected);

            // 4. settings changes go out as cfg
            int cfgCount = puck.Received(Protocol.T.Cfg).Count();
            settings.Update(s => s.Brightness = 42);
            Check("settings change -> cfg", WaitFor(() => puck.Received(Protocol.T.Cfg).Count() > cfgCount
                && puck.Received(Protocol.T.Cfg).Last().GetInt("bright") == 42));

            // 5. keepalive
            Check("ping within 4 s", WaitFor(() => puck.Received(Protocol.T.Ping).Any(), 4000));

            // 6. forgetting
            hub.Forget();
            Check("unpair sent", WaitFor(() => puck.Received(Protocol.T.Unpair).Any()));
            Check("disconnected after forget", WaitFor(() => !hub.IsConnected));
            Check("ConnectionChanged(false) raised", WaitFor(() => connectedEvent == false));
            Check("puck removed from settings", settings.Current.Pucks.Count == 0);
            Check("link closed after forget", WaitFor(() => puck.IsClosed));

            // 7. a known puck connects straight away
            settings.Update(s => s.Pucks.Add(new KnownPuck { Id = "ffeeddccbbaa9988", Name = "Puck-9988" }));
            var known = new FakePuck("ffeeddccbbaa9988", "Puck-9988", pairedHosts: [settings.Current.HostId]);
            hub.AttachLink(known);
            Check("known puck -> Connected without pairing", WaitFor(() => hub.IsConnected && hub.Puck?.Id == "ffeeddccbbaa9988"));

            // 8. it goes quiet: 10 s without a message = gone
            known.Mute = true;
            Check("silent puck dropped after the timeout", WaitFor(() => !hub.IsConnected, 13000));

            // 9. a pairing cancelled on the puck: the flyout says so, and the (USB) link stays, quiet
            var stranger = new FakePuck("0102030405060708", "Puck-0708");
            hub.AttachLink(stranger);
            Check("stranger -> Pairing", WaitFor(() => hub.State == LinkState.Pairing));
            stranger.Say("""{"t":"pair","s":"fail"}""");
            Check("pair fail -> no longer Pairing", WaitFor(() => hub.State != LinkState.Pairing));
            Check("pair fail -> 'cancelled on the puck' notice", WaitFor(() => hub.Notice?.Kind == PairingNoticeKind.Cancelled));
            Check("pair fail over USB keeps the link for Try again", !stranger.IsClosed);

            // 10. Try again while the puck still refuses: hello first, then pair start; "refused" -> countdown
            stranger.RefuseFor = 42;
            int hellosBefore = stranger.Received(Protocol.T.Hello).Count();
            Thread.Sleep(1600);  // past the hello-reply window, so StartPairing says hello again
            hub.StartPairing();
            Check("Try again sends hello before pair start", WaitFor(() =>
            {
                var all = stranger.All;
                int lastHello = all.FindLastIndex(m => m.Type == Protocol.T.Hello);
                int lastStart = all.FindLastIndex(m => m.Type == Protocol.T.Pair && m.GetString("a") == "start");
                return all.Count(m => m.Type == Protocol.T.Hello) > hellosBefore && lastHello >= 0 && lastStart > lastHello;
            }));
            Check("refused -> notice with a retry time", WaitFor(() => hub.Notice is { Kind: PairingNoticeKind.Refused } n
                && (n.RetryAtUtc - DateTime.UtcNow).TotalSeconds is > 30 and < 45));
            Check("refused -> not Pairing", hub.State != LinkState.Pairing);

            // 11. progress and the puck's own timeout
            var fresh = new FakePuck("aaaaaaaaaaaaaaaa", "Puck-AAAA");
            hub.AttachLink(fresh);
            Check("Pairing once the puck says 'pair start'", WaitFor(() => hub.State == LinkState.Pairing && hub.PairingPuckName == "Puck-AAAA"));
            fresh.Say("""{"t":"pair","s":"progress","n":2}""");
            Check("pair progress -> 2 of 3", WaitFor(() => hub.PairingProgress == 2));
            fresh.Say("""{"t":"pair","s":"timeout"}""");
            Check("pair timeout -> notice, window closed", WaitFor(() => hub.Notice?.Kind == PairingNoticeKind.TimedOut && !hub.PairingWindowOpen));
            Check("pair timeout -> not Pairing", hub.State != LinkState.Pairing);

            // 12. a puck that never says "pair start" (busy with another PC) isn't shown as pairing
            var busy = new FakePuck("bbbbbbbbbbbbbbbb", "Puck-BBBB") { AutoPairStart = false };
            hub.AttachLink(busy);
            Check("no 'pair start' -> not Pairing", WaitFor(() => busy.Received(Protocol.T.Hello).Any()) && !WaitFor(() => hub.State == LinkState.Pairing, 600));

            // 13. pairing over two transports at once: "ok" on both, one notification
            int pairedEvents = 0;
            hub.Paired += _ => Interlocked.Increment(ref pairedEvents);
            var twinA = new FakePuck("cccccccccccccccc", "Puck-CCCC");
            var twinB = new FakePuck("cccccccccccccccc", "Puck-CCCC");
            hub.AttachLink(twinA);
            hub.AttachLink(twinB);
            Check("twin links handshake", WaitFor(() => twinA.Received(Protocol.T.Hello).Any() && twinB.Received(Protocol.T.Hello).Any()));
            Thread.Sleep(200);
            twinA.CompletePairing();
            twinB.CompletePairing();
            Check("paired on both links -> Connected", WaitFor(() => hub.IsConnected && hub.Puck?.Id == "cccccccccccccccc"));
            Thread.Sleep(300);
            Check("'Puck paired' raised once", Volatile.Read(ref pairedEvents) == 1);

            // 14. the connected puck restarts (its USB port stays open): it says hello on its own
            //     ("new":true, paired meaningless); the app answers, and sends everything again
            // the active link is the one the link-up cfg went to
            Check("link-up cfg sent", WaitFor(() => twinA.Received(Protocol.T.Cfg).Any() || twinB.Received(Protocol.T.Cfg).Any()));
            var active = twinA.Received(Protocol.T.Cfg).Any() ? twinA : twinB;
            int hellosToActive = active.Received(Protocol.T.Hello).Count();
            int cfgToActive = active.Received(Protocol.T.Cfg).Count();
            active.Restart(markNew: true);
            Check("restart: app answers the puck's own hello", WaitFor(() => active.Received(Protocol.T.Hello).Count() > hellosToActive));
            Check("restart: still connected", WaitFor(() => hub.IsConnected && hub.State == LinkState.Connected));
            Check("restart: cfg sent again", WaitFor(() => active.Received(Protocol.T.Cfg).Count() > cfgToActive));

            // 15. older firmware: a hello long after ours, without "new", is a restart all the same
            Thread.Sleep(1700);
            hellosToActive = active.Received(Protocol.T.Hello).Count();
            active.Restart(markNew: false);
            Check("unmarked unprompted hello answered", WaitFor(() => active.Received(Protocol.T.Hello).Count() > hellosToActive));
            Check("unmarked restart: still connected", WaitFor(() => hub.IsConnected));
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAIL  exception: {e}");
            failures++;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }

        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>An in-memory puck speaking the protocol the way the firmware does.</summary>
    sealed class FakePuck(string id, string name, IEnumerable<string>? pairedHosts = null) : PuckLink
    {
        readonly LineSplitter _rx = new();
        readonly HashSet<string> _hosts = new(pairedHosts ?? []);
        readonly List<PuckMessage> _received = [];
        string? _pendingHost;
        DateTime _refusedUntil = DateTime.MinValue;

        public override string Kind => "usb";
        public override string Description => $"fake {name}";
        public volatile bool Mute;
        /// <summary>Starts pairing on its own when an unknown PC says hello (as the firmware does).</summary>
        public bool AutoPairStart { get; init; } = true;
        /// <summary>Seconds to refuse this PC for, from the next "pair start" on (a cancel on the puck).</summary>
        public int RefuseFor { get; set; }

        public List<PuckMessage> All { get { lock (_received) return [.. _received]; } }
        public IEnumerable<PuckMessage> Received(string type) => All.Where(m => m.Type == type);

        readonly System.Threading.Channels.Channel<string> _out = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Task? _speaker;

        /// <summary>Sends a line to the app, in order, from a thread of its own (like a transport's reader).</summary>
        public void Say(string line)
        {
            _out.Writer.TryWrite(line);
            lock (_out)
                _speaker ??= Task.Run(async () =>
                {
                    await foreach (var l in _out.Reader.ReadAllAsync())
                        OnBytes(Encoding.UTF8.GetBytes(l + "\n"));
                });
        }

        public void CompletePairing()
        {
            if (_pendingHost is null) return;
            _hosts.Add(_pendingHost);
            Say("""{"t":"pair","s":"ok"}""");
        }

        /// <summary>A reboot with the port left open: the boot hello, which knows no host yet.</summary>
        public void Restart(bool markNew)
        {
            _pendingHost = null;
            Say(Hello(paired: false, markNew));
        }

        string Hello(bool paired, bool markNew = false)
        {
            var extra = markNew ? ",\"new\":true" : "";
            return $$"""{"t":"hello","v":1,"fw":"0.1.0","id":"{{id}}","name":"{{name}}","paired":{{(paired ? "true" : "false")}},"bat":87,"chg":true,"usb":true,"face":"home"{{extra}}}""";
        }

        void PairStart()
        {
            var left = _refusedUntil - DateTime.UtcNow;
            if (left > TimeSpan.Zero)
                Say($$"""{"t":"pair","s":"refused","retry":{{(int)Math.Ceiling(left.TotalSeconds)}}}""");
            else
                Say("""{"t":"pair","s":"start"}""");
        }

        protected override void SendBytes(byte[] data)
        {
            foreach (var line in _rx.Push(data))
            {
                var m = PuckMessage.TryParse(line);
                if (m is null) continue;
                lock (_received) _received.Add(m);
                if (Mute) continue;
                switch (m.Type)
                {
                    case Protocol.T.Hello:
                        var host = m.GetString("id") ?? "";
                        bool paired = _hosts.Contains(host);
                        if (!paired) _pendingHost = host;
                        Say(Hello(paired));
                        if (!paired && AutoPairStart && DateTime.UtcNow >= _refusedUntil) Say("""{"t":"pair","s":"start"}""");
                        break;
                    case Protocol.T.Pair when m.GetString("a") == "start":
                        if (_pendingHost is null) break;  // never said hello: the firmware drops it too
                        if (RefuseFor > 0 && _refusedUntil == DateTime.MinValue) _refusedUntil = DateTime.UtcNow.AddSeconds(RefuseFor);
                        PairStart();
                        break;
                    case Protocol.T.Ping:
                        Say("""{"t":"pong"}""");
                        break;
                    case Protocol.T.Unpair:
                        _hosts.Clear();
                        Say("""{"t":"pair","s":"none"}""");
                        break;
                }
            }
        }

        protected override void CloseTransport() { }
    }
}
