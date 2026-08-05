namespace CadTranslation.Core;

public sealed record BlockReferenceNode(
    string Handle,
    string TargetDefinitionId,
    Transform2 LocalTransform);

public sealed record BlockDefinitionNode(
    string DefinitionId,
    IReadOnlyList<BlockReferenceNode> References);

public sealed record BlockInstancePath(
    string DefinitionId,
    string Path,
    Transform2 WorldTransform);

public static class BlockInstanceExpander
{
    public static BlockInstancePath[] Expand(
        string rootDefinitionId,
        IReadOnlyDictionary<string, BlockDefinitionNode> definitions)
    {
        if (!definitions.ContainsKey(rootDefinitionId))
        {
            throw new ArgumentException("Root definition is missing.", nameof(rootDefinitionId));
        }

        var result = new List<BlockInstancePath>();
        ExpandDefinition(
            rootDefinitionId,
            rootDefinitionId,
            Transform2.Identity,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            result);
        return result.ToArray();
    }

    private static void ExpandDefinition(
        string definitionId,
        string path,
        Transform2 worldTransform,
        IReadOnlyDictionary<string, BlockDefinitionNode> definitions,
        ISet<string> activeDefinitions,
        ICollection<BlockInstancePath> result)
    {
        result.Add(new BlockInstancePath(definitionId, path, worldTransform));
        if (!activeDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            BlockDefinitionNode definition = definitions[definitionId];
            foreach (BlockReferenceNode reference in definition.References)
            {
                if (!definitions.ContainsKey(reference.TargetDefinitionId) ||
                    activeDefinitions.Contains(reference.TargetDefinitionId))
                {
                    continue;
                }

                ExpandDefinition(
                    reference.TargetDefinitionId,
                    $"{path}/{reference.TargetDefinitionId}[{reference.Handle}]",
                    worldTransform.Compose(reference.LocalTransform),
                    definitions,
                    activeDefinitions,
                    result);
            }
        }
        finally
        {
            activeDefinitions.Remove(definitionId);
        }
    }
}
