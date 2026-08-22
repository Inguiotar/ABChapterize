// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Runtime.InteropServices;

namespace ABChapterize.Gpu;

/// <summary>
/// Asks the Vulkan loader which GPUs exist, so a device can be chosen by name instead of by an
/// index nobody can predict (see <see cref="GpuDevice"/> for what makes indices untrustworthy).
/// </summary>
/// <remarks>
/// <para>
/// Whisper.net's own API offers no way to list devices - only <c>WhisperFactoryOptions.GpuDevice</c>,
/// an int - so the list has to come from Vulkan directly. Doing it in-process, shortly before the
/// model loads, is the point: the enumeration this returns is the one *this* process's loader
/// produced, which is what the Whisper backend will index into moments later. An index obtained any
/// other way (a previous run, another login, a note in a wiki) can be stale.
/// </para>
/// <para>
/// Every failure path returns an empty list rather than throwing. Vulkan being absent is a normal
/// state of the world - a CPU-only machine, a container without the loader, a CUDA-only setup - and
/// must not stop a run that was never going to use it. Callers treat "no devices" as "no automatic
/// selection possible", not as an error.
/// </para>
/// </remarks>
public static class VulkanDeviceEnumerator
{
    /// <summary>Import name; the resolver below maps it to the platform's actual library.</summary>
    private const string VulkanLibrary = "vulkan-1";

    /// <summary>VkStructureType value for VkInstanceCreateInfo.</summary>
    private const uint StructureTypeInstanceCreateInfo = 1;

    /// <summary>VkResult value for success.</summary>
    private const int VkSuccess = 0;

    /// <summary>
    /// Bytes handed to the driver for VkPhysicalDeviceProperties. The real struct is around 824
    /// bytes (mostly VkPhysicalDeviceLimits, which is of no interest here), so this is generous on
    /// purpose: only the leading fields are read back, but the driver writes the whole thing and
    /// must not be given a short buffer.
    /// </summary>
    private const int PropertiesBufferSize = 4096;

    /// <summary>Byte offset of <c>deviceType</c> within VkPhysicalDeviceProperties: it follows
    /// apiVersion, driverVersion, vendorID and deviceID, all uint32.</summary>
    private const int DeviceTypeOffset = 4 * sizeof(uint);

    /// <summary>Byte offset of <c>deviceName</c>: immediately after <c>deviceType</c>.</summary>
    private const int DeviceNameOffset = DeviceTypeOffset + sizeof(uint);

    /// <summary>Length of the <c>deviceName</c> array (VK_MAX_PHYSICAL_DEVICE_NAME_SIZE), NUL-padded.</summary>
    private const int DeviceNameSize = 256;

    static VulkanDeviceEnumerator()
    {
        // The Windows loader is vulkan-1.dll, everywhere else libvulkan.so.1 - the unversioned
        // .so is a development symlink that is often absent on a machine with only a runtime
        // installed, so it is a fallback rather than the first guess.
        NativeLibrary.SetDllImportResolver(typeof(VulkanDeviceEnumerator).Assembly, (name, assembly, path) =>
        {
            if (name != VulkanLibrary)
                return IntPtr.Zero;

            foreach (var candidate in new[] { "vulkan-1.dll", "libvulkan.so.1", "libvulkan.so" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle))
                    return handle;

            return IntPtr.Zero;
        });
    }

    /// <summary>
    /// What an empty <see cref="Enumerate"/> result means on the platform this is running on, as a
    /// standalone sentence for a user-facing message.
    /// </summary>
    /// <remarks>
    /// Split into two <c>internal</c> texts rather than a ternary so that both can be asserted from
    /// any host: the macOS wording is unreachable on the two platforms this project can actually
    /// run, and an empty device list is the one thing a Mac user is guaranteed to meet.
    /// </remarks>
    public static string AbsenceNote =>
        OperatingSystem.IsMacOS() ? MacAbsenceNote : DefaultAbsenceNote;

    /// <summary>What an empty device list means on macOS.</summary>
    /// <remarks>
    /// Its own wording because the emptiness there is categorical rather than circumstantial.
    /// Everywhere else "no devices" describes a machine that could have some - install a driver,
    /// pass a GPU through to the container - so the message can point at the alternative backends.
    /// On macOS there is no Vulkan loader to install, Whisper.net publishes neither a Vulkan nor a
    /// CUDA native for the platform, and ggml reaches Metal from inside the CPU runtime without
    /// exposing a device list anything here could enumerate or filter. Offering a Mac user the
    /// usual remedies would send them after something that does not exist, so this says the feature
    /// is absent rather than merely unavailable - and says what is running instead, because a user
    /// who reads "no GPU" will otherwise assume the run is wasting one.
    /// </remarks>
    internal static string MacAbsenceNote =>
        "macOS has no Vulkan loader, so GPU selection does not apply there. Whisper runs on "
        + "Apple's Metal backend where ggml offers one, which this tool neither chooses nor names.";

    /// <summary>What an empty device list means anywhere Vulkan could have been present.</summary>
    internal static string DefaultAbsenceNote => "Whisper will use CUDA or the CPU backend.";

    /// <summary>
    /// Lists the GPUs Vulkan reports, in enumeration order, or an empty list if Vulkan is
    /// unavailable for any reason.
    /// </summary>
    public static IReadOnlyList<GpuDevice> Enumerate()
    {
        try
        {
            return EnumerateCore();
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// The enumeration proper: create a throwaway instance, ask it for the physical devices, read
    /// each one's properties, destroy the instance again.
    /// </summary>
    private static IReadOnlyList<GpuDevice> EnumerateCore()
    {
        // pApplicationInfo may be null, which is why VkApplicationInfo is not modelled at all: it
        // only carries names and a requested API version, none of which affect enumeration.
        var createInfo = new VkInstanceCreateInfo { SType = StructureTypeInstanceCreateInfo };

        if (vkCreateInstance(ref createInfo, IntPtr.Zero, out var instance) != VkSuccess || instance == IntPtr.Zero)
            return [];

        try
        {
            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) != VkSuccess || count == 0)
                return [];

            var handles = new IntPtr[count];
            if (vkEnumeratePhysicalDevices(instance, ref count, handles) != VkSuccess)
                return [];

            var devices = new List<GpuDevice>((int)count);
            for (var i = 0; i < count; i++)
                devices.Add(ReadProperties(i, handles[i]));

            return devices;
        }
        finally
        {
            vkDestroyInstance(instance, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Reads one device's name and type out of the raw VkPhysicalDeviceProperties bytes, avoiding
    /// a managed mirror of a struct whose bulk (the limits) is irrelevant here.
    /// </summary>
    /// <param name="index">Enumeration position, which becomes <see cref="GpuDevice.Index"/>.</param>
    /// <param name="handle">The VkPhysicalDevice handle to query.</param>
    private static GpuDevice ReadProperties(int index, IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(PropertiesBufferSize);
        try
        {
            // Zeroed first so a driver that writes less than expected cannot leave us reading
            // whatever the heap happened to hold.
            for (var i = 0; i < PropertiesBufferSize; i += sizeof(long))
                Marshal.WriteInt64(buffer, i, 0);

            vkGetPhysicalDeviceProperties(handle, buffer);

            var kind = (GpuDeviceKind)Marshal.ReadInt32(buffer, DeviceTypeOffset);
            if (!Enum.IsDefined(kind))
                kind = GpuDeviceKind.Other;

            var name = Marshal.PtrToStringUTF8(buffer + DeviceNameOffset, DeviceNameSize) ?? string.Empty;
            // The field is a fixed 256-byte array padded with NULs, which PtrToStringUTF8 happily
            // includes when given an explicit length.
            var end = name.IndexOf('\0');
            if (end >= 0)
                name = name[..end];

            return new GpuDevice(index, name.Trim(), kind);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The subset of VkInstanceCreateInfo this needs: everything except <c>sType</c> stays zero,
    /// meaning no layers, no extensions and no application info.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [DllImport(VulkanLibrary)]
    private static extern int vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport(VulkanLibrary)]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport(VulkanLibrary)]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint deviceCount, IntPtr[]? devices);

    [DllImport(VulkanLibrary)]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);
}
