using LongshotLauncher.Core;

/// Scanning a tag with the controller (Core/NfcReader.cs) with no controller: a fake one that answers as the real one does, over a
/// fake tag made here (NfcChecks' FakeTag). The commands' bytes, the answers read, the scan's order, and the file a scan is saved as.
static partial class Program
{
    /// A controller's NFC reader as a script: what it was sent, and its answers, an 8-byte header echoed and then the payload
    sealed class FakeReader
    {
        public readonly List<byte[]> Sent = new();
        public byte[]? Tag;
        /// The State answers after a Start before the tag is found
        public int FindAfter = 3;
        /// The tag refuses the figure tags' password (a read with it fails with 0x48)
        public bool Refuses;
        /// Every read fails with this error
        public int? Broken;
        /// The reader goes idle once, at this State answer, as when the tag is lifted
        public int IdleAt = -1;
        /// The buffer is this many bytes short
        public int Short;
        /// The controller stops answering at this command
        public int DeadAt = -1;
        public Action? OnState;
        int state, error, asked, states, pending = -1, pendingIn;

        public IEnumerable<byte> Subs => Sent.Select(c => c[3]);
        public int Count(byte sub) => Subs.Count(s => s == sub);

        public byte[] Exchange(byte[] command)
        {
            if (Sent.Count == DeadAt) throw new IOException("the controller did not answer (Windows error 121)");
            Sent.Add(command);
            var data = command.AsSpan(8).ToArray();
            var payload = Array.Empty<byte>();
            switch (command[3])
            {
                case NfcReader.SubStop: (state, pending) = (0, -1); break;
                case NfcReader.SubStart: (state, asked) = (1, 0); break;
                case NfcReader.SubState:
                    OnState?.Invoke();
                    if (states++ == IdleAt) state = 0;
                    else if (state == 1 && Tag != null && ++asked >= FindAfter) state = 9;
                    else if (pending >= 0 && --pendingIn <= 0) (state, pending) = (pending, -1);
                    payload = new byte[16];
                    (payload[0], payload[1]) = ((byte)state, (byte)(state == 7 ? error : 0));
                    if (state is 9 or 4)
                    {
                        (payload[4], payload[5], payload[6], payload[8]) = (1, 1, 2, 7);
                        Convert.FromHexString(NfcTags.Uid(Tag!)).CopyTo(payload, 9);
                    }
                    break;
                case NfcReader.SubRead:
                    (pending, pendingIn) = (state != 9 || Broken != null || (Refuses && data[9] == 1) ? 7 : 4, 2);
                    error = Broken ?? (state != 9 ? 0x01 : 0x48);
                    break;
                case NfcReader.SubBuffer:
                    var buffer = Enumerable.Repeat((byte)0xD5, NfcReader.Described).Concat(Tag!).SkipLast(Short).ToArray();
                    var offset = data[0] | data[1] << 8;
                    var part = buffer.Skip(offset).Take(0x59).ToArray();
                    payload = new[] { (byte)(offset + part.Length >= buffer.Length ? 1 : 0), (byte)part.Length, (byte)0 }.Concat(part).ToArray();
                    break;
                default: throw new Exception($"nfc reader: a command the scan must never send: 0x{command[3]:X2}");
            }
            return command.AsSpan(0, 8).ToArray().Concat(payload).ToArray();
        }
    }

    static void NfcReaderChecks()
    {
        static string Hex(byte[] b) => Convert.ToHexString(b);

        // ---- the commands, byte for byte ----
        Require(Hex(NfcReader.Start()) == "0191000300050000" + "00E8032C01", "Start looking: " + Hex(NfcReader.Start()));
        Require(Hex(NfcReader.Stop()) == "0191000400000000", "Stop: " + Hex(NfcReader.Stop()));
        Require(Hex(NfcReader.State()) == "0191000500000000", "State: " + Hex(NfcReader.State()));
        Require(Hex(NfcReader.Read(password: true)) == "0191000600130000" + "D007" + "00000000000000" + "01" + "03" + "003B3C777886" + "0000",
                "Read, with the password: " + Hex(NfcReader.Read(true)));
        Require(Hex(NfcReader.Read(password: false)) == "0191000600130000" + "D007" + "00000000000000" + "00" + "03" + "003B3C777886" + "0000",
                "Read, without: " + Hex(NfcReader.Read(false)));
        Require(Hex(NfcReader.Buffer(0)) == "0191011500020000" + "0000" && Hex(NfcReader.Buffer(0x59)) == "0191011500020000" + "5900"
                && Hex(NfcReader.Buffer(0x0216)) == "0191011500020000" + "1602", "Read buffer: flag 1, the offset little-endian: " + Hex(NfcReader.Buffer(0x0216)));
        Throws<ArgumentException>(() => NfcReader.Command(0x08), "A write command (0x08) was made.");
        Throws<ArgumentException>(() => NfcReader.Command(0x14, 1, 2), "A write command (0x14) was made.");

        // ---- the State answer ----
        var tag = FakeTag(41);
        var uid = Convert.FromHexString(NfcTags.Uid(tag));
        var head = NfcReader.State();
        byte[] Answer(params byte[] payload) => head.Concat(payload).ToArray();
        var found = NfcReader.ParseState(Answer(new byte[] { 9, 0, 0, 0, 1, 1, 2, 0, 7 }.Concat(uid).ToArray()));
        Require(found.State == NfcReaderState.Found && found.Error == 0 && found.Uid!.SequenceEqual(uid), "A tag found, with its 7-byte UID.");
        Require(NfcReader.ParseState(Answer(new byte[] { 4, 0, 0, 0, 1, 1, 2, 0, 7 }.Concat(uid).ToArray())) is { State: NfcReaderState.ReadDone, Uid.Length: 7 },
                "A read finished, with the UID.");
        Require(NfcReader.ParseState(Answer(0)) is { State: NfcReaderState.Idle, Uid: null } && NfcReader.ParseState(Answer(1, 0, 0)) is { State: NfcReaderState.Looking, Uid: null },
                "Idle, and looking.");
        Require(NfcReader.ParseState(Answer(7, 0x48)) is { State: NfcReaderState.Failed, Error: NfcReaderState.PasswordRefused, Uid: null }, "An error with its code.");
        Require(NfcReader.ParseState(Answer(9, 0, 0, 0, 1, 1, 2, 0, 7, 1, 2)).Uid == null && NfcReader.ParseState(Answer(new byte[] { 9, 0, 0, 0, 1, 1, 2, 0, 4 }.Concat(uid).ToArray())).Uid == null,
                "A UID cut short, or of another length, is none.");
        Throws<IOException>(() => NfcReader.ParseState(head), "An answer with no payload was read.");
        Throws<IOException>(() => NfcReader.ParseState(new byte[3]), "A short answer was read.");

        // ---- the buffer, part by part ----
        var (bytes, last) = NfcReader.ParsePart(Answer(0, 3, 0, 0xAA, 0xBB, 0xCC, 0xEE));
        Require(Hex(bytes) == "AABBCC" && !last && NfcReader.ParsePart(Answer(1, 1, 0, 0x11)) is { Last: true, Part.Length: 1 }
                && NfcReader.ParsePart(Answer(1, 0, 0)) is { Last: true, Part.Length: 0 }, "A part: its length, its bytes, the last-part flag.");
        Throws<IOException>(() => NfcReader.ParsePart(Answer(0, 5, 0, 1, 2)), "A part shorter than it says was read.");
        Throws<IOException>(() => NfcReader.ParsePart(Answer(0, 0x5A, 0).Concat(new byte[0x5A]).ToArray()), "A part longer than 0x59 was read.");
        Throws<IOException>(() => NfcReader.ParsePart(Answer(0)), "A part with no length was read.");
        var reader = new FakeReader { Tag = tag };
        var whole = NfcReader.ReadBuffer(reader.Exchange);
        Require(whole.Length == 600 && whole.Take(60).All(b => b == 0xD5) && whole.AsSpan(NfcReader.Described).SequenceEqual(tag), "The buffer: 60 bytes, then the tag.");
        Require(reader.Sent.Select(c => c[8] | c[9] << 8).SequenceEqual(new[] { 0, 89, 178, 267, 356, 445, 534 }) && reader.Sent.All(c => c[2] == 1 && c[3] == 0x15),
                "Asked for at each part's end, until the last: " + string.Join(", ", reader.Sent.Select(c => c[8] | c[9] << 8)));
        Throws<IOException>(() => NfcReader.ReadBuffer(_ => Answer(0, 0, 0)), "A buffer that never ends was read.");

        // ---- a scan ----
        var waits = new List<int>();
        var steps = new List<NfcScanStep>();
        reader = new FakeReader { Tag = tag };
        var scan = NfcReader.Scan(reader.Exchange, default, steps.Add, waits.Add);
        Require(scan.Outcome == NfcScanOutcome.Read && scan.Problem == null && scan.Tag!.SequenceEqual(tag), "A scan gives the tag's 540 bytes.");
        Require(reader.Subs.Take(2).SequenceEqual(new byte[] { 4, 3 }) && waits[0] == 120 && reader.Sent[1].SequenceEqual(NfcReader.Start())
                && reader.Subs.Last() == NfcReader.SubStop, "Stop, a moment, start looking; and stop at the end.");
        Require(reader.Subs.Where(s => s != 5).SequenceEqual(new byte[] { 4, 3, 6, 0x15, 0x15, 0x15, 0x15, 0x15, 0x15, 0x15, 4 })
                && reader.Sent.Single(c => c[3] == 6).SequenceEqual(NfcReader.Read(password: true)), "One read, with the password, then the buffer: "
                + string.Join(" ", reader.Subs.Select(s => s.ToString("X2"))));
        Require(waits.Skip(1).Take(3).All(w => w == 100) && waits.Skip(4).All(w => w == 50) && steps.SequenceEqual(new[] { NfcScanStep.Looking, NfcScanStep.Reading }),
                "Asked every 100 ms for the tag, every 50 ms for the read; looking, then reading.");
        Require(!reader.Subs.Any(s => s is 0x08 or 0x14), "Nothing that writes a tag is sent.");

        // (the tag refuses the password: found again, read without)
        reader = new FakeReader { Tag = tag, Refuses = true };
        waits.Clear();
        scan = NfcReader.Scan(reader.Exchange, default, null, waits.Add);
        Require(scan.Outcome == NfcScanOutcome.Read && scan.Tag!.SequenceEqual(tag), "A tag that refuses the password is read without it.");
        Require(reader.Subs.Where(s => s is not (5 or 0x15)).SequenceEqual(new byte[] { 4, 3, 6, 4, 3, 6, 4 })
                && reader.Sent.Where(c => c[3] == 6).Select(c => c[8 + 9]).SequenceEqual(new byte[] { 1, 0 }), "Read with it, stop, start, read without: "
                + string.Join(" ", reader.Subs.Select(s => s.ToString("X2"))));
        Require(waits.Count(w => w == 120) == 2, "A moment after each stop.");

        // (a tag that fails both ways; one taken away after the first read)
        reader = new FakeReader { Tag = tag, Broken = 0x21 };
        scan = NfcReader.Scan(reader.Exchange, default, null, _ => { });
        Require(scan.Outcome == NfcScanOutcome.Failed && scan.Tag == null && scan.Problem!.Contains("0x21") && reader.Count(6) == 2 && reader.Count(0x15) == 0
                && reader.Subs.Last() == 4, "A tag that cannot be read: said with the error, no buffer asked for, stopped.");
        var lifted = new FakeReader { Tag = tag, Refuses = true };
        lifted.OnState = () => { if (lifted.Count(6) == 1 && lifted.Count(3) == 2) lifted.Tag = null; };
        waits.Clear();
        scan = NfcReader.Scan(lifted.Exchange, default, null, waits.Add);
        Require(scan.Outcome == NfcScanOutcome.Failed && scan.Problem!.Contains("taken away") && lifted.Count(6) == 1 && lifted.Subs.Last() == 4,
                "The tag taken away before the second read: " + scan.Problem);

        // (no tag: asked for 10 s, then stopped)
        reader = new FakeReader();
        waits.Clear();
        scan = NfcReader.Scan(reader.Exchange, default, null, waits.Add);
        Require(scan.Outcome == NfcScanOutcome.NoTag && scan.Tag == null && reader.Count(5) == 100 && waits.Sum() == 120 + 10_000 && reader.Count(6) == 0
                && reader.Subs.Last() == 4, $"No tag: 100 questions over 10 s, no read, stopped ({reader.Count(5)} over {waits.Sum()} ms).");

        // (the reader goes idle while it looks: started again)
        reader = new FakeReader { Tag = tag, IdleAt = 1, FindAfter = 4 };
        scan = NfcReader.Scan(reader.Exchange, default, null, _ => { });
        Require(scan.Outcome == NfcScanOutcome.Read && reader.Count(3) == 2 && reader.Subs.Take(6).SequenceEqual(new byte[] { 4, 3, 5, 5, 4, 3 }),
                "A reader gone idle is started again: " + string.Join(" ", reader.Subs.Take(8).Select(s => s.ToString("X2"))));

        // (a buffer that is short of the tag; a controller that stops answering; a scan the player stops)
        reader = new FakeReader { Tag = tag, Short = 4 };
        scan = NfcReader.Scan(reader.Exchange, default, null, _ => { });
        Require(scan.Outcome == NfcScanOutcome.Failed && scan.Tag == null && scan.Problem!.Contains("536 of"), "A buffer short of the tag is no tag: " + scan.Problem);
        reader = new FakeReader { Tag = tag, DeadAt = 4 };
        scan = NfcReader.Scan(reader.Exchange, default, null, _ => { });
        Require(scan.Outcome == NfcScanOutcome.Failed && scan.Problem!.Contains("did not answer"), "A controller that stops answering: said, not thrown.");
        using var stop = new CancellationTokenSource();
        reader = new FakeReader();
        var pauses = 0;
        scan = NfcReader.Scan(reader.Exchange, stop.Token, null, _ => { if (++pauses == 5) stop.Cancel(); });
        Require(scan.Outcome == NfcScanOutcome.Cancelled && reader.Subs.Last() == 4 && reader.Count(5) < 10, "Stopped by the player: the reader is stopped too.");

        // ---- what a scan is saved as ----
        var folder = Path.Combine(Folder("nfc reader"), "nfc ゲーム");
        var name = "Scanned " + Convert.ToHexString(uid) + ".bin";
        Require(NfcReader.FileName(tag) == name && name.Length == 8 + 14 + 4 && Convert.ToHexString(uid) == Convert.ToHexString(uid).ToUpperInvariant(),
                "Scanned, the UID in 14 upper-case hex digits, .bin: " + NfcReader.FileName(tag));
        var saved = NfcReader.Save(folder, tag);
        Require(saved is { Outcome: NfcTagOutcome.Added, Copied: true } && saved.FileName == name && File.ReadAllBytes(Path.Combine(folder, name)).SequenceEqual(tag)
                && new FileInfo(Path.Combine(folder, name)).Length == 540, "Saved under that name, its 540 bytes.");
        Require(NfcTags.List(folder).Single() is { Valid: true } listed && listed.Uid == Convert.ToHexString(uid) && listed.Model == NfcTags.Model(tag),
                "The saved scan is a tag of the list.");
        Require(NfcReader.Save(folder, tag) is { Outcome: NfcTagOutcome.Same } same && same.FileName == name && Directory.EnumerateFiles(folder).Count() == 1,
                "The same tag scanned again: already there.");
        var written = (byte[])tag.Clone();
        written[0x80] ^= 0xFF;
        Require(NfcReader.Save(folder, written) is { Outcome: NfcTagOutcome.Numbered } second && second.FileName == $"Scanned {Convert.ToHexString(uid)} (2).bin"
                && File.ReadAllBytes(Path.Combine(folder, name)).SequenceEqual(tag), "The tag with other data on it: numbered, the first kept.");
        var plain = (byte[])tag.Clone();
        plain[0x5B] = 0;
        Require(NfcReader.Save(folder, plain) is { Outcome: NfcTagOutcome.Refused, FileName: null, Reason: { } why } && why.Contains("figure")
                && NfcReader.Save(folder, new byte[540]).Outcome == NfcTagOutcome.Refused && NfcReader.Save(folder, new byte[600]).Outcome == NfcTagOutcome.Refused
                && Directory.EnumerateFiles(folder).Count() == 2, "A tag that is no figure's is not saved.");
    }
}
