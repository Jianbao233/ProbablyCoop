using System;

namespace ProbablyCoop.Core;

// An immutable checkpoint or delta. Pages are individually bounded and
// compressed. The transport only publishes a frame after its last page.
internal sealed class NativeSnapshotFrame
{
    internal const int MaxPages = 128;
    internal const int MaxPackedBytes = 32 * 1024 * 1024;
    private readonly byte[][] pages;
    internal int Count => pages.Length;
    internal int Bytes { get; }
    internal byte[] First => pages[0];
    internal byte[] Single => Count == 1 ? First : null;
    internal bool Delta => NativeRecordFrame.IsDelta(Single);
    internal bool Full => NativeRecordFrame.IsFull(First);
    internal byte[] this[int index] => pages[index];

    internal NativeSnapshotFrame(byte[][] pages)
    {
        if (pages == null || pages.Length is < 1 or > MaxPages)
            throw new ArgumentException("Invalid native snapshot page count");
        foreach (var page in pages)
        {
            if (page == null || page.Length is < 1 or > ReplicaLink.MaxBytes ||
                (pages.Length > 1 && !NativeRecordFrame.IsFull(page)) || page.Length > MaxPackedBytes - Bytes)
                throw new ArgumentException("Invalid native snapshot page");
            Bytes += page.Length;
        }
        this.pages = pages;
    }

    internal bool SameContent(NativeSnapshotFrame other) =>
        ReferenceEquals(this, other) || Single != null && other?.Single != null && Single.AsSpan().SequenceEqual(other.Single);
}
