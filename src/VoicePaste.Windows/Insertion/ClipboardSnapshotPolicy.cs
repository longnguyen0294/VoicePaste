using System.Text;
using System.IO;
using VoicePaste.Core;

namespace VoicePaste.Windows.Insertion;

public sealed record ClipboardSnapshot(
    IReadOnlyDictionary<string, object> Values,
    long SizeBytes);

public static class ClipboardSnapshotPolicy
{
    public const long MaximumSnapshotBytes = 16L * 1024 * 1024;

    public static OperationResult<ClipboardSnapshot> Create(
        IEnumerable<KeyValuePair<string, object?>> values)
    {
        var snapshot = new Dictionary<string, object>(StringComparer.Ordinal);
        long size = 0;
        foreach (var pair in values)
        {
            if (pair.Value is null)
            {
                continue;
            }

            object materialized;
            long itemSize;
            switch (pair.Value)
            {
                case string text:
                    materialized = text;
                    itemSize = Encoding.Unicode.GetByteCount(text);
                    break;
                case byte[] bytes:
                    materialized = bytes.ToArray();
                    itemSize = bytes.LongLength;
                    break;
                case MemoryStream stream when stream.Length <= MaximumSnapshotBytes:
                    materialized = stream.ToArray();
                    itemSize = stream.Length;
                    break;
                default:
                    return Unsupported(pair.Key);
            }

            size = checked(size + itemSize);
            if (size > MaximumSnapshotBytes)
            {
                return OperationResult.Failure<ClipboardSnapshot>(new OperationError(
                    ErrorCategory.ClipboardBusy,
                    "clipboard.snapshot_too_large",
                    IsRetryable: false,
                    "snapshot_above_16_mib"));
            }

            snapshot[pair.Key] = materialized;
        }

        return OperationResult.Success(new ClipboardSnapshot(snapshot, size));
    }

    private static OperationResult<ClipboardSnapshot> Unsupported(string format) =>
        OperationResult.Failure<ClipboardSnapshot>(new OperationError(
            ErrorCategory.ClipboardBusy,
            "clipboard.unsupported_snapshot",
            IsRetryable: false,
            $"unsupported_format:{format}"));
}
