namespace GpuSuite.Authoring;

/// <summary>
/// Narrow file-operation seam used by multi-file authoring transactions.  Keeping it small makes
/// failure paths testable without weakening the normal draft-root and reparse-point checks.
/// </summary>
public interface IAuthoringFileOperations
{
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] contents);
    void Move(string source, string destination, bool overwrite);
    void Delete(string path);
}

internal sealed class SystemAuthoringFileOperations : IAuthoringFileOperations
{
    public bool Exists(string path) => File.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);
    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void Delete(string path) => File.Delete(path);
}
