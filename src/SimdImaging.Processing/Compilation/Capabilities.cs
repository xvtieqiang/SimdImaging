namespace SimdImaging.Processing.Compilation;

/// <summary>What a source can do, as the compiler needs to know it. Not a promise about the codec's internals: a
/// <c>Seekable</c> stream is not automatically <c>Replayable</c> if the file behind it can change between passes.</summary>
[Flags]
internal enum SourceCapabilities : byte
{
    None = 0,

    /// <summary>Rows can be read forwards, once.</summary>
    ForwardOnly = 1,

    /// <summary>The reader can be repositioned.</summary>
    Seekable = 2,

    /// <summary>The same logical content can be read again.</summary>
    Replayable = 4,

    /// <summary>A rectangle can be read without decoding the whole image first.</summary>
    RandomRegion = 8,

    /// <summary>Concurrent reads are allowed.</summary>
    ConcurrentRead = 16,

    /// <summary>Two reads return the same content.</summary>
    StableContent = 32,
}

/// <summary>What a destination can accept.</summary>
[Flags]
internal enum SinkCapabilities : byte
{
    None = 0,

    /// <summary>Rows can be appended in order.</summary>
    ForwardAppend = 1,

    /// <summary>The writer can be repositioned.</summary>
    Seekable = 2,

    /// <summary>An <c>IRowSink</c> can take rows as they are produced.</summary>
    RowWritable = 4,

    /// <summary>An arbitrary rectangle can be written; the tile executor's sink contract.</summary>
    RandomRegionWrite = 8,

    /// <summary>The sink can place a tile independently of its neighbours.</summary>
    TileWrite = 16,

    /// <summary>Concurrent tile or region writes are allowed.</summary>
    ConcurrentWrite = 32,
}
