namespace GHelper.Fan
{
    public readonly struct CpuFanView
    {
        public bool Attempted { get; init; }
        public bool Valid { get; init; }
        public string Text { get; init; }
        public bool ShowRpmUnit { get; init; }
        public int Filtered { get; init; }
        public int Scaled { get; init; }

        public static CpuFanView NotRead => new() { Text = "" };
    }

    public readonly struct HeldSample
    {
        public bool Available { get; init; }
        public string Source { get; init; }
        public long MonoMs { get; init; }
        public string Value { get; init; }

        public static HeldSample Missing => new()
        {
            Source = "unavailable",
            Value = "unavailable"
        };
    }

    public sealed class CpuFanTrace
    {
        public const int HeartbeatMs = 60000;
        public const string Scale = "low16_x100";

        public static readonly CpuFanTrace Shared = new();

        public Action<string> Write { get; set; } = Logger.WriteLine;

        bool emitted;
        long lastEmitMs = long.MinValue;
        string signature = "";

        public static bool TransportAccepted(DecodedRead read)
        {
            return read.IoctlSucceeded && read.BytesReturned >= 4 && read.Buffer != null && read.Buffer.Length >= 4;
        }

        public static string RawHex(DecodedRead read)
        {
            if (read.Buffer == null || read.BytesReturned == 0)
                return "-";
            int count = (int)Math.Min(read.BytesReturned, (uint)Math.Min(read.Buffer.Length, 16));
            if (count <= 0)
                return "-";
            return BitConverter.ToString(read.Buffer, 0, count);
        }

        public static string DecodedText(DecodedRead read)
        {
            if (read.BytesReturned < 4 || read.Buffer == null || read.Buffer.Length < 4)
                return "-";
            return read.Decoded.ToString();
        }

        public static CpuFanView Interpret(DecodedRead read)
        {
            if (!TransportAccepted(read))
                return Unavailable();

            int decoded = read.Decoded;
            int fan = decoded & 0xFFFF;
            if (fan > 120 || (fan == 0 && decoded < 0))
                return Unavailable();

            int scaled = fan * 100;
            return new CpuFanView
            {
                Attempted = true,
                Valid = true,
                Text = scaled.ToString(),
                ShowRpmUnit = true,
                Filtered = fan,
                Scaled = scaled
            };
        }

        public bool Observe(DecodedRead read, DateTimeOffset utc, long monoMs, HeldSample temperature, HeldSample power, out string line)
        {
            CpuFanView view = Interpret(read);
            string next = TransportAccepted(read) + "|" + read.BytesReturned + "|" + read.Win32Error + "|"
                + RawHex(read) + "|" + DecodedText(read) + "|" + view.Text;
            string kind;
            if (!emitted)
                kind = "first";
            else if (next != signature)
                kind = "change";
            else if (monoMs - lastEmitMs >= HeartbeatMs)
                kind = "heartbeat";
            else
            {
                line = "";
                return false;
            }

            emitted = true;
            signature = next;
            lastEmitMs = monoMs;
            line = "CPUFAN kind=" + kind
                + " utc=" + utc.ToString("O")
                + " mono_ms=" + monoMs
                + " ioctl=" + (read.IoctlSucceeded ? "ok" : "fail")
                + " bytes=" + read.BytesReturned
                + " win32=" + read.Win32Error
                + " raw=" + RawHex(read)
                + " decoded=" + DecodedText(read)
                + " displayed=" + view.Text
                + " scale=" + Scale
                + " " + FormatHeld("temp", temperature, monoMs)
                + " " + FormatHeld("power", power, monoMs);
            Write(line);
            return true;
        }

        public static string FormatHeld(string name, HeldSample sample, long nowMono)
        {
            if (!sample.Available)
                return name + "=unavailable source=unavailable age_ms=unavailable";
            return name + "=" + sample.Value
                + " source=" + sample.Source
                + " age_ms=" + (nowMono - sample.MonoMs);
        }

        static CpuFanView Unavailable()
        {
            return new CpuFanView
            {
                Attempted = true,
                Valid = false,
                Text = "N/A",
                ShowRpmUnit = false
            };
        }
    }
}
