using System;
using System.Buffers.Binary;
using System.IO;
using FluentAssertions;
using VeloxClip.Platform.Clipboard;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class DibBitmapTests
{
    [Theory]
    [InlineData(40, 24, 0, 0, 54)]
    [InlineData(40, 8, 0, 0, 1078)]
    [InlineData(40, 8, 0, 2, 62)]
    [InlineData(40, 16, 3, 0, 66)]
    [InlineData(40, 32, 6, 0, 70)]
    [InlineData(108, 32, 3, 0, 122)]
    [InlineData(124, 32, 3, 0, 138)]
    public void ToBitmapFile_PlacesPixelsAfterPaletteAndMasks(
        int headerSize, ushort bits, uint compression, uint colors, uint offset)
    {
        var dib = new byte[offset - 14 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, (uint)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bits);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), colors);
        dib[^4] = 123;

        var bmp = DibBitmap.ToBitmapFile(dib);

        BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10)).Should().Be(offset);
        bmp[(int)offset].Should().Be(123);
        bmp.AsSpan(14).ToArray().Should().Equal(dib);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(39)]
    public void ToBitmapFile_RejectsTruncatedHeaders(int size)
    {
        var convert = () => DibBitmap.ToBitmapFile(new byte[size]);
        convert.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ToBitmapFile_RejectsMissingPalette()
    {
        var dib = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 8);
        var convert = () => DibBitmap.ToBitmapFile(dib);
        convert.Should().Throw<InvalidDataException>();
    }
}
