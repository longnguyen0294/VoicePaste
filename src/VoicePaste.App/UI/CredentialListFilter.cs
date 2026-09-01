using VoicePaste.Core;

namespace VoicePaste.App.UI;

public sealed record SavedCredentialListItem(
    string Reference,
    DateTimeOffset? LastWritten,
    bool IsActive,
    string DisplayText)
{
    public override string ToString() => DisplayText;
}

public static class CredentialListFilter
{
    public static IReadOnlyList<SavedCredentialListItem> Apply(
        IEnumerable<StoredCredentialDescriptor> credentials,
        string? searchText,
        string activeReference)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(activeReference))
        {
            throw new ArgumentException("An active credential reference is required.", nameof(activeReference));
        }
        var query = searchText?.Trim();
        return credentials
            .Where(item => string.IsNullOrEmpty(query) ||
                           item.Reference.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(item => CreateItem(item, activeReference))
            .ToArray();
    }

    private static SavedCredentialListItem CreateItem(
        StoredCredentialDescriptor descriptor,
        string activeReference)
    {
        var isActive = string.Equals(
            descriptor.Reference,
            activeReference,
            StringComparison.OrdinalIgnoreCase);
        var activeLabel = isActive ? " — active" : string.Empty;
        var savedLabel = descriptor.LastWritten is DateTimeOffset lastWritten
            ? $" — saved {lastWritten.ToLocalTime():yyyy-MM-dd HH:mm}"
            : string.Empty;
        return new SavedCredentialListItem(
            descriptor.Reference,
            descriptor.LastWritten,
            isActive,
            descriptor.Reference + activeLabel + savedLabel);
    }
}
