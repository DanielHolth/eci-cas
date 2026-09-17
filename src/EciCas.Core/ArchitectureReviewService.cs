namespace EciCas.Core;

public sealed record ArchitectureReview(
    IReadOnlyList<ArchitectureLayer> MissingLayers,
    IReadOnlyList<string> Findings)
{
    public bool HasFindings => MissingLayers.Count > 0 || Findings.Count > 0;
}

public interface IArchitectureReviewService
{
    ArchitectureReview Review();
}

public sealed class ArchitectureReviewService : IArchitectureReviewService
{
    private readonly IReadOnlyList<IArchitectureBoundary> _boundaries;

    public ArchitectureReviewService(IEnumerable<IArchitectureBoundary> boundaries)
    {
        _boundaries = boundaries.ToArray();
    }

    public ArchitectureReview Review()
    {
        var present = _boundaries.Select(b => b.Layer).ToHashSet();
        var missing = ArchitectureContract.RequiredLayers.Where(layer => !present.Contains(layer)).ToArray();

        var findings = new List<string>();
        foreach (var boundary in _boundaries)
        {
            var rule = ArchitectureContract.RuleFor(boundary.Layer);
            var violations = rule.ForbiddenDependencies.Where(dep => present.Contains(dep)).Select(dep =>
                $"{boundary.Layer} depends on {dep}; this violates the split-layer contract and should be reviewed.");

            findings.AddRange(violations);
        }

        return new ArchitectureReview(missing, findings);
    }
}
