// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mimic.Core;

/// <summary>Records one application's sound, for videos no downloader can reach.</summary>
public static unsafe class Capture
{
    /// <summary>What Whisper reads: 16 kHz, mono, 16-bit.</summary>
    private const int Rate = 16_000;
    private const int BytesPerFrame = 2;
    private const uint WaitMs = 200;
    /// <summary>Quieter than this is not sound anyone can hear: about -60 dB.</summary>
    private const int QuietBelow = 32;

    internal static byte[] WavHeader(uint dataBytes)
    {
        var header = new MemoryStream(44);
        var writer = new BinaryWriter(header);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)Rate);
        writer.Write((uint)(Rate * BytesPerFrame));
        writer.Write((ushort)BytesPerFrame);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        return header.ToArray();
    }

    internal static bool Audible(ReadOnlySpan<byte> samples)
    {
        for (var i = 0; i + BytesPerFrame <= samples.Length; i += BytesPerFrame)
        {
            var sample = (short)(samples[i] | samples[i + 1] << 8);
            if (Math.Abs((int)sample) >= QuietBelow) return true;
        }
        return false;
    }

    /// <summary>
    /// Records the process and everything it started until the user presses Finish.
    /// Windows keeps delivering silence while nothing plays, so silent stretches are dropped:
    /// pausing the video adds nothing, and a window that never plays records nothing.
    /// Returns the seconds recorded.
    /// </summary>
    public static long Record(Job job, uint processId, string output)
    {
        _ = CoInitializeEx(null, 0);
        void* client = null;
        void* capture = null;
        var signal = IntPtr.Zero;
        try
        {
            client = Check(ActivateLoopback(processId, out var activated), activated);
            var format = stackalloc byte[40];
            WriteFormat(format);
            const uint eventCallback = 0x00040000, loopback = 0x00020000, autoConvert = 0x80000000, defaultQuality = 0x08000000;
            // IAudioClient::Initialize, shared mode, default buffer.
            Check(((delegate* unmanaged<void*, int, uint, long, long, byte*, Guid*, int>)Slot(client, 3))(
                client, 0, eventCallback | loopback | autoConvert | defaultQuality, 0, 0, format, null));
            signal = CreateEventW(null, 0, 0, null);
            // IAudioClient::SetEventHandle
            Check(((delegate* unmanaged<void*, IntPtr, int>)Slot(client, 13))(client, signal));
            var captureId = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            void* service = null;
            // IAudioClient::GetService
            Check(((delegate* unmanaged<void*, Guid*, void**, int>)Slot(client, 14))(client, &captureId, &service));
            capture = service;

            using var file = Sys.Io(() => new FileStream(output, FileMode.Create, FileAccess.Write));
            Sys.Io(() => file.Write(WavHeader(0)));
            // IAudioClient::Start
            Check(((delegate* unmanaged<void*, int>)Slot(client, 10))(client));

            long written = 0;
            var queue = new MemoryStream();
            while (!job.FinishRequested && !job.Cancelled)
            {
                // A timeout only means nothing is playing right now.
                _ = WaitForSingleObject(signal, WaitMs);
                while (true)
                {
                    uint packet = 0;
                    // IAudioCaptureClient::GetNextPacketSize
                    Check(((delegate* unmanaged<void*, uint*, int>)Slot(capture, 5))(capture, &packet));
                    if (packet == 0) break;
                    byte* data = null;
                    uint frames = 0;
                    uint flags = 0;
                    // IAudioCaptureClient::GetBuffer
                    Check(((delegate* unmanaged<void*, byte**, uint*, uint*, ulong*, ulong*, int>)Slot(capture, 3))(
                        capture, &data, &frames, &flags, null, null));
                    if (frames == 0) break;
                    queue.Write(new ReadOnlySpan<byte>(data, checked((int)frames * BytesPerFrame)));
                    // IAudioCaptureClient::ReleaseBuffer
                    Check(((delegate* unmanaged<void*, uint, int>)Slot(capture, 4))(capture, frames));
                }
                var samples = queue.GetBuffer().AsSpan(0, (int)queue.Length);
                if (Audible(samples))
                {
                    var chunk = samples.ToArray();
                    Sys.Io(() => file.Write(chunk));
                    written += chunk.Length;
                }
                queue.SetLength(0);
            }
            // IAudioClient::Stop
            _ = ((delegate* unmanaged<void*, int>)Slot(client, 11))(client);

            if (job.Cancelled) throw new MimicException(Tools.Cancelled);
            if (written == 0)
            {
                throw new MimicException("No sound came from that window. Play the video first, then press Finish when it ends.");
            }
            Sys.Io(() =>
            {
                file.Seek(0, SeekOrigin.Begin);
                file.Write(WavHeader((uint)written));
            });
            return written / (Rate * BytesPerFrame);
        }
        finally
        {
            Release(capture);
            Release(client);
            if (signal != IntPtr.Zero) CloseHandle(signal);
        }
    }

    /// <summary>16-bit PCM, one channel, as a WAVEFORMATEXTENSIBLE.</summary>
    private static void WriteFormat(byte* format)
    {
        *(ushort*)format = 0xFFFE;
        *(ushort*)(format + 2) = 1;
        *(uint*)(format + 4) = Rate;
        *(uint*)(format + 8) = Rate * BytesPerFrame;
        *(ushort*)(format + 12) = BytesPerFrame;
        *(ushort*)(format + 14) = 16;
        *(ushort*)(format + 16) = 22;
        *(ushort*)(format + 18) = 16;
        *(uint*)(format + 20) = 0x4;
        *(Guid*)(format + 24) = new Guid("00000001-0000-0010-8000-00aa00389b71");
    }

    /// <summary>An audio client that hears one process and everything it started.</summary>
    private static int ActivateLoopback(uint processId, out IntPtr client)
    {
        client = IntPtr.Zero;
        // AUDIOCLIENT_ACTIVATION_PARAMS: process loopback, this process, including its tree.
        var activation = stackalloc uint[3];
        activation[0] = 1;
        activation[1] = processId;
        activation[2] = 0;
        // A PROPVARIANT holding that as a blob.
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 65;
        *(uint*)(variant + 8) = 12;
        *(void**)(variant + 16) = activation;

        var handler = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
        handler->Table = HandlerTable;
        handler->References = 1;
        handler->Done = CreateEventW(null, 0, 0, null);
        void* operation = null;
        try
        {
            var clientId = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            int result;
            fixed (char* device = "VAD\\Process_Loopback")
            {
                result = ActivateAudioInterfaceAsync(device, &clientId, variant, handler, &operation);
            }
            if (result < 0) return result;
            _ = WaitForSingleObject(handler->Done, 0xFFFFFFFF);
            int activateResult = 0;
            void* unknown = null;
            // IActivateAudioInterfaceAsyncOperation::GetActivateResult
            result = ((delegate* unmanaged<void*, int*, void**, int>)Slot(operation, 3))(operation, &activateResult, &unknown);
            if (result < 0) return result;
            if (activateResult < 0) return activateResult;
            void* audioClient = null;
            // IUnknown::QueryInterface
            result = ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(unknown, 0))(unknown, &clientId, &audioClient);
            Release(unknown);
            client = (IntPtr)audioClient;
            return result;
        }
        finally
        {
            Release(operation);
            var done = handler->Done;
            if (Interlocked.Decrement(ref handler->References) == 0) NativeMemory.Free(handler);
            CloseHandle(done);
        }
    }

    private static void* Check(int result, IntPtr value = default)
    {
        if (result < 0) throw new MimicException($"{new Win32Exception(result).Message} (0x{result:X8})");
        return (void*)value;
    }

    private static void* Slot(void* instance, int index) => (*(void***)instance)[index];

    private static void Release(void* instance)
    {
        if (instance != null) ((delegate* unmanaged<void*, uint>)Slot(instance, 2))(instance);
    }

    /// <summary>The object Windows calls when the audio client is ready. It only raises an event.</summary>
    private struct Handler
    {
        public void** Table;
        public int References;
        public IntPtr Done;
    }

    private static readonly void** HandlerTable = BuildHandlerTable();

    private static void** BuildHandlerTable()
    {
        var table = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged<Handler*, Guid*, void**, int>)&HandlerQueryInterface;
        table[1] = (delegate* unmanaged<Handler*, uint>)&HandlerAddRef;
        table[2] = (delegate* unmanaged<Handler*, uint>)&HandlerRelease;
        table[3] = (delegate* unmanaged<Handler*, void*, int>)&HandlerActivateCompleted;
        return table;
    }

    [UnmanagedCallersOnly]
    private static int HandlerQueryInterface(Handler* self, Guid* id, void** result)
    {
        // IUnknown, IActivateAudioInterfaceCompletionHandler and IAgileObject.
        if (*id == new Guid("00000000-0000-0000-C000-000000000046")
            || *id == new Guid("41D949AB-9862-444A-80F6-C261334DA5EB")
            || *id == new Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"))
        {
            Interlocked.Increment(ref self->References);
            *result = self;
            return 0;
        }
        *result = null;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly]
    private static uint HandlerAddRef(Handler* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly]
    private static uint HandlerRelease(Handler* self)
    {
        var left = Interlocked.Decrement(ref self->References);
        if (left == 0) NativeMemory.Free(self);
        return (uint)left;
    }

    [UnmanagedCallersOnly]
    private static int HandlerActivateCompleted(Handler* self, void* operation)
    {
        SetEvent(self->Done);
        return 0;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(void* reserved, uint model);

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(char* device, Guid* id, void* activation, void* handler, void** operation);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateEventW(void* security, int manualReset, int initialState, char* name);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(IntPtr handle);
}
