using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ProbablyCoop.Core;

internal static class HeadlessGuest
{
    internal static byte[] ReadFingerprint(string gameDll, string modDll)
    {
        var fingerprint = new byte[Wire.FingerprintSize];
        using (var game = File.OpenRead(gameDll)) SHA256.HashData(game).CopyTo(fingerprint, 0);
        using (var mod = File.OpenRead(modDll)) SHA256.HashData(mod).CopyTo(fingerprint, 32);
        return fingerprint;
    }

    internal static void Run(string code, byte[] fingerprint)
    {
        using var link = new LoopbackLink(fingerprint, 0, code);
        var timer = Stopwatch.StartNew();
        var session = link.Session;
        var shop = session.Shop;
        double Now() => timer.Elapsed.TotalSeconds;
        void Pump()
        {
            link.Tick(Now());
            if (session.State is LinkState.Waiting or LinkState.Incompatible or LinkState.TimedOut)
                throw new InvalidOperationException($"Local session ended: {session.State}");
            Thread.Sleep(15);
        }
        void WaitUntil(Func<bool> condition, string purpose, double limit = 10)
        {
            double deadline = Now() + limit;
            while (!condition())
            {
                if (Now() >= deadline) throw new TimeoutException($"Timed out: {purpose}");
                Pump();
            }
        }

        var cursorPackets = 0;
        session.RemoteCursor += _ => cursorPackets++;
        WaitUntil(() => session.State == LinkState.Connected && shop.Revision != 0, "handshake and first snapshot");
        Console.WriteLine("CONNECTED");
        ShowState();
        while (true)
        {
            // Keep ticking the session even if stdin is waiting for the next command.
            var line = Task.Run(() => Console.ReadLine());
            while (!line.IsCompleted) Pump();
            string value = line.GetAwaiter().GetResult();
            if (value == null || value.Trim() == "exit") return;
            string[] words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) continue;
            uint number = 0;
            if (words.Length > 2 || (words.Length == 2 && !uint.TryParse(words[1], out number)))
                throw new FormatException($"Invalid command: {value}");
            switch (words[0])
            {
                case "snapshot" when words.Length == 1:
                    break;
                case "tile" when words.Length == 2 && number is >= 1 and <= 16 && !shop.View.Active:
                    session.ChangeTile((int)number - 1, Now());
                    WaitUntil(() => !session.HasPendingChange, "tile reply");
                    break;
                case "cursor" when words.Length == 1:
                    session.MoveCursor(32768, 32768, true, true, shop.View.Active ? 1u : 0u);
                    break;
                case "wait" when words.Length == 2 && number <= 120:
                    double until = Now() + number;
                    while (Now() < until) Pump();
                    break;
                case "move" when words.Length == 2 && shop.View.Active:
                    Submit(ShopAction.Move, number);
                    break;
                case "buy" when words.Length == 2 && shop.View.Active:
                    Submit(ShopAction.Buy, number);
                    break;
                case "sell" when words.Length == 2 && shop.View.Active:
                    Submit(ShopAction.Sell, number);
                    break;
                case "ready" when words.Length == 1 && shop.View.Active:
                    Submit(ShopAction.Ready, 0);
                    break;
                default:
                    throw new InvalidOperationException($"Invalid command for the current view: {value}");
            }
            ShowState();
        }

        void Submit(ShopAction action, uint id)
        {
            shop.Submit(action, id, Now());
            WaitUntil(() => !shop.Waiting, $"{action} reply");
        }

        void ShowState()
        {
            var view = shop.View;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                session.Tiles, session.Revision, ShopRevision = shop.Revision,
                Result = shop.Result.ToString(), link.Sent, link.Received, CursorPackets = cursorPackets,
                view.Active, view.Day, view.Cash, Phase = view.Phase.ToString(), view.ReadyMask,
                view.OfferId, OfferAction = view.OfferAction.ToString(), view.Price, view.OfferLabel,
                Items = view.Items.Select(i => new { i.Id, i.Label, i.Quantity, i.Place, i.Owned, i.Movable })
            }));
        }
    }
}
