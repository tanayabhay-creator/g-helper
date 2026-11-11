using PawnIO;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public static class NvidiaSmi
{

    public static int GetDefaultMaxGPUPower()
    {
        if (AppConfig.ContainsModel("GU605") || AppConfig.ContainsModel("GA605")) return 125;
        if (AppConfig.ContainsModel("GA403")) return 90;
        if (AppConfig.ContainsModel("FA607")) return 140;
        else return 175;
    }

    public static bool TryReadReportedMaxGpuPower(out int watts)
    {
        watts = 0;
        string output = RunNvidiaSmiCommand("--query-gpu=power.max_limit --format csv,noheader,nounits");
        output = output.Trim().Trim('\n', '\r').Replace(".00","").Replace(",00", "");
        if (!float.TryParse(output, out float floatValue)) return false;
        int intValue = (int)floatValue;
        if (intValue < 50 || intValue > 175) return false;
        watts = intValue;
        return true;
    }

    public static bool TryReadLimitWatts(string field, out double watts)
    {
        watts = 0;
        string output = RunNvidiaSmiCommand("-i 0 --query-gpu=" + field + " --format=csv,noheader,nounits");
        string line = output.Split('\n', '\r')[0].Trim();
        if (line.Length == 0 || line.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return false;
        if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            !double.TryParse(line, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return false;
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return false;
        watts = value;
        return true;
    }

    public static int GetMaxGPUPower()
    {
        if (TryReadReportedMaxGpuPower(out int watts)) return watts;
        return GetDefaultMaxGPUPower();
    }

    private static string RunNvidiaSmiCommand(string arguments = "-i 0 -q")
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception ex)
        {
            //return File.ReadAllText(@"smi.txt");
            Debug.WriteLine(ex.Message);
        }

        return "";

    }
}
