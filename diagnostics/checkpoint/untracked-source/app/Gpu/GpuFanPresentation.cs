namespace GHelper.Gpu
{
    public readonly struct GpuFanPresentation
    {
        public string Text { get; }
        public bool ShowRpmUnit { get; }

        public GpuFanPresentation(string text, bool showRpmUnit)
        {
            Text = text ?? "";
            ShowRpmUnit = showRpmUnit && Text.Length > 0;
        }

        // The eGPU row must not reuse the internal GPU-fan endpoint.
        public static GpuFanPresentation ForDisplayedGpu(bool displayedGpuIsXgMobile, int? internalEndpointReading)
        {
            if (displayedGpuIsXgMobile)
                return new GpuFanPresentation("N/A", false);

            return Reading(internalEndpointReading);
        }

        // A separate internal reading is shown only when the caller can name that endpoint.
        public static GpuFanPresentation InternalFan(bool identitySupported, int? reading)
        {
            if (!identitySupported)
                return new GpuFanPresentation("", false);

            return Reading(reading);
        }

        public static string? ForLabeledSpeed(bool displayedGpuIsXgMobile, string? internalFanText)
        {
            if (displayedGpuIsXgMobile)
                return "N/A";

            return internalFanText;
        }

        static GpuFanPresentation Reading(int? reading)
        {
            if (reading is null || reading < 0)
                return new GpuFanPresentation("", false);

            return new GpuFanPresentation(reading.Value.ToString(), true);
        }
    }
}
