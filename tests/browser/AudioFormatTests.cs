using System.Text.Json;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using Microsoft.Playwright;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>
/// Spec 18.1 audio conversion checks for wwwroot/js/audio-format.js, run in the real browser engine by importing the
/// module exactly as the page and the AudioWorklets do.
/// </summary>
public sealed class AudioFormatTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    // Deterministic PRNG (LCG) shared by the scripts below; returns values in [0, 1).
    private const string Random = "let seed = 0x2545F491; const random = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296; };";

    [Fact]
    public async Task FloatToPcm16ClampsRoundsAndZeroesNaN()
    {
        var result = await RunAsync<int[]>("""
            const m = await import('/js/audio-format.js');
            return Array.from(m.floatToPcm16(new Float32Array([1.5, -1.5, 1, -1, 0, 0.5, -0.5, NaN, Infinity, -Infinity])));
            """);

        Assert.Equal([32767, -32768, 32767, -32768, 0, 16384, -16384, 0, 32767, -32768], result);
    }

    [Fact]
    public async Task Pcm16BytesAreLittleEndianAndRoundTrip()
    {
        var result = await RunAsync<JsonElement>("""
            const m = await import('/js/audio-format.js');
            const samples = new Int16Array([0x1234, -2, 32767, -32768, 0]);
            const bytes = m.int16ToLittleEndianBytes(samples);
            let oddLengthError = null;
            try { m.littleEndianBytesToInt16(new Uint8Array(3)); } catch (error) { oddLengthError = error.name; }
            return {
              bytes: Array.from(bytes),
              back: Array.from(m.littleEndianBytesToInt16(bytes)),
              fromOffsetView: Array.from(m.littleEndianBytesToInt16(new Uint8Array([9, 0x34, 0x12, 0xfe, 0xff]).subarray(1))),
              oddLengthError,
            };
            """);

        Assert.Equal([0x34, 0x12, 0xFE, 0xFF, 0xFF, 0x7F, 0x00, 0x80, 0x00, 0x00], Ints(result, "bytes"));
        Assert.Equal([0x1234, -2, 32767, -32768, 0], Ints(result, "back"));
        Assert.Equal([0x1234, -2], Ints(result, "fromOffsetView"));
        Assert.Equal("RangeError", result.GetProperty("oddLengthError").GetString());
    }

    [Fact]
    public async Task Base64RoundTripsSmallAndLargeBuffers()
    {
        var result = await RunAsync<JsonElement>("""
            const m = await import('/js/audio-format.js');
            const equal = (a, b) => a.length === b.length && a.every((value, i) => value === b[i]);
            const all = Uint8Array.from({ length: 256 }, (_, i) => i);
            const large = Uint8Array.from({ length: 50000 }, (_, i) => (i * 7 + 3) & 255);
            const pcm = m.floatToPcm16(Float32Array.from({ length: 480 }, (_, i) => Math.sin(i / 7)));
            const wire = m.bytesToBase64(m.int16ToLittleEndianBytes(pcm));
            return {
              known: m.bytesToBase64(new Uint8Array([0, 1, 2, 255])),
              allBytes: equal(m.base64ToBytes(m.bytesToBase64(all)), all),
              matchesBtoa: m.bytesToBase64(all) === btoa(String.fromCharCode(...all)),
              largeBuffer: equal(m.base64ToBytes(m.bytesToBase64(large)), large),
              pcmFrame: equal(m.littleEndianBytesToInt16(m.base64ToBytes(wire)), pcm),
              frameBase64Length: wire.length,
            };
            """);

        Assert.Equal("AAEC/w==", result.GetProperty("known").GetString());
        Assert.True(result.GetProperty("allBytes").GetBoolean());
        Assert.True(result.GetProperty("matchesBtoa").GetBoolean());
        Assert.True(result.GetProperty("largeBuffer").GetBoolean());
        Assert.True(result.GetProperty("pcmFrame").GetBoolean());
        Assert.Equal(1280, result.GetProperty("frameBase64Length").GetInt32());
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public async Task ResamplerOutputIsIdenticalForOneCallAndRandomChunks(int inputRate)
    {
        var result = await RunAsync<JsonElement>(Random + """
            const m = await import('/js/audio-format.js');
            const inputRate = args;
            const input = new Float32Array(inputRate * 2);
            for (let i = 0; i < input.length; i++) input[i] = 0.5 * Math.sin(2 * Math.PI * 440 * i / inputRate) + 0.3 * (random() - 0.5);
            const whole = new m.StreamingResampler(inputRate, 24000).process(input);
            const streaming = new m.StreamingResampler(inputRate, 24000);
            const parts = [];
            let offset = 0;
            while (offset < input.length) {
              const size = Math.min(input.length - offset, [0, 1, 127, 128][Math.floor(random() * 6)] ?? 1 + Math.floor(random() * 3000));
              parts.push(streaming.process(input.subarray(offset, offset + size)));
              offset += size;
            }
            const chunked = new Float32Array(parts.reduce((sum, part) => sum + part.length, 0));
            let position = 0;
            for (const part of parts) { chunked.set(part, position); position += part.length; }
            let mismatches = 0;
            for (let i = 0; i < Math.min(whole.length, chunked.length); i++) if (whole[i] !== chunked[i]) mismatches++;
            return { inputLength: input.length, whole: whole.length, chunked: chunked.length, chunks: parts.length, mismatches, lookahead: streaming.lookahead };
            """, inputRate);

        var inputLength = result.GetProperty("inputLength").GetInt32();
        var whole = result.GetProperty("whole").GetInt32();
        var lookahead = result.GetProperty("lookahead").GetInt32();
        Assert.True(result.GetProperty("chunks").GetInt32() > 100, "The input should be split into many chunks.");
        Assert.Equal(whole, result.GetProperty("chunked").GetInt32());
        Assert.Equal(0, result.GetProperty("mismatches").GetInt32());

        // Output length is input x (24000 / inputRate), minus only the look-ahead held back for future input.
        var expected = inputLength * 24_000.0 / inputRate;
        Assert.InRange(whole, expected - (lookahead * 24_000.0 / inputRate) - 2, expected + 1);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public async Task ResamplerHasUnityDcGainAndKeepsA1KhzSineAmplitude(int inputRate)
    {
        var result = await RunAsync<JsonElement>("""
            const m = await import('/js/audio-format.js');
            const inputRate = args;
            const dc = new m.StreamingResampler(inputRate, 24000).process(new Float32Array(inputRate).fill(0.5));
            let dcMaxError = 0;
            for (let i = 200; i < dc.length; i++) dcMaxError = Math.max(dcMaxError, Math.abs(dc[i] - 0.5));
            const sineInput = Float32Array.from({ length: inputRate }, (_, i) => 0.8 * Math.sin(2 * Math.PI * 1000 * i / inputRate));
            const sine = new m.StreamingResampler(inputRate, 24000).process(sineInput);
            // Steady state over a whole number of 1 kHz cycles (24 output samples per cycle).
            const start = 240, count = 24 * 900;
            let sumSquares = 0, peak = 0;
            for (let i = start; i < start + count; i++) { sumSquares += sine[i] * sine[i]; peak = Math.max(peak, Math.abs(sine[i])); }
            return { dcLength: dc.length, dcMaxError, sineLength: sine.length, amplitudeFromRms: Math.sqrt(2 * sumSquares / count), peak };
            """, inputRate);

        Assert.True(result.GetProperty("dcLength").GetInt32() > 20_000);
        Assert.True(result.GetProperty("dcMaxError").GetDouble() < 1e-4, $"DC gain error {result.GetProperty("dcMaxError").GetDouble()}");
        Assert.True(result.GetProperty("sineLength").GetInt32() >= 240 + (24 * 900));
        Assert.InRange(result.GetProperty("amplitudeFromRms").GetDouble(), 0.8 * 0.95, 0.8 * 1.05);
        Assert.InRange(result.GetProperty("peak").GetDouble(), 0.8 * 0.95, 0.8 * 1.05);
    }

    private async Task<T> RunAsync<T>(string body, object? args = null)
    {
        var page = await OpenPageAsync(signedIn: false);
        await page.GotoAsync("/access");
        return await page.EvaluateAsync<T>($"async (args) => {{ {body} }}", args);
    }

    private static int[] Ints(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(item => item.GetInt32()).ToArray();
}
