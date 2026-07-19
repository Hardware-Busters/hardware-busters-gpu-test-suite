using Xunit;

// ProfileAssetLocator is process-global by design; pack resolution tests must not race each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
