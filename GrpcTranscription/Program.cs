using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Net.Client;
using NAudio.Wave;
using Google.Protobuf;
using Transcription;
using SixLabors;

class Program
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int SampleWidth = 2;
    private const int ChunkSize = 640;// 20ms of audio - Keep this!

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        var channel = GrpcChannel.ForAddress("https://sttgrpc.widebot.net", new GrpcChannelOptions { HttpHandler = httpHandler });
        var client = new TranscriptionService.TranscriptionServiceClient(channel);
        using var call = client.Transcribe(cancellationToken: cts.Token);

        var sessionId = Guid.NewGuid().ToString();
        var audioQueue = Channel.CreateUnbounded<byte[]>();

        #region SEND TASK
        var sendTask = Task.Run(async () =>
        {
            await foreach (var chunk in audioQueue.Reader.ReadAllAsync(cts.Token))
            {
                await call.RequestStream.WriteAsync(new AudioChunk
                {
                    AudioData = ByteString.CopyFrom(chunk),
                    SessionId = sessionId,
                    AudioFormat = "raw",
                    AudioSr = (uint)SampleRate,
                    SampleWidth = (uint)SampleWidth,
                    Channels = (uint)Channels,
                    ModelId = "conformer-ar",
                    TgtLang = "ar",
                    AutoSilenceDetection = false,
                });
            }
            await call.RequestStream.WriteAsync(new AudioChunk { SessionId = sessionId, IsLastChunk = true });
            await call.RequestStream.CompleteAsync();
        }, cts.Token);
        #endregion

        #region RECEIVE TASK
        var receiveTask = Task.Run(async () =>
        {
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                var response = call.ResponseStream.Current;
                if (string.IsNullOrWhiteSpace(response.Transcript)) continue;

                var text = FixArabicTranscript(response.Transcript);

                if (response.IsFinal)
                {
                    // Clear the line and show the final version in green
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"\r✅ FINAL: {text}");
                    Console.ResetColor();
                }
                else
                {
                    // Show partial results in gray to see it "thinking"
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write($"\r🔄 Thinking: {text}...");
                    Console.ResetColor();
                }
            }
        }, cts.Token);
        #endregion

        #region MICROPHONE
        var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, SampleWidth * 8, Channels), BufferMilliseconds = 20 };
        waveIn.DataAvailable += (s, e) =>
        {
            if (cts.IsCancellationRequested) return;
            var chunk = new byte[ChunkSize];
            Array.Copy(e.Buffer, chunk, Math.Min(e.BytesRecorded, ChunkSize));
            audioQueue.Writer.TryWrite(chunk);
        };
        #endregion

        Console.WriteLine("🎙️ Recording... Press ENTER to stop.");
        waveIn.StartRecording();
        Console.ReadLine();

        cts.Cancel();
        waveIn.StopRecording();
        audioQueue.Writer.Complete();
        try { await Task.WhenAll(sendTask, receiveTask); } catch { }
        Console.WriteLine("\n✅ Done.");
    }

    private static string FixArabicTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        text = text.Trim().Normalize(NormalizationForm.FormC).Replace("?", "؟");

        // Manual visual fix for Console
        char[] letters = text.ToCharArray();
        Array.Reverse(letters);
        return new string(letters);
    }
}