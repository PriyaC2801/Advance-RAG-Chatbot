namespace AdvancedRag.Core.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Folder holding documents/, data/ and eval/. Relative paths are resolved against the API's content root.</summary>
    public string RootDirectory { get; set; } = "../..";
}

/// <summary>Where each pipeline finds its PDF, keeps its generated files and reads its evaluation cases.</summary>
public sealed class StoragePaths(Microsoft.Extensions.Options.IOptions<StorageOptions> options, IWebHostEnvironment environment)
{
    public string Root => Path.GetFullPath(options.Value.RootDirectory, environment.ContentRootPath);

    public string Document(string fileName) => Path.Combine(Root, "documents", fileName);

    /// <summary>Generated files of one pipeline (chunks.json, embeddings.json, caches). Created on demand.</summary>
    public string Data(string documentId)
    {
        var path = Path.Combine(Root, "data", documentId);
        Directory.CreateDirectory(path);
        return path;
    }

    public string Eval(string fileName) => Path.Combine(Root, "eval", fileName);

    /// <summary>Saved evaluation reports. Created on demand.</summary>
    public string EvalResults()
    {
        var path = Path.Combine(Root, "eval", "results");
        Directory.CreateDirectory(path);
        return path;
    }
}
