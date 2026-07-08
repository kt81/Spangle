using System.Buffers;
using BenchmarkDotNet.Attributes;
using Spangle.Containers.ISOBMFF;
using Spangle.Containers.M2TS;

namespace Spangle.Benchmarks;

/// <summary>
/// Measures the MPEG-2 TS muxer: one keyframe (PAT+PMT+PES with PCR/RAI)
/// and one inter frame per invocation, 5KB access units.
/// </summary>
[MemoryDiagnoser]
public class M2TSWriterBenchmarks
{
    private readonly M2TSWriter _writer = new();
    private readonly ArrayBufferWriter<byte> _outlet = new(256 * 1024);
    private readonly byte[] _accessUnit = CreateAccessUnit(5 * 1024);

    private ulong _ts;

    private static byte[] CreateAccessUnit(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        // Annex B-ish start
        data[0] = 0; data[1] = 0; data[2] = 0; data[3] = 1;
        return data;
    }

    [Benchmark]
    public int KeyFrame()
    {
        _outlet.ResetWrittenCount();
        _ts += 3000;
        _writer.WriteProgramTables(_outlet);
        _writer.WritePes(_outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            _accessUnit, _ts, null, randomAccess: true, withPcr: true);
        return _outlet.WrittenCount;
    }

    [Benchmark]
    public int InterFrame()
    {
        _outlet.ResetWrittenCount();
        _ts += 3000;
        _writer.WritePes(_outlet, M2TSWriter.PidVideo, M2TSWriter.StreamIdVideo,
            _accessUnit, _ts, null, randomAccess: false, withPcr: true);
        return _outlet.WrittenCount;
    }
}

/// <summary>
/// Measures building one CMAF fragment (moof+mdat) of a 1-second part:
/// 30 video samples of 5KB plus 43 AAC samples of 300B.
/// </summary>
[MemoryDiagnoser]
public class CmafPackagerBenchmarks
{
    private CmafPackager _packager = null!;
    private CmafSample[] _video = [];
    private CmafSample[] _audio = [];
    private readonly MemoryStream _output = new();

    [GlobalSetup]
    public void Setup()
    {
        var videoTrack = new CmafVideoTrack
        {
            Codec = VideoCodec.H264,
            ConfigRecord = new byte[40],
            Width = 1280,
            Height = 720,
        };
        var audioTrack = new CmafAudioTrack
        {
            AudioSpecificConfig = [0x12, 0x10],
            SampleRate = 44100,
            ChannelCount = 2,
        };
        _packager = new CmafPackager(videoTrack, audioTrack);

        var videoData = new byte[5 * 1024];
        Random.Shared.NextBytes(videoData);
        _video = new CmafSample[30];
        for (var i = 0; i < _video.Length; i++)
        {
            _video[i] = new CmafSample
            {
                Data = videoData,
                Duration = 3000,
                CompositionOffset = 0,
                IsSync = i == 0,
            };
        }

        var audioData = new byte[300];
        Random.Shared.NextBytes(audioData);
        _audio = new CmafSample[43];
        for (var i = 0; i < _audio.Length; i++)
        {
            _audio[i] = new CmafSample
            {
                Data = audioData,
                Duration = 1024,
                CompositionOffset = 0,
                IsSync = true,
            };
        }
    }

    [Benchmark]
    public long BuildFragment()
    {
        _output.SetLength(0);
        _packager.BuildFragment(0, _video, 0, _audio, _output);
        return _output.Length;
    }
}
