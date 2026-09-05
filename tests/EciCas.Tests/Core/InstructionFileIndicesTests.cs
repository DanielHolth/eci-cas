using EciCas.Core;

namespace EciCas.Tests.Core;

/// <summary>
/// The picking parse. Recall and Librarian both route through this, and both
/// used to drop a reply that was correct but punctuated — silently, into an
/// empty list that reads exactly like a model declining to pick. These cases
/// are the ones a 4B actually produced.
/// </summary>
public class InstructionFileIndicesTests
{
    [Theory]
    [InlineData("0, 2", new[] { 0, 2 })]
    [InlineData("0,2", new[] { 0, 2 })]
    [InlineData("  1 ", new[] { 1 })]
    // Rows render as "0. ...", so a model told to copy the number copies the stop too.
    [InlineData("0., 2.", new[] { 0, 2 })]
    // A sentence ends somewhere.
    [InlineData("0, 2.", new[] { 0, 2 })]
    [InlineData("none", new int[0])]
    [InlineData("", new int[0])]
    // Prose is not an answer: guessing turns a visible failure into a plausible wrong pick.
    [InlineData("note 2", new int[0])]
    [InlineData("I think none are relevant", new int[0])]
    // Out of range survives the parse; the range check belongs to the caller,
    // which alone knows how many candidates it offered.
    [InlineData("99", new[] { 99 })]
    public void ReadsWhatAModelActuallyReplies(string response, int[] expected) =>
        Assert.Equal(expected, InstructionFile.Indices(response));

    [Fact]
    public void NullIsEmptyRatherThanAThrow() =>
        Assert.Empty(InstructionFile.Indices(null!));
}
