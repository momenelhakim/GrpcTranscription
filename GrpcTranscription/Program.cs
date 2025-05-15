using System;
using System.Net.Http;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Net.Client;
using NAudio.Wave;
using Google.Protobuf;
using Transcription;
using System.Threading; // Ensure your generated gRPC code uses this namespace

class Program
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int SampleWidth = 2; // 16-bit PCM
    private const int ChunkSize = 320; // ~10ms of audio at 16kHz mono

    static async Task Main(string[] args)
    {
        // Setup gRPC channel
        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        var channel = GrpcChannel.ForAddress("http://34.55.74.225:50052", new GrpcChannelOptions
        {
            HttpHandler = httpHandler
        });

        var client = new TranscriptionService.TranscriptionServiceClient(channel);
        using var call = client.Transcribe();

        var sessionId = Guid.NewGuid().ToString();

        // Create audio queue
        var audioQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Stream audio chunks from queue to server
        var sendTask = Task.Run(async () =>
        {
            await foreach (var chunk in audioQueue.Reader.ReadAllAsync())
            {
                await call.RequestStream.WriteAsync(new AudioChunk
                {
                    AudioData = ByteString.CopyFrom(chunk),
                    SessionId = sessionId,
                    AudioFormat = "wav",
                    AudioSr = (uint)SampleRate,
                    SampleWidth = (uint)SampleWidth,
                    Channels = (uint)Channels,
                    OutputFormat = "Text",
                    TaskType = "Transcript",
                    AutoSilenceDetection = true,
                    SilenceThreshold = 300
                });
            }

            // Finalize stream
            await call.RequestStream.WriteAsync(new AudioChunk
            {
                SessionId = sessionId,
                IsLastChunk = true
            });

            await call.RequestStream.CompleteAsync();
        });

        // Read transcriptions as they come in
        var receiveTask = Task.Run(async () =>
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                var response = call.ResponseStream.Current;
                if (!string.IsNullOrWhiteSpace(response.Transcript))
                {
                    Console.WriteLine($"📢 {response.Transcript} {(response.LongSilence ? "[Silent]" : "")}");
                }
            }
        });

        // Setup microphone input
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, SampleWidth * 8, Channels),
            BufferMilliseconds = 20 // Lower latency
        };

        waveIn.DataAvailable += (s, e) =>
        {
            byte[] chunk = e.Buffer[..Math.Min(ChunkSize, e.BytesRecorded)];

            // Padding if smaller than expected
            if (chunk.Length < ChunkSize)
            {
                var padded = new byte[ChunkSize];
                Array.Copy(chunk, padded, chunk.Length);
                chunk = padded;
            }

            // Stream the chunk
            audioQueue.Writer.TryWrite(chunk);
        };

        Console.WriteLine("🎙️  Recording & Transcribing Live... Press Enter to stop.");
        waveIn.StartRecording();
        Console.ReadLine();
        waveIn.StopRecording();

        Console.WriteLine("✅ Transcription finished.");
    }
}
