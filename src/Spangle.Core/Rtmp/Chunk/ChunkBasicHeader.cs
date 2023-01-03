// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Spangle.Rtmp.Chunk;

/// <summary>
/// Chunk Basic Header
/// 1 ～ 3 bytes
/// </summary>
/*
  0 1 2 3 4 5 6 7
 +-+-+-+-+-+-+-+-+
 |fmt|   cs id   |
 +-+-+-+-+-+-+-+-+

 Chunk basic header 1

----

  0                   1
  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |fmt|     0     |   cs id - 64  |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

 Chunk basic header 2
 
----

  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
 |fmt|     1     |          cs id - 64           |
 +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

 Chunk basic header 3
 */
internal struct ChunkBasicHeader
{
    public const int MaxSize = 3;

    public ChunkFormat Format;
    public uint ChunkStreamId;

    public void Renew(byte format, uint chunkStreamId)
    {
        Format = (ChunkFormat)format;
        ChunkStreamId = chunkStreamId;
    }
}
