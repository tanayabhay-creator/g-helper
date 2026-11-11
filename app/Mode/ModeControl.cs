using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using GHelper.USB;
using PawnIO;

namespace GHelper.Mode
{
    public class ModeControl
    {

        static SettingsForm settings = Program.settingsForm;

        private static bool customFans = false;
        private static int customPower = 0;

        private int _cpuUV = 0;
        private int _igpuUV = 0;
        private int _cpuTemp = CpuInfo.DefaultTemp;
        private bool _ryzenPower = false;

        private static RyzenSmuService? _smu;
        private static readonly object _smuLock = new();

        private static RyzenSmuService? GetSmu()
        {
            lock (_smuLock)
            {
                if (_smu != null && _smu.IsInitialized) return _smu;
                _smu?.Dispose();
                _smu = new RyzenSmuService();
                if (!_smu.Initialize(System.Reflection.Assembly.GetExecutingAssembly()))
                {
                    _smu.Dispose();
                    _smu = null;
                }
                else
                {
                    Logger.WriteLine($"SMU Init: {_smu.CpuCodeName} ({_smu.Family}), SMU v{_smu.SmuVersion >> 16}.{(_smu.SmuVersion >> 8) & 0xFF}.{_smu.SmuVersion & 0xFF}");
                }
                return _smu;
            }
        }

        public static bool IsPawnAvailable()  => GetSmu() != null;
        public static bool IsPawnInstalled()   => RyzenSmuService.IsPawnInstalled();

        static System.Timers.Timer? reapplyTimer;
        static System.Timers.Timer modeToggleTimer = default!;
        static CancellationTokenSource _modeCts = new();
        static Task _modeTask = Task.CompletedTask;

        public ModeControl()
        {
            int reapplyTime = AppConfig.Get("reapply_time", IsReapplyTempRequired() ? 30 : 0);
            if (reapplyTime > 0)
            {
                reapplyTimer = new System.Timers.Timer(reapplyTime * 1000);
                reapplyTimer.Elapsed += ReapplyTimer_Elapsed;
            }
        }

        // Cezanne/Rembrandt (Renoir) + Phoenix/HawkPoint (Mobile) silently reset temp limit under load.
        private static bool IsReapplyTempRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Renoir or CpuFamily.Mobile;
        }

        private static bool IsReapplyRyzenRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Raphael;
        }

        private static void SetReapplyEnabled(bool enabled)
        {
            if (reapplyTimer != null) reapplyTimer.Enabled = enabled;
        }


        private void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            SetCPUTemp(AppConfig.GetMode("cpu_temp"));
            SetRyzenPower();
        }

        public void WaitForApply()
        {
            try { _modeTask.Wait(5000); } catch { }
        }

        public void AutoPerformance(bool powerChanged = false)
        {
            int mode = AppConfig.Get("performance_" + Program.PerformanceKey());
            Logger.WriteLine($"{Program.currentSource} Performance Mode: {Modes.GetName(mode == -1 ? Modes.GetCurrent() : mode)}");

            if (mode != -1)
                SetPerformanceMode(mode, powerChanged);
            else
                SetPerformanceMode(Modes.GetCurrent());
        }


        public void ResetPerformanceMode()
        {
            ResetRyzen();

            int status = Program.acpi.SetPerformanceMode(Modes.GetCurrentBase());
            if (status == XgMitigation.Suppressed) return;

            // Default power mode
            AppConfig.RemoveMode("powermode");
            PowerNative.SetPowerMode(Modes.GetCurrentBase());
        }

        public void Toast()
        {
            Program.toast.RunToast(Modes.GetCurrentName(), SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? ToastIcon.Charger : ToastIcon.Battery);
        }

        public bool SetPerformanceMode(int mode = -1, bool notify = false)
        {
            using (var lease = XgMitigation.Shared.Enter("SetPerformanceMode", "DEVS", XgOp.Profile))
            {
                if (!lease.Allowed)
                {
                    settings.ShowMode(Modes.GetCurrent());
                    return false;
                }
                if (!lease.Enforced && Program.acpi.IsXGActive())
                {
                    Logger.WriteLine("XG Mobile is active. Performance profile was not written.");
                    settings.ShowMode(Modes.GetCurrent());
                    return false;
                }
            }

            int oldMode = Modes.GetCurrent();
            if (mode < 0) mode = oldMode;

            if (!Modes.Exists(mode)) mode = 0;

            settings.ShowMode(mode);

            Modes.SetCurrent(mode);


            _modeCts.Cancel();
            _modeCts = new CancellationTokenSource();
            var ct = _modeCts.Token;

            _modeTask = Task.Run(async () =>
            {
                try
                {
                    bool reset = AppConfig.IsResetRequired() && (Modes.GetBase(oldMode) == Modes.GetBase(mode)) && customPower > 0 && !AppConfig.IsApplyPower();

                    customFans = false;
                    customPower = 0;

                    SetModeLabel();

                    // Workaround for not properly resetting limits on G14 2024
                    if (reset)
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, (Modes.GetBase(oldMode) != 1) ? AsusACPI.PerformanceTurbo : AsusACPI.PerformanceBalanced, "ModeReset");
                        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                    }

                    ct.ThrowIfCancellationRequested();

                    if (AppConfig.Is("status_mode")) Program.acpi.DeviceSet(AsusACPI.StatusMode, [0x00, Modes.GetBase(mode) == AsusACPI.PerformanceSilent ? (byte)0x02 : (byte)0x03], "StatusMode");
                    Program.acpi.SetPerformanceMode(AppConfig.IsManualModeRequired() ? AsusACPI.PerformanceManual : Modes.GetBase(mode));

                    SetGPUClocks();

                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoFans();
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoPower();
                    
                    var command = AppConfig.GetModeString("mode_command");
                    if (command is not null)
                    {   Logger.WriteLine("Running mode command: " + command);
                        RestrictedProcessHelper.RunAsRestrictedUser(command);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.WriteLine($"SetPerformanceMode cancelled (mode {mode})");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"SetPerformanceMode failed (mode {mode}): {ex.Message}");
                }
            }, ct);

            if (notify) Toast();

            if (!AppConfig.Is("skip_powermode"))
            {
                // Windows power mode
                if (AppConfig.GetModeString("powermode") is not null)
                    PowerNative.SetPowerMode(AppConfig.GetModeString("powermode"));
                else
                    PowerNative.SetPowerMode(Modes.GetBase(mode));

                if (AppConfig.IsAutoASPM()) PowerNative.SetBalancedASPM();
                if (AppConfig.IsAutoStandbyNetworking()) PowerNative.SetConnectivityInStandby();
            }

            // CPU Boost setting override
            if (AppConfig.GetMode("auto_boost") != -1)
                    PowerNative.SetCPUBoost(AppConfig.GetMode("auto_boost"));

            settings.FansInit();
            return true;
        }


        private void ModeToggleTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            modeToggleTimer.Stop();
            Logger.WriteLine($"Hotkey mode: {Modes.GetCurrent()}");
            using (XgMitigation.Explicit(XgMitigation.ProfileApply))
                SetPerformanceMode();

        }

        public void CyclePerformanceMode(bool back = false)
        {
            int delay = AppConfig.Get("mode_delay", 1000);

            if (modeToggleTimer is null)
            {
                modeToggleTimer = new System.Timers.Timer(delay);
                modeToggleTimer.Elapsed += ModeToggleTimer_Elapsed;
            }

            modeToggleTimer.Stop();
            modeToggleTimer.Start();
            Modes.SetCurrent(Modes.GetNext(back));
            Toast();
        }

        public void AutoFans(bool force = false)
        {
            customFans = false;

            if (AppConfig.IsApplyFans() || force)
            {

                bool xgmFan = false;
                bool xgmActive = Program.acpi.IsXGActive();
                if (xgmActive || AppConfig.Is("xgm_fan"))
                {
                    byte[] xgmCurve = AppConfig.GetFanConfig(AsusFan.XGM);
                    if (AppConfig.Is("xgm_fan") && !AsusACPI.IsInvalidCurve(xgmCurve))
                        XGM.SetFan(xgmCurve);
                    else if (xgmActive)
                        XGM.Reset();

                    if (!xgmActive)
                        xgmFan = Program.acpi.IsXGConnected();
                }

                using var fanLease = XgMitigation.Shared.Enter("AutoFans", "DEVS", XgOp.Fan);
                if (fanLease.Allowed)
                {
                int cpuResult = Program.acpi.SetFanCurve(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
                int gpuResult = xgmActive ? 1 : Program.acpi.SetFanCurve(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));

                if (AppConfig.Is("mid_fan"))
                    Program.acpi.SetFanCurve(AsusFan.Mid, AppConfig.GetFanConfig(AsusFan.Mid));


                // Alternative way to set fan curve
                if (XgMitigation.FanFallbackIsHardwareRejection(cpuResult, gpuResult))
                {
                    cpuResult = Program.acpi.SetFanRange(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
                    gpuResult = xgmActive ? 1 : Program.acpi.SetFanRange(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));

                    // Something went wrong, resetting to default profile
                    if (XgMitigation.FanFallbackIsHardwareRejection(cpuResult, gpuResult))
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Reset Mode");
                        settings.LabelFansResult("Model doesn't support custom fan curves");
                    }
                }
                else if (cpuResult == 1 && gpuResult == 1)
                {
                    settings.LabelFansResult("");
                    customFans = true;
                }

                int hystUp = AppConfig.GetMode("hysteresis_up");
                int hystDown = AppConfig.GetMode("hysteresis_down");
                if (hystUp > 0 && hystDown > 0)
                    Program.acpi.SetFanHysteresis(hystUp, hystDown);

                // force set PPTs for missbehaving bios on FX507/517 series
                if (!xgmActive && (AppConfig.IsPowerRequired() || xgmFan) && !AppConfig.IsApplyPower())
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, 80, "PowerLimit Fix A0");
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, 80, "PowerLimit Fix A3");
                    });
                }
                }

            } else
            {
                XGM.Reset();
            }

            SetModeLabel();

        }

        public void AutoPower(bool launchAsAdmin = false)
        {
            bool applyPower = AppConfig.IsApplyPower();
            bool applyFans = AppConfig.IsApplyFans();

            if (applyPower && !applyFans && AppConfig.IsFanRequired())
            {
                AutoFans(true);
                Thread.Sleep(500);
            }

            if (applyPower) SetPower(launchAsAdmin);

            Thread.Sleep(500);
            if (AppConfig.IsApplyGpuPower()) SetGPUPower();
            if (applyPower) SetCrossPower();
            AutoRyzen();

            if (IsReapplyRyzenRequired())
                Task.Delay(5000).ContinueWith(_ => { AutoRyzen(); ReadRyzenLimits(); });

        }

        public void SetModeLabel()
        {
            settings.SetModeLabel(Properties.Strings.PerformanceMode + ": " + Modes.GetCurrentName() + (customFans ? "+" : "") + ((customPower > 0) ? " " + customPower + "W" : ""));
        }

        public void SetRyzenPower(bool init = false)
        {
            if (init) _ryzenPower = true;

            if (!_ryzenPower) return;
            if (!AppConfig.IsApplyPower()) return;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
            int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            using var ryzenLease = XgMitigation.Shared.Enter("SetRyzenPower", "SMU.SetAllLimits", XgOp.Power);
            if (!ryzenLease.Allowed) return;
            if (!ryzenLease.Enforced && Program.acpi.IsXGActive()) return;

            var smu = GetSmu();
            if (smu == null) return;

            smu.SetAllLimits(limit_total, limit_fast, limit_slow,
                out SmuStatus stapm, out SmuStatus fast, out SmuStatus slow);
            if (ryzenLease.Enforced)
                XgMitigation.Shared.LogCompleted(ryzenLease, "SetRyzenPower", "SMU.SetAllLimits", $"{limit_total}/{limit_fast}/{limit_slow}", $"{stapm}/{fast}/{slow}");
            if (init) Logger.WriteLine($"STAPM: {limit_total}W {stapm} | SLOW: {limit_slow}W {slow} | FAST: {limit_fast}W {fast}");
        }

        public void SetPower(bool launchAsAdmin = false)
        {
            if (!XgMitigation.Shared.Enabled && Program.acpi.IsXGActive())
            {
                Logger.WriteLine("XG Mobile is active. CPU power limit was not written.");
                return;
            }

            bool allAMD = Program.acpi.IsAllAmdPPT();
            bool isAMD = CpuInfo.IsAMD;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_cpu = AppConfig.GetMode("limit_cpu");
            int limit_slow = AppConfig.GetMode("limit_slow");
            int limit_fast = AppConfig.GetMode("limit_fast");

            if (limit_slow < 0 || allAMD) limit_slow = limit_total;

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            if (limit_cpu > AsusACPI.MaxCPU) return;
            if (limit_cpu < AsusACPI.MinCPU) return;

            if (limit_fast > AsusACPI.MaxTotal) return;
            if (limit_fast < AsusACPI.MinTotal) return;

            if (limit_slow > AsusACPI.MaxTotal) return;
            if (limit_slow < AsusACPI.MinTotal) return;

            // SPL and SPPT. A3, A0, and the optional B0/C1 write share one admission.
            if (Program.acpi.IsSupported(AsusACPI.PPT_APUA0))
            {
                bool writeFast = !allAMD && isAMD && Program.acpi.IsSupported(AsusACPI.PPT_APUC1);
                int written = XgMitigation.Shared.ApplyCpuPowerWrites(
                    limit_total, limit_slow, allAMD, limit_cpu, writeFast, limit_fast,
                    (id, value, name) => Program.acpi.DeviceSet(id, value, name));
                if (written == XgMitigation.Suppressed) return;
                customPower = allAMD ? limit_cpu : limit_total;
            }
            else if (isAMD)
            {
                if (ProcessHelper.IsUserAdministrator())
                {
                    SetRyzenPower(true);
                }
                else if (launchAsAdmin)
                {
                    ProcessHelper.RunAsAdmin("cpu");
                    return;
                }

                if (allAMD)
                {
                    int cpuResult = Program.acpi.DeviceSet(AsusACPI.PPT_CPUB0, limit_cpu, "PowerLimit B0");
                    if (cpuResult != XgMitigation.Suppressed) customPower = limit_cpu;
                }
                else if (Program.acpi.IsSupported(AsusACPI.PPT_APUC1))
                {
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUC1, limit_fast, "PowerLimit C1");
                }
            }

            SetModeLabel();

        }

        public void SetCrossPower()
        {
            if (!XgMitigation.Shared.Enabled && Program.acpi.IsXGActive())
            {
                Logger.WriteLine("XG Mobile is active. CPU power limit was not written.");
                return;
            }

            int gpucpu = AppConfig.GetMode("limit_gpucpu");
            int crossload = AppConfig.GetMode("limit_crossload");
            int cputemp = AppConfig.GetMode("limit_cputemp");

            if (gpucpu >= AsusACPI.MinGPUtoCPU && gpucpu <= AsusACPI.MaxGPUtoCPU && Program.acpi.IsSupported(AsusACPI.PPT_GPUCPU9C))
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUCPU9C, gpucpu, "PowerLimit 9C (GPU to CPU)");

            if (crossload >= AsusACPI.MinCrossLoad && crossload <= AsusACPI.MaxCrossLoad && Program.acpi.IsSupported(AsusACPI.PPT_CROSS9F))
                Program.acpi.DeviceSet(AsusACPI.PPT_CROSS9F, crossload, "PowerLimit 9F (Cross Loading)");

            if (cputemp >= AsusACPI.MinCPUTemp && cputemp <= AsusACPI.MaxCPUTemp && Program.acpi.IsSupported(AsusACPI.PPT_TEMP9E))
                Program.acpi.DeviceSet(AsusACPI.PPT_TEMP9E, cputemp, "PowerLimit 9E (CPU Temp)");
        }

        public void SetGPUClocks(bool launchAsAdmin = true, bool reset = false)
        {
            bool enforced = XgMitigation.Shared.Enabled;
            if (!enforced && Program.acpi.IsXGActive())
            {
                Logger.WriteLine("XG Mobile is active. GPU clocks were not written.");
                return;
            }

            Task.Run(() =>
            {
                using var clockLease = enforced
                    ? XgMitigation.Shared.Enter("SetGPUClocks", "NvAPI", XgOp.Clocks)
                    : XgMitigation.Lease.Bypass();
                if (!clockLease.Allowed) return;

                int core = AppConfig.GetMode("gpu_core");
                int memory = AppConfig.GetMode("gpu_memory");
                int clock_limit = AppConfig.GetMode("gpu_clock_limit");

                if (reset) core = memory = clock_limit = 0;

                if (core == -1 && memory == -1 && clock_limit == -1) return;
                //if ((gpu_core > -5 && gpu_core < 5) && (gpu_memory > -5 && gpu_memory < 5)) launchAsAdmin = false;

                if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) { Logger.WriteLine("Clocks: Eco"); return; }
                if (HardwareControl.GpuControl is null) { Logger.WriteLine("Clocks: NoGPUControl"); return; }
                if (!HardwareControl.GpuControl!.IsNvidia) { Logger.WriteLine("Clocks: NotNvidia"); return; }

                NvidiaGpuControl nvControl = (NvidiaGpuControl)HardwareControl.GpuControl;
                try
                {
                    int statusClocks = nvControl.SetClocks(core, memory);
                    int statusLimit = nvControl.SetMaxGPUClock(clock_limit);
                    if (enforced)
                        XgMitigation.Shared.LogCompleted(clockLease, "SetGPUClocks", "NvAPI", "clocks", $"clocks={statusClocks} limit={statusLimit}");
                    if ((statusLimit != 0 || statusClocks != 0) && launchAsAdmin) ProcessHelper.RunAsAdmin("gpu");
                }
                catch (Exception ex)
                {
                    if (enforced)
                        XgMitigation.Shared.LogFailed(clockLease, "SetGPUClocks", "NvAPI", "clocks", ex.GetType().Name);
                    Logger.WriteLine("Clocks Error:" + ex.ToString());
                }

                settings.GPUInit();
            });
        }

        public XgMitigation.GpuLimitApplyResult SetGPUPower()
        {
            if (!XgMitigation.Shared.Enabled && Program.acpi.IsXGActive())
            {
                Logger.WriteLine("XG Mobile is active. GPU power and temperature were not written.");
                return new XgMitigation.GpuLimitApplyResult(true, 0) { PowerDenied = true, TempDenied = true };
            }

            int gpu_boost = AppConfig.GetMode("gpu_boost");
            int gpu_temp = AppConfig.GetMode("gpu_temp");
            int gpu_power = AppConfig.GetMode("gpu_power");
            // A fresh read. IsSupported keeps the first miss for the process, including a read from before this endpoint answers.
            bool writePower = GpuPowerLimit.PowerInRange(gpu_power) && Program.acpi.ReadDecoded(AsusACPI.GPU_POWER).Decoded >= 0;
            bool writeBoost = GpuPowerLimit.BoostInRange(gpu_boost) && Program.acpi.ReadDecoded(AsusACPI.PPT_GPUC0).Decoded >= 0;
            bool writeTemp = GpuPowerLimit.TempInRange(gpu_temp) && Program.acpi.ReadDecoded(AsusACPI.PPT_GPUC2).Decoded >= 0;
            if (GpuPowerLimit.PowerInRange(gpu_power) && !writePower)
                Logger.WriteLine("GPU_POWER did not answer. The power slider was not written.");

            var result = XgMitigation.Shared.ApplySupportedGpuLimits(
                writePower, gpu_power,
                writeBoost, gpu_boost,
                writeTemp, gpu_temp,
                (device, value, name) => Program.acpi.DeviceSet(device, value, name));
            if (result.Suppressed && result.Writes == 0)
                Logger.WriteLine("GPU power and temperature were not written. " + XgMitigation.GpuLimitsLockedText);
            return result;
        }

        public SmuStatus? SetCPUTemp(int cpuTemp, bool log = false)
        {
            if (cpuTemp < CpuInfo.MinTemp || cpuTemp > CpuInfo.DefaultTemp) return null;
            if (cpuTemp == CpuInfo.DefaultTemp && _cpuTemp == CpuInfo.DefaultTemp) return null;

            using var tempLease = XgMitigation.Shared.Enter("SetCPUTemp", "SMU.SetThm", XgOp.Thermal);
            if (!tempLease.Allowed) return null;

            var smu = GetSmu();
            if (smu == null) return null;
            SmuStatus status = smu.SetThm(cpuTemp);
            if (tempLease.Enforced)
                XgMitigation.Shared.LogCompleted(tempLease, "SetCPUTemp", "SMU.SetThm", cpuTemp.ToString(), status.ToString());
            if (log) Logger.WriteLine($"CPU Temp: {cpuTemp}°C {status}");
            if (status == SmuStatus.OK) _cpuTemp = cpuTemp;
            return status;
        }

        public void SetUV(int cpuUV)
        {
            if (!CpuInfo.IsSupportedUV()) return;
            if (cpuUV < CpuInfo.MinCPUUV || cpuUV > CpuInfo.MaxCPUUV) return;

            using var lease = XgMitigation.Shared.Enter("SetUV", "SMU.SetCoAll", XgOp.Uv);
            if (!lease.Allowed) return;

            var smu = GetSmu();
            if (smu == null) return;
            SmuStatus status = smu.SetCoAll(cpuUV);
            if (lease.Enforced)
                XgMitigation.Shared.LogCompleted(lease, "SetUV", "SMU.SetCoAll", cpuUV.ToString(), status.ToString());
            Logger.WriteLine($"UV: {cpuUV} {status}");
            if (status == SmuStatus.OK) _cpuUV = cpuUV;
        }

        public void SetUViGPU(int igpuUV)
        {
            if (!CpuInfo.IsSupportedUViGPU()) return;
            if (igpuUV < CpuInfo.MinIGPUUV || igpuUV > CpuInfo.MaxIGPUUV) return;

            using var lease = XgMitigation.Shared.Enter("SetUViGPU", "SMU.SetCoGfx", XgOp.Uv);
            if (!lease.Allowed) return;

            var smu = GetSmu();
            if (smu == null) return;
            SmuStatus status = smu.SetCoGfx(igpuUV);
            if (lease.Enforced)
                XgMitigation.Shared.LogCompleted(lease, "SetUViGPU", "SMU.SetCoGfx", igpuUV.ToString(), status.ToString());
            Logger.WriteLine($"iGPU UV: {igpuUV} {status}");
            if (status == SmuStatus.OK) _igpuUV = igpuUV;
        }

        public string SetRyzen(bool launchAsAdmin = false)
        {
            if (!ProcessHelper.IsUserAdministrator())
            {
                if (launchAsAdmin) ProcessHelper.RunAsAdmin("uv");
                return string.Empty;
            }

            var lines = new System.Text.StringBuilder();
            int cpuTemp = AppConfig.GetMode("cpu_temp");
            using (var uvLease = XgMitigation.Shared.Enter("SetRyzen", "SMU.SetCo", XgOp.Uv))
            {
            if (uvLease.Allowed)
            {
            var smu = GetSmu();
            if (smu != null)
            {
            try
            {
                int cpuUV   = AppConfig.GetMode("cpu_uv",   0);
                int igpuUV  = AppConfig.GetMode("igpu_uv",  0);
                string? cpuUVCores = AppConfig.GetModeString("cpu_uv_cores");

                if (CpuInfo.IsSupportedUV() && cpuUVCores is not null)
                {
                    int core = 0;
                    foreach (var token in cpuUVCores.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(token, out int uv) && -uv >= CpuInfo.MinCPUUV && -uv <= CpuInfo.MaxCPUUV)
                        {
                            SmuStatus s = smu.SetCoPer(core, -uv);
                            Logger.WriteLine($"UV core {core}: {-uv} {s}");
                            if (s == SmuStatus.OK && uv != 0) _cpuUV = -uv;
                        }
                        core++;
                    }
                    lines.AppendLine($"CPU UV cores {cpuUVCores}");
                }
                else if (CpuInfo.IsSupportedUV() && cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
                {
                    SmuStatus s = smu.SetCoAll(cpuUV);
                    Logger.WriteLine($"UV: {cpuUV} {s}");
                    if (s == SmuStatus.OK) _cpuUV = cpuUV;
                    lines.AppendLine($"CPU UV {cpuUV}: {s}");
                }

                if (CpuInfo.IsSupportedUViGPU() && igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
                {
                    SmuStatus s = smu.SetCoGfx(igpuUV);
                    Logger.WriteLine($"iGPU UV: {igpuUV} {s}");
                    if (s == SmuStatus.OK) _igpuUV = igpuUV;
                    lines.AppendLine($"iGPU UV {igpuUV}: {s}");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("UV Error: " + ex.ToString());
            }

            if (uvLease.Enforced)
                XgMitigation.Shared.LogCompleted(uvLease, "SetRyzen", "SMU.SetCo", "curve", lines.ToString());
            }
            }
            }

            try
            {
                SmuStatus? tempStatus = SetCPUTemp(cpuTemp, true);
                if (tempStatus.HasValue) lines.AppendLine($"CPU Temp {cpuTemp}°C: {tempStatus}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("UV Error: " + ex.ToString());
            }

            SetReapplyEnabled(AppConfig.IsApplyUV());
            return lines.ToString().TrimEnd();
        }

        public string ReadRyzenLimits()
        {
            var smu = GetSmu();
            if (smu == null) return string.Empty;

            try
            {
                PowerLimits? lim = smu.GetPowerLimits();
                if (lim == null) return string.Empty;

                string line = $"SPL: {lim.Stapm:F1}W | sPPT {lim.Slow:F1}W | fPPT {lim.Fast:F1}W";
                if (lim.ApuSlow.HasValue) line += $" | APU {lim.ApuSlow.Value:F1}W";
                line += $", Temp: {lim.TctlTemp:F0}°C";
                Logger.WriteLine("Ryzen Limits: " + line);
                return line;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ReadRyzenLimits Error: " + ex.ToString());
                return string.Empty;
            }
        }

        public void ResetRyzen()
        {
            if (_cpuUV != 0) SetUV(0);
            if (_igpuUV != 0) SetUViGPU(0);
            if (_cpuTemp != CpuInfo.DefaultTemp) SetCPUTemp(CpuInfo.DefaultTemp, true);
            SetReapplyEnabled(false);
        }

        public void AutoRyzen()
        {
            if (!CpuInfo.IsAMD) return;

            if (AppConfig.IsApplyUV()) SetRyzen();
            else ResetRyzen();
        }

        public void AutoCPUTemp()
        {
            if (!CpuInfo.IsAMD) return;
            if (!AppConfig.IsApplyUV()) return;
            if (!ProcessHelper.IsUserAdministrator()) return;

            try
            {
                SetCPUTemp(AppConfig.GetMode("cpu_temp"), true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AutoCPUTemp Error: " + ex.Message);
            }
        }

        public void ShutdownReset()
        {
            if (!AppConfig.IsShutdownReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode,AsusACPI.PerformanceBalanced, "Mode Reset");
        }

        public void SleepReset()
        {
            if (!AppConfig.IsSleepReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Sleep Reset");
        }

    }
}
