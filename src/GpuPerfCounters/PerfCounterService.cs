//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
//
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading;

namespace GpuPerfCounters
{
    using GpuCounter = Tuple<GpuDevice, PerformanceCounter, Func<GpuDevice, int>>;
    using TotalGpuCounter = Tuple<PerformanceCounter, Func<GpuDevice, int>>;

    public class PerfCounterService : ServiceBase
    {
        internal const string CategoryName = "GPU";

        private const string GpuFanSpeed = "% GPU Fan Speed";
        private const string GpuFanSpeedBase = "% GPU Fan Speed-Base";
        private const string GpuFanSpeedHelp = "The intended operating speed of the device's fan, as a percent. Not applicable for passively-cooled GPUs";

        private const string GpuTime = "% GPU Time";
        private const string GpuTimeBase = "% GPU Time-Base";
        private const string GpuTimeHelp = "Percent of time over the past sample period during which one or more kernels was executing on the GPU";

        private const string GpuMemory = "% GPU Memory Reads/Writes";
        private const string GpuMemoryBase = "% GPU Memory Reads/Writes-Base";
        private const string GpuMemoryHelp = "Percent of time over the past sample period during which global (device) memory was being read or written";

        private const string GpuMemoryUsedPct = "% GPU Memory Used";
        private const string GpuMemoryUsedPctBase = "% GPU Memory Used-Base";
        private const string GpuMemoryUsedPctHelp = "Percent of used GPU memory";

        private const string GpuMemoryTotal = "GPU Memory Total (MiB)";
        private const string GpuMemoryTotalHelp = "Total installed memory (in MiBs)";
        private const string GpuMemoryUsed = "GPU Memory Used (MiB)";
        private const string GpuMemoryUsedHelp = "Allocated memory (in MiBs). Note that the driver/GPU always sets aside a small amount of memory for bookkeeping";

        private const string GpuPower = "GPU Power Usage (Watts)";
        private const string GpuPowerHelp = "Power usage for this GPU in Watts and its associated circuitry (e.g. memory)";

        private const string GpuSMClock = "GPU SM Clock (MHz)";
        private const string GpuSMClockHelp = "The current SM clock speed for the device, in MHz";

        private const string GpuTemperature = "GPU Temperature (in degrees C)";
        private const string GpuTemperatureHelp = "The current temperature readings for the device, in degrees C";

        private const string Total = "_Total";

        private List<GpuCounter> _counters = new List<GpuCounter>();
        private List<PerformanceCounter> _baseCounters = new List<PerformanceCounter>();

        private List<TotalGpuCounter> _totalCounters = new List<TotalGpuCounter>();

        private GpuDevice[] _devices;

        private Thread _counterThread;

        private CancellationTokenSource _cancel;

        private readonly int _updateIntervalMsec;
        private readonly bool _usePcieIdInDeviceName;

        public PerfCounterService()
        {
            ServiceName = "GpuPerfCounters";
            CanStop = true;
            CanPauseAndContinue = true;
            AutoLog = true;
            if (!int.TryParse(ConfigurationManager.AppSettings["updateIntervalMsec"], out _updateIntervalMsec))
                _updateIntervalMsec = 1000;
            if (!bool.TryParse(ConfigurationManager.AppSettings["usePcieIdInDeviceName"], out _usePcieIdInDeviceName))
                _usePcieIdInDeviceName = false;
        }

        protected override void OnStart(string[] args)
        {
            if (!PerformanceCounterCategory.Exists(CategoryName))
            {
                var ctrs = new CounterCreationDataCollection
                {
                    new CounterCreationData(GpuFanSpeed, GpuFanSpeedHelp, PerformanceCounterType.RawFraction),
                    new CounterCreationData(GpuFanSpeedBase, "", PerformanceCounterType.RawBase),

                    new CounterCreationData(GpuTime, GpuTimeHelp, PerformanceCounterType.RawFraction),
                    new CounterCreationData(GpuTimeBase, "", PerformanceCounterType.RawBase),

                    new CounterCreationData(GpuMemory, GpuMemoryHelp, PerformanceCounterType.RawFraction),
                    new CounterCreationData(GpuMemoryBase, "", PerformanceCounterType.RawBase),

                    new CounterCreationData(GpuMemoryUsedPct, GpuMemoryUsedPctHelp, PerformanceCounterType.RawFraction),
                    new CounterCreationData(GpuMemoryUsedPctBase, "", PerformanceCounterType.RawBase),

                    new CounterCreationData(GpuMemoryTotal, GpuMemoryTotalHelp, PerformanceCounterType.NumberOfItems32),

                    new CounterCreationData(GpuMemoryUsed, GpuMemoryUsedHelp, PerformanceCounterType.NumberOfItems32),

                    new CounterCreationData(GpuPower, GpuPowerHelp, PerformanceCounterType.NumberOfItems32),

                    new CounterCreationData(GpuSMClock, GpuSMClockHelp, PerformanceCounterType.NumberOfItems32),

                    new CounterCreationData(GpuTemperature, GpuTemperatureHelp, PerformanceCounterType.NumberOfItems32),
                };
                PerformanceCounterCategory.Create(CategoryName, "GPU Counters",
                    PerformanceCounterCategoryType.MultiInstance, ctrs);
            }

            // Order devices by name and use index in the ordered array as id.
            // The reason is multiple devices of the same type might have different PCIe ids
            // which creates redundant device names in case PCIe ids are used in instance names.
            _devices = GpuDevice.EnumDevices().OrderBy(d => d.Name).ToArray();
            int id = 0;
            foreach (var d in _devices)
            {
                string instName = _usePcieIdInDeviceName ? d.ToString() : string.Format("{0}({1})", d.Name, id);

                d.UpdateCounters();

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuFanSpeed, instName, false), dd => dd.FanSpeed));
                _baseCounters.Add(new PerformanceCounter(CategoryName, GpuFanSpeedBase, instName, false) { RawValue = 100 });

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuTime, instName, false), dd => dd.Utilization));
                _baseCounters.Add(new PerformanceCounter(CategoryName, GpuTimeBase, instName, false) { RawValue = 100 });

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuMemory, instName, false), dd => dd.MemoryUtilization));
                _baseCounters.Add(new PerformanceCounter(CategoryName, GpuMemoryBase, instName, false) { RawValue = 100 });

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuMemoryUsedPct, instName, false), dd => dd.MemoryUsedMiB));
                _baseCounters.Add(new PerformanceCounter(CategoryName, GpuMemoryUsedPctBase, instName, false) { RawValue = d.MemoryTotalMiB });

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuMemoryTotal, instName, false), dd => dd.MemoryTotalMiB));
                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuMemoryUsed, instName, false), dd => dd.MemoryUsedMiB));

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuPower, instName, false), dd => dd.PowerW));

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuSMClock, instName, false), dd => dd.SMClockMhz));

                _counters.Add(new GpuCounter(d,
                    new PerformanceCounter(CategoryName, GpuTemperature, instName, false), dd => dd.TemperatureC));
                
                id++;
            }

            int baseRaw = 100 * _devices.Length;

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuFanSpeed, Total, false), d => d.FanSpeed));
            _baseCounters.Add(new PerformanceCounter(CategoryName, GpuFanSpeedBase, Total, false) { RawValue = baseRaw });

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuTime, Total, false), d => d.Utilization));
            _baseCounters.Add(new PerformanceCounter(CategoryName, GpuTimeBase, Total, false) { RawValue = baseRaw });

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuMemory, Total, false), d => d.MemoryUtilization));
            _baseCounters.Add(new PerformanceCounter(CategoryName, GpuMemoryBase, Total, false) { RawValue = baseRaw });

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuMemoryUsedPct, Total, false), d => d.MemoryUsedMiB));
            _baseCounters.Add(new PerformanceCounter(CategoryName, GpuMemoryUsedPctBase, Total, false) { RawValue = _devices.Sum(d => d.MemoryTotalMiB) });

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuMemoryTotal, Total, false), d => d.MemoryTotalMiB));
            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuMemoryUsed, Total, false), d => d.MemoryUsedMiB));

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuPower, Total, false), d => d.PowerW));

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuSMClock, Total, false), d => d.SMClockMhz));

            _totalCounters.Add(new TotalGpuCounter(new PerformanceCounter(CategoryName, GpuTemperature, Total, false), d => d.TemperatureC));

            _counterThread = new Thread(UpdateCounters)
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = "CounterUpdateThread"
            };
            _cancel = new CancellationTokenSource();
            _counterThread.Start();
        }

        protected override void OnContinue()
        {
            base.OnContinue();
        }

        protected override void OnStop()
        {
            _cancel.Cancel();
            _counterThread.Join();
            _cancel.Dispose();

            Nvml.nvmlShutdown();
        }

        private void UpdateCounters(object p)
        {
            while (!_cancel.Token.IsCancellationRequested)
            {
                foreach (var d in _devices)
                    d.UpdateCounters();

                foreach (var ctr in _counters)
                    ctr.Item2.RawValue = ctr.Item3(ctr.Item1);

                foreach (var ctr in _totalCounters)
                    ctr.Item1.RawValue = _devices.Sum(d => ctr.Item2(d));

                // REVIEW alexeyk: move to .config file.
                bool cancelled = _cancel.Token.WaitHandle.WaitOne(_updateIntervalMsec);
                if (cancelled)
                    break;
            }
        }
    }
}
