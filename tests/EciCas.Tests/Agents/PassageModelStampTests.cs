using EciCas.Agents.Passages;
using EciCas.Core;

namespace EciCas.Tests.Agents;

public class PassageModelStampTests
{
    private static float[] Unit(int axis)
    {
        var v = new float[4];
        v[axis] = 1f;
        return v;
    }

    [Fact]
    public void AgreementCheck_PassesWhenTheCorpusAndTheEmbedderMatch() =>
        PassageCorpus.EnsureModelAgreement(["onnx:models/embedding/model.onnx"], "onnx:models/embedding/model.onnx");

    [Fact]
    public void AgreementCheck_PassesOnAnEmptyCorpus() =>
        PassageCorpus.EnsureModelAgreement([], "onnx:whatever");

    [Fact]
    public void AgreementCheck_PassesWithNoEmbedderConfigured() =>
        // Nothing will search, so nothing can be silently mis-scored — and
        // a missing model file is a normal state, never a startup failure.
        PassageCorpus.EnsureModelAgreement(["onnx:a"], string.Empty);

    [Fact]
    public void AgreementCheck_RefusesWhenTheCorpusWasWrittenByAnotherModel()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PassageCorpus.EnsureModelAgreement(["onnx:old"], "onnx:new"));

        Assert.Contains("onnx:old", error.Message);
        Assert.Contains("onnx:new", error.Message);
    }

    [Fact]
    public async Task StoredPassages_RoundTripTheirModelStamp()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new ParquetPassageStore(dir);
            await store.WriteAsync(
                [new Passage("a", "a thought", [], DateTimeOffset.UtcNow, Unit(0), ModelId: "onnx:x")],
                null, CancellationToken.None);

            var reread = new ParquetPassageStore(dir);
            Assert.Equal(["onnx:x"], await reread.StampedModelsAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RowsWrittenBeforeTheStampExisted_AreNotTreatedAsDisagreeing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new ParquetPassageStore(dir);
            await store.WriteAsync(
                [new Passage("a", "an older thought", [], DateTimeOffset.UtcNow, Unit(0))],
                null, CancellationToken.None);

            var stamped = await new ParquetPassageStore(dir).StampedModelsAsync(CancellationToken.None);

            Assert.Empty(stamped);
            PassageCorpus.EnsureModelAgreement(stamped, "onnx:new");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
