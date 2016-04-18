using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuPerfCounters
{
    internal class GpuDevice
    {
        // See nvmlConstants::NVML_DEVICE_NAME_BUFFER_SIZE in nvml.h
        private const int MaxNameLen = 64;
        // See nvmlConstants::NVML_DEVICE_UUID_BUFFER_SIZE in nvml.h
        private const int MaxUuidLen = 80;

        private readonly IntPtr _device;

        private Nvml.nvmlPciInfo _pci;

        private Nvml.NvmlUtilization _utilization;
        private Nvml.NvmlMemory _memory;
        private uint _fanSpeed;
        private uint _power;
        private uint _smClock;
        private uint _temperature;

        public readonly int DeviceId;
        public readonly string Name;
        public readonly string Uuid;

        public int Utilization { get { return (int)_utilization.gpu; } }

        public int MemoryUtilization { get { return (int)_utilization.memory; } }

        public int MemoryTotalMiB { get { return (int)(_memory.total / (1024 * 1024)); } }

        public int MemoryUsedMiB { get { return (int)(_memory.used / (1024 * 1024)); } }

        public int FanSpeed { get { return (int)_fanSpeed; } }

        public int PowerW { get { return (int)(_power / 1000); } }

        public int SMClockMhz { get { return (int)_smClock; } }

        public int TemperatureC { get { return (int)_temperature; } }

        public GpuDevice(int deviceId)
        {
            DeviceId = deviceId;
            var err = Nvml.nvmlInit_v2();
            if (err == Nvml.nvmlReturn.NVML_SUCCESS)
            {
                err = Nvml.nvmlDeviceGetHandleByIndex((uint)deviceId, out _device);
                if (err == Nvml.nvmlReturn.NVML_SUCCESS)
                {
                    var name = new StringBuilder(MaxNameLen);
                    var uuid = new StringBuilder(MaxUuidLen);
                    if (Nvml.nvmlDeviceGetName(_device, name, (uint)name.Capacity) == Nvml.nvmlReturn.NVML_SUCCESS &&
                        Nvml.nvmlDeviceGetPciInfo(_device, out _pci) == Nvml.nvmlReturn.NVML_SUCCESS &&
                        Nvml.nvmlDeviceGetUUID(_device, uuid, (uint)uuid.Capacity) == Nvml.nvmlReturn.NVML_SUCCESS)
                    {
                        Name = name.ToString();
                        Uuid = uuid.ToString();
                        return;
                    }
                }
            }
            Name = "<FailedToInitialize>";
        }

        public static IEnumerable<GpuDevice> EnumDevices()
        {
            var err = Nvml.nvmlInit_v2();
            if (err != Nvml.nvmlReturn.NVML_SUCCESS)
                yield break;

            int cdev;
            err = Nvml.nvmlDeviceGetCount(out cdev);
            if (err != Nvml.nvmlReturn.NVML_SUCCESS)
                yield break;

            for (int i = 0; i < cdev; i++)
                yield return new GpuDevice(i);
        }

        public void UpdateCounters()
        {
            var err = Nvml.nvmlDeviceGetUtilizationRates(_device, out _utilization);
            _utilization = (err == Nvml.nvmlReturn.NVML_SUCCESS) ? _utilization : default(Nvml.NvmlUtilization);

            err = Nvml.nvmlDeviceGetMemoryInfo(_device, out _memory);
            _memory = (err == Nvml.nvmlReturn.NVML_SUCCESS) ? _memory : default(Nvml.NvmlMemory);

            err = Nvml.nvmlDeviceGetFanSpeed(_device, out _fanSpeed);
            _fanSpeed = (err == Nvml.nvmlReturn.NVML_SUCCESS) ? _fanSpeed : 0;

            err = Nvml.nvmlDeviceGetPowerUsage(_device, out _power);
            _power = (err == Nvml.nvmlReturn.NVML_SUCCESS) ? _power : 0;

            err = Nvml.nvmlDeviceGetTemperature(_device, Nvml.nvmlTemperatureSensors.NVML_TEMPERATURE_GPU, out _temperature);
            _temperature = (err == Nvml.nvmlReturn.NVML_SUCCESS) ? _temperature : 0;

            err = Nvml.nvmlDeviceGetClockInfo(_device, Nvml.nvmlClockType.NVML_CLOCK_SM, out _smClock);
            _smClock = (err == Nvml.nvmlReturn.NVML_SUCCESS) ? _smClock : 0;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}({1:X2}:{2:X2})", Name, _pci.bus, _pci.device);
        }
    }

    internal static class Nvml
    {
        public enum nvmlReturn
        {
            NVML_SUCCESS = 0,                   //!< The operation was successful
            NVML_ERROR_UNINITIALIZED = 1,       //!< NVML was not first initialized with nvmlInit()
            NVML_ERROR_INVALID_ARGUMENT = 2,    //!< A supplied argument is invalid
            NVML_ERROR_NOT_SUPPORTED = 3,       //!< The requested operation is not available on target device
            NVML_ERROR_NO_PERMISSION = 4,       //!< The current user does not have permission for operation
            NVML_ERROR_ALREADY_INITIALIZED = 5, //!< Deprecated: Multiple initializations are now allowed through ref counting
            NVML_ERROR_NOT_FOUND = 6,           //!< A query to find an object was unsuccessful
            NVML_ERROR_INSUFFICIENT_SIZE = 7,   //!< An input argument is not large enough
            NVML_ERROR_INSUFFICIENT_POWER = 8,  //!< A device's external power cables are not properly attached
            NVML_ERROR_DRIVER_NOT_LOADED = 9,   //!< NVIDIA driver is not loaded
            NVML_ERROR_TIMEOUT = 10,            //!< User provided timeout passed
            NVML_ERROR_IRQ_ISSUE = 11,          //!< NVIDIA Kernel detected an interrupt issue with a GPU
            NVML_ERROR_LIBRARY_NOT_FOUND = 12,  //!< NVML Shared Library couldn't be found or loaded
            NVML_ERROR_FUNCTION_NOT_FOUND = 13, //!< Local version of NVML doesn't implement this function
            NVML_ERROR_CORRUPTED_INFOROM = 14,  //!< infoROM is corrupted
            NVML_ERROR_GPU_IS_LOST = 15,        //!< The GPU has fallen off the bus or has otherwise become inaccessible
            NVML_ERROR_UNKNOWN = 999            //!< An internal driver error occurred
        };

        public enum nvmlTemperatureSensors
        {
            NVML_TEMPERATURE_GPU = 0
        }

        internal enum nvmlClockType
        {
            NVML_CLOCK_GRAPHICS = 0,
            NVML_CLOCK_SM = 1,
            NVML_CLOCK_MEM = 2,
        }

        private const string NvmlDll = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll";

        [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2", ExactSpelling = true)]
        public static extern nvmlReturn nvmlInit_v2();

        [DllImport(NvmlDll, EntryPoint = "nvmlShutdown", ExactSpelling = true)]
        public static extern nvmlReturn nvmlShutdown();

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetCount", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetCount([Out] out int deviceCount);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetHandleByIndex([In] uint index,
            [Out] out IntPtr device);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetName", ExactSpelling = true, CharSet = CharSet.Ansi)]
        public static extern nvmlReturn nvmlDeviceGetName([In] IntPtr device, StringBuilder name, [In] uint length);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetUUID", ExactSpelling = true, CharSet = CharSet.Ansi)]
        public static extern nvmlReturn nvmlDeviceGetUUID([In] IntPtr device, StringBuilder name, [In] uint length);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPciInfo_v2", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetPciInfo([In] IntPtr device, [Out] out nvmlPciInfo pci);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetUtilizationRates", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetUtilizationRates([In] IntPtr device, [Out] out NvmlUtilization utilization);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetMemoryInfo([In] IntPtr device, [Out] out NvmlMemory memory);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetTemperature([In] IntPtr device, [In] nvmlTemperatureSensors sensorType, [Out] out uint temp);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetClockInfo", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetClockInfo([In] IntPtr device, [In] nvmlClockType type, [Out] out uint clock);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetPowerUsage([In] IntPtr device, [Out] out uint power);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetFanSpeed", ExactSpelling = true)]
        public static extern nvmlReturn nvmlDeviceGetFanSpeed([In] IntPtr device, [Out] out uint speed);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct nvmlPciInfo
        {
            private const int NVML_DEVICE_PCI_BUS_ID_BUFFER_SIZE = 16;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NVML_DEVICE_PCI_BUS_ID_BUFFER_SIZE)]
            public string busId;
            public uint domain;
            public uint bus;
            public uint device;
            public uint pciDeviceId;

            public uint pciSubSystemId;

            private uint reserved0;
            private uint reserved1;
            private uint reserved2;
            private uint reserved3;
        } ;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlUtilization
        {
            public uint gpu;
            public uint memory;
        };

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlMemory
        {
            public ulong total;
            public ulong free;
            public ulong used;
        };
    }
}
