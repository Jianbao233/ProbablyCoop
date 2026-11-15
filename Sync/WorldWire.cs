namespace ProbablyCoop.Sync;

// Channel 1 of a ready session: the shared world. One kind byte, then the body.
// Kinds 1..6 are SyncMessage frames (SyncCodec); the checkpoint shares their channel
// because a commit must never overtake the checkpoint it builds on.
internal static class WorldWire
{
    internal const byte Channel = 1;
    internal const byte Checkpoint = 16; // host -> guest: barrier, sequence, packed save
    internal const byte Resync = 17;     // guest -> host: a commit is missing, send a checkpoint
    internal const byte TeamVote = 18;   // guest -> host: same-scene transition confirmation
    internal const byte TeamReadiness = 19; // host -> guest: accepted proposal and votes
    internal const byte AuthorityRequest = 20; // guest -> host: an action that runs original shared rules
    internal const byte TeamCancel = 21; // guest -> host: cancel a specific uncommitted proposal
    // A save inflates to at most this; larger declared sizes are refused unread.
    internal const int MaxSave = 64 * 1024 * 1024;
    private const int Header = 13;

    // The save must be the world exactly as it stood after commit `sequence`.
    internal static byte[] EncodeCheckpoint(uint barrier, ulong sequence, byte[] save)
    {
        byte[] packed = SyncPack.Pack(save);
        var message = new byte[packed.Length + Header];
        message[0] = Checkpoint;
        for (int i = 0; i < 4; i++) message[1 + i] = (byte)(barrier >> (8 * i));
        for (int i = 0; i < 8; i++) message[5 + i] = (byte)(sequence >> (8 * i));
        System.Buffer.BlockCopy(packed, 0, message, Header, packed.Length);
        return message;
    }

    internal static bool TryDecodeCheckpoint(byte[] message, out uint barrier, out ulong sequence, out byte[] save)
    {
        barrier = 0;
        sequence = 0;
        save = null;
        if (message == null || message.Length <= Header || message[0] != Checkpoint) return false;
        for (int i = 0; i < 4; i++) barrier |= (uint)message[1 + i] << (8 * i);
        for (int i = 0; i < 8; i++) sequence |= (ulong)message[5 + i] << (8 * i);
        return SyncPack.TryUnpack(message, Header, MaxSave, out save) && save.Length > 0;
    }

    internal static byte[] EncodeResync() => new[] { Resync };

    internal static bool IsResync(byte[] message) =>
        message != null && message.Length == 1 && message[0] == Resync;
}
