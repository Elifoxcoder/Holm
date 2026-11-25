using System;
using System.IO;
using Holm.Platform.Linux;
using WinNotch;

namespace Holm.Tests.Linux;

public class LinuxSoundTests
{
    [Fact]
    public void Temporary_WAVs_are_random_exclusive_and_private()
    {
        byte[] wav = LinuxSoundPlatform.Tone(880, 0.01, 0.01, 1);
        string first = LinuxSoundPlatform.CreateWav(wav);
        try
        {
            string second = LinuxSoundPlatform.CreateWav(wav);
            try
            {
                Assert.NotEqual(first, second);
                Assert.Equal(wav, File.ReadAllBytes(first));
                if (OperatingSystem.IsLinux())
                {
                    var mode = File.GetUnixFileMode(first);
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                        File.GetUnixFileMode(Path.GetDirectoryName(first)!));
                }
            }
            finally { File.Delete(second); }
        }
        finally { File.Delete(first); }
    }

    [Fact]
    public void The_fallback_beeps_are_a_playable_wav()
    {
        byte[] wav = LinuxSoundPlatform.Tone(880, 0.12, 0.10, 3);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40)); // the data chunk holds the rest
        Assert.Equal(22050, BitConverter.ToInt32(wav, 24));
        Assert.Contains(wav.Skip(44), b => b != 0); // it isn't silence
    }
}
