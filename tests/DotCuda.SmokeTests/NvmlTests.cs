using DotCuda.Cuda12;

namespace DotCuda.SmokeTests;

/// <summary>NVML: init, enumerate devices, read memory/utilization/temperature/power. Skipped without the driver's NVML.</summary>
public class NvmlTests
{
    [Test]
    public async Task EnumerateDevicesAndReadTelemetry()
    {
        var devices = Query();
        await Assert.That(devices.Count).IsGreaterThan(0);
        foreach (var d in devices)
        {
            await Assert.That(d.TotalBytes).IsGreaterThan(0UL);
            await Assert.That(d.UsedBytes).IsLessThanOrEqualTo(d.TotalBytes);
            await Assert.That(d.GpuUtilization).IsLessThanOrEqualTo(100u);
        }
    }

    record DeviceInfo(string Name, ulong TotalBytes, ulong UsedBytes, uint GpuUtilization, uint TemperatureC, uint PowerMilliwatts);

    static unsafe List<DeviceInfo> Query()
    {
        nvmlReturn_t init;
        try
        {
            init = Nvml.nvmlInit();
        }
        catch (DllNotFoundException)
        {
            Skip.Test("NVML (nvml.dll / libnvidia-ml.so.1) not found");
            throw new System.Diagnostics.UnreachableException();
        }
        if (init != nvmlReturn_t.NVML_SUCCESS) Skip.Test($"nvmlInit returned {init}");
        try
        {
            uint count;
            Nvml.Check(Nvml.nvmlDeviceGetCount(&count));
            var result = new List<DeviceInfo>();
            byte* name = stackalloc byte[(int)Nvml.NVML_DEVICE_NAME_V2_BUFFER_SIZE];
            for (uint i = 0; i < count; i++)
            {
                nvmlDevice_t dev;
                Nvml.Check(Nvml.nvmlDeviceGetHandleByIndex(i, &dev));
                Nvml.Check(Nvml.nvmlDeviceGetName(dev, name, (uint)Nvml.NVML_DEVICE_NAME_V2_BUFFER_SIZE));
                nvmlMemory_t mem;
                Nvml.Check(Nvml.nvmlDeviceGetMemoryInfo(dev, &mem));
                nvmlUtilization_t util;
                Nvml.Check(Nvml.nvmlDeviceGetUtilizationRates(dev, &util));
                uint temp, power;
                Nvml.Check(Nvml.nvmlDeviceGetTemperature(dev, nvmlTemperatureSensors_t.NVML_TEMPERATURE_GPU, &temp));
                Nvml.Check(Nvml.nvmlDeviceGetPowerUsage(dev, &power));
                var info = new DeviceInfo(NativeLibraries.Utf8(name)!, mem.total, mem.used, util.gpu, temp, power);
                Console.WriteLine(info);
                result.Add(info);
            }
            return result;
        }
        finally
        {
            Nvml.Check(Nvml.nvmlShutdown());
        }
    }
}
