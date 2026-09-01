using EciCas.Host;

namespace EciCas.Tests.Agents;

public class ProfileStoreTests
{
    private static ProfileStore Create() =>
        new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

    [Fact]
    public void EmptyArchive_ListsNoProfiles()
    {
        Assert.Empty(Create().List());
    }

    [Fact]
    public void CreatedProfile_IsListedAndFoundBySlug()
    {
        var store = Create();

        var (profile, created) = store.Create("Ada Lovelace", "🐙");

        Assert.True(created);
        Assert.Equal("ada-lovelace", profile.Id);
        Assert.Equal("Ada Lovelace", profile.DisplayName);
        Assert.Equal(profile, Assert.Single(store.List()));
        Assert.Equal(profile, store.Find("ada-lovelace"));
    }

    [Fact]
    public void RepeatedName_ReturnsTheExistingProfileRatherThanASecondOne()
    {
        var store = Create();
        var (first, _) = store.Create("Daniel", "🦊");

        var (second, created) = store.Create("daniel", "🐝");

        Assert.False(created);
        Assert.Equal(first, second);
        Assert.Single(store.List());
    }

    [Fact]
    public void ProfilesAreListedOldestFirst()
    {
        var store = Create();
        store.Create("Daniel", "🦊");
        store.Create("Ada", "🐙");

        Assert.Equal(["daniel", "ada"], store.List().Select(profile => profile.Id));
    }

    [Fact]
    public void NameWithNoUsableCharacters_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create().Create("!!!", "🦊"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Daniel")]
    [InlineData("has space")]
    [InlineData("")]
    public void IdsOutsideTheSlugAlphabet_AreRejectedRatherThanSanitized(string id)
    {
        // These reach the store as path segments from a client, so the guard
        // is a reject, not a cleanup — Find must never resolve one.
        Assert.False(ProfileStore.IsValidId(id));
        Assert.Null(Create().Find(id));
    }

    [Fact]
    public void EachProfileOwnsItsOwnDirectory()
    {
        var store = Create();
        store.Create("Daniel", "🦊");
        store.Create("Ada", "🐙");

        Assert.NotEqual(store.DirectoryFor("daniel"), store.DirectoryFor("ada"));
        Assert.True(Directory.Exists(store.DirectoryFor("daniel")));
    }
}
