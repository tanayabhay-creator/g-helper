public static class NvidiaPolicyTarget
{
    public static bool TryTargetPcm(int requestedWatts, double referenceWatts, double referencePercent, uint minPcm, uint maxPcm, out uint pcm)
    {
        pcm = 0;
        if (requestedWatts <= 0 || referenceWatts <= 0 || referencePercent <= 0 || maxPcm < minPcm) return false;
        double raw = requestedWatts / referenceWatts * referencePercent * 1000.0;
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0 || raw > uint.MaxValue) return false;
        uint target = (uint)Math.Round(raw);
        if (target < minPcm || target > maxPcm) return false;
        pcm = target;
        return true;
    }

    public static double WattsForPcm(uint pcm, double referenceWatts, double referencePercent)
    {
        if (referencePercent <= 0) return 0;
        return pcm / 1000.0 / referencePercent * referenceWatts;
    }
}
